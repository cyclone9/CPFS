using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Threading;
using Cpfs.Core.Monitor;

namespace Cpfs.Windows;

internal sealed record SessionInfo(ulong Id, string Client, bool Vpn, string User, string Dialect, ulong OpenFiles, long SecondsExists, long SecondsIdle);

internal sealed record OpenFileInfo(ulong Id, string User, string Client, string Path);

// One part of a reading: its value, or why it could not be read.
internal sealed class Part<T>
    where T : class
{
    public T? Value { get; init; }

    public ReadStatus Status { get; init; } = ReadStatus.Ok;

    public string? Error { get; init; }

    public static Part<T> Of(Func<T?> read, string what)
    {
        try
        {
            var value = read();
            return value is null ? new Part<T> { Status = ReadStatus.Missing } : new Part<T> { Value = value };
        }
        catch (Exception ex) when (State.IsExpected(ex) || ex is EventLogException or FormatException)
        {
            var denied = State.IsAccessDenied(ex) || ex is EventLogException { InnerException: UnauthorizedAccessException };
            AppLog.Write("monitor", what + ": " + ex.GetType().Name + ": " + ex.Message);
            return new Part<T> { Status = denied ? ReadStatus.NeedsAdmin : ReadStatus.Failed, Error = ex.GetType().Name + ": " + ex.Message };
        }
    }
}

internal sealed class ServerTotals
{
    // Down is PC to device (bytes the server sent), up is device to PC, as the 0.2.3 Monitor named them.
    public double? Down { get; init; }

    public double? Up { get; init; }

    public double? LogonErrors { get; init; }
}

internal sealed class MonitorSnapshot
{
    public long Timestamp { get; } = Stopwatch.GetTimestamp();

    public DateTime Taken { get; } = DateTime.Now;

    public Part<List<SessionInfo>> Sessions { get; init; } = new();

    public Part<List<OpenFileInfo>> Files { get; init; } = new();

    public Part<ServerTotals> Server { get; init; } = new();

    // Share name to (down, up) running totals; Missing when this Windows has no per-share counters.
    public Part<Dictionary<string, (double Down, double Up)>> Shares { get; init; } = new();

    // Normalized client address to MAC, from the neighbor table; read only while a device is connected.
    public Part<Dictionary<string, string>> Macs { get; init; } = new();
}

internal enum DisconnectResult
{
    Done,
    Gone,
    Failed,
}

// The Monitor's reads: SMB sessions and open files (root\Microsoft\Windows\SMB), the SMB server performance data
// (raw WMI performance classes, found by class name at run time because counter display names are localized) and
// failed network logons from the Security log. Its one write is Disconnect.
internal static class SmbMonitor
{
    private const string SmbScope = @"root\Microsoft\Windows\SMB";
    private const string CimScope = @"root\cimv2";

    private static readonly Lazy<(string? Server, string? Shares)> PerfClasses = new(FindPerfClasses);

    public static MonitorSnapshot Read()
    {
        var sessions = Part<List<SessionInfo>>.Of(ReadSessions, "sessions");
        return new()
        {
            Sessions = sessions,
            Files = Part<List<OpenFileInfo>>.Of(ReadFiles, "open files"),
            Server = Part<ServerTotals>.Of(ReadServer, "server counters"),
            Shares = Part<Dictionary<string, (double, double)>>.Of(ReadShares, "share counters"),
            Macs = sessions.Value is { Count: > 0 } ? Part<Dictionary<string, string>>.Of(DeviceLookup.ReadNeighbors, "neighbor table") : new(),
        };
    }

    public static Part<List<FailedLogon>> ReadFailedLogons(int max) => Part<List<FailedLogon>>.Of(() =>
    {
        var list = new List<FailedLogon>();
        var query = new EventLogQuery("Security", PathType.LogName, FailedLogon.Last24HoursQuery) { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        while (list.Count < max)
        {
            using var record = reader.ReadEvent();
            if (record is null)
            {
                break;
            }

            list.Add(FailedLogon.Parse(record.ToXml()));
        }

        return list;
    }, "failed logons");

    // Closes one SMB session (MSFT_SmbSession.ForceClose): the client is disconnected and its open handles closed.
    // It changes no setting, and the client may connect again. The session is looked up again first, so an id that
    // has ended (or been reused by another client) is not closed.
    public static DisconnectResult Disconnect(SessionInfo session, out string? error)
    {
        error = null;
        AppLog.Write("monitor", "Disconnect requested: session " + session.Id.ToString(CultureInfo.InvariantCulture)
            + ", client " + session.Client + ", user " + session.User);
        try
        {
            var wql = "SELECT * FROM MSFT_SmbSession WHERE SessionId = " + session.Id.ToString(CultureInfo.InvariantCulture);
            using var searcher = new ManagementObjectSearcher(new ManagementScope(SmbScope), new ObjectQuery(wql));
            using var results = searcher.Get();
            foreach (ManagementObject found in results)
            {
                using (found)
                {
                    var client = MonitorMath.ClientAddress(found["ClientComputerName"] as string);
                    var user = MonitorMath.UserName(found["ClientUserName"] as string);
                    if (!string.Equals(client, session.Client, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(user, session.User, StringComparison.OrdinalIgnoreCase))
                    {
                        AppLog.Write("monitor", "Disconnect skipped: session id now belongs to " + client + " / " + user);
                        return DisconnectResult.Gone;
                    }

                    // The (name, object[]) overload copies out parameters back into the args array and throws on a
                    // null one; ForceClose has out parameters on real systems although the docs list none.
                    using var output = found.InvokeMethod("ForceClose", null, null);
                    var code = MonitorMath.ReturnCode(output is null ? null : Prop(output, "ReturnValue"));
                    AppLog.Write("monitor", "MSFT_SmbSession.ForceClose output: " + DescribeOutput(output)
                        + "; ReturnValue " + (code?.ToString(CultureInfo.InvariantCulture) ?? "absent"));
                    switch (MonitorMath.JudgeDisconnect(code, () => WaitUntilGone(session)))
                    {
                        case DisconnectVerdict.Done:
                            return DisconnectResult.Done;
                        case DisconnectVerdict.StillConnected:
                            error = "the session is still connected";
                            return DisconnectResult.Failed;
                        default:
                            error = "ForceClose returned " + code?.ToString(CultureInfo.InvariantCulture);
                            return DisconnectResult.Failed;
                    }
                }
            }

            AppLog.Write("monitor", "Disconnect: the session had already ended");
            return DisconnectResult.Gone;
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            error = ex.GetType().Name + ": " + ex.Message;
            AppLog.Write("monitor", "Disconnect failed: " + error);
            return DisconnectResult.Failed;
        }
    }

    // Property names and CIM types only: values could carry a user or client name.
    private static string DescribeOutput(ManagementBaseObject? output)
    {
        if (output is null)
        {
            return "no object";
        }

        var parts = new List<string>();
        foreach (var p in output.Properties)
        {
            parts.Add(p.Name + " (" + p.Type.ToString() + (p.IsArray ? "[]" : "") + ")");
        }

        return parts.Count == 0 ? "no properties" : string.Join(", ", parts);
    }

    // Polls for up to 2 s; runs on the Disconnect worker thread, never the UI thread.
    private static bool WaitUntilGone(SessionInfo session)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (!SessionPresent(session))
            {
                AppLog.Write("monitor", "Disconnect verified gone in " + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
                return true;
            }

            if (watch.ElapsedMilliseconds >= 2000)
            {
                AppLog.Write("monitor", "Disconnect check: the session is still connected after " + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
                return false;
            }

            Thread.Sleep(250);
        }
    }

    // A session id reused by another client counts as gone: the one that was closed is.
    private static bool SessionPresent(SessionInfo session)
    {
        var present = false;
        foreach (var s in Query(SmbScope, "SELECT SessionId, ClientComputerName, ClientUserName FROM MSFT_SmbSession WHERE SessionId = " + session.Id.ToString(CultureInfo.InvariantCulture)))
        {
            using (s)
            {
                present |= string.Equals(MonitorMath.ClientAddress(Prop(s, "ClientComputerName") as string), session.Client, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(MonitorMath.UserName(Prop(s, "ClientUserName") as string), session.User, StringComparison.OrdinalIgnoreCase);
            }
        }

        return present;
    }

    private static List<SessionInfo> ReadSessions()
    {
        var list = new List<SessionInfo>();
        foreach (var s in Query(SmbScope, "SELECT SessionId, ClientComputerName, ClientUserName, Dialect, NumOpens, SecondsExists, SecondsIdle FROM MSFT_SmbSession"))
        {
            using (s)
            {
                // Sessions without a user are anonymous probes (share listing before sign-in); 0.2.3 left them out too.
                var user = Prop(s, "ClientUserName") as string;
                if (string.IsNullOrEmpty(user))
                {
                    continue;
                }

                var client = MonitorMath.ClientAddress(Prop(s, "ClientComputerName") as string);
                list.Add(new SessionInfo(
                    ULong(Prop(s, "SessionId")),
                    client,
                    MonitorMath.IsVpnClient(client),
                    MonitorMath.UserName(user),
                    Prop(s, "Dialect") as string ?? "",
                    ULong(Prop(s, "NumOpens")),
                    (long)ULong(Prop(s, "SecondsExists")),
                    (long)ULong(Prop(s, "SecondsIdle"))));
            }
        }

        return list;
    }

    private static List<OpenFileInfo> ReadFiles()
    {
        var list = new List<OpenFileInfo>();
        foreach (var f in Query(SmbScope, "SELECT FileId, ShareRelativePath, ClientComputerName, ClientUserName FROM MSFT_SmbOpenFile"))
        {
            using (f)
            {
                // Handles on the share root itself (no relative path) are folder views, not files.
                var path = Prop(f, "ShareRelativePath") as string;
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                list.Add(new OpenFileInfo(
                    ULong(Prop(f, "FileId")),
                    MonitorMath.UserName(Prop(f, "ClientUserName") as string),
                    MonitorMath.ClientAddress(Prop(f, "ClientComputerName") as string),
                    path!));
            }
        }

        return list;
    }

    private static ServerTotals? ReadServer()
    {
        var name = PerfClasses.Value.Server;
        if (name is null)
        {
            return null;
        }

        foreach (var o in Query(CimScope, "SELECT * FROM " + name))
        {
            using (o)
            {
                return new ServerTotals
                {
                    Down = Number(Prop(o, "BytesTransmittedPersec")),
                    Up = Number(Prop(o, "BytesReceivedPersec")),
                    LogonErrors = Number(Prop(o, "ErrorsLogon")),
                };
            }
        }

        return null;
    }

    private static Dictionary<string, (double, double)>? ReadShares()
    {
        var name = PerfClasses.Value.Shares;
        if (name is null)
        {
            return null;
        }

        var result = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in Query(CimScope, "SELECT * FROM " + name))
        {
            using (o)
            {
                var share = MonitorMath.ShareFromInstance(Prop(o, "Name") as string);
                if (share is not null)
                {
                    result[share] = (Number(Prop(o, "ReadBytesPersec")) ?? 0, Number(Prop(o, "WriteBytesPersec")) ?? 0);
                }
            }
        }

        return result;
    }

    // The raw performance classes are subclasses of Win32_PerfRawData; their names come from the counter set's
    // registry name, so they are matched by name here, never by the localized display name.
    private static (string? Server, string? Shares) FindPerfClasses()
    {
        var watch = Stopwatch.StartNew();
        string? server = null;
        string? shares = null;
        try
        {
            using var root = new ManagementClass(new ManagementScope(CimScope), new ManagementPath("Win32_PerfRawData"), null);
            using var subclasses = root.GetSubclasses(new EnumerationOptions { EnumerateDeep = true, ReturnImmediately = true });
            foreach (var c in subclasses)
            {
                using (c)
                {
                    var name = c["__CLASS"] as string ?? "";
                    if (server is null && name.EndsWith("_PerfNet_Server", StringComparison.OrdinalIgnoreCase))
                    {
                        server = name;
                    }
                    else if (shares is null && name.IndexOf("SMBServerShares", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        shares = name;
                    }
                }
            }
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            AppLog.Write("monitor", "Performance classes: " + ex.GetType().Name + ": " + ex.Message);
        }

        AppLog.Write("monitor", "Performance classes: server " + (server ?? "none") + ", shares " + (shares ?? "none") + " in " + StateReader.Ms(watch));
        return (server, shares);
    }

    private static List<ManagementBaseObject> Query(string scope, string wql)
    {
        var list = new List<ManagementBaseObject>();
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));
        using var results = searcher.Get();
        foreach (var item in results)
        {
            list.Add(item);
        }

        return list;
    }

    private static object? Prop(ManagementBaseObject obj, string name)
    {
        try
        {
            return obj[name];
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotFound)
        {
            return null;
        }
    }

    private static ulong ULong(object? value) => value is null ? 0 : Convert.ToUInt64(value, CultureInfo.InvariantCulture);

    private static double? Number(object? value) => value is null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
}
