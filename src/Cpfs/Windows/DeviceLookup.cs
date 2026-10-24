using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Cpfs.Core.Monitor;

namespace Cpfs.Windows;

// What the Monitor can learn about a connected device besides its IP: the MAC in the neighbor table, a network
// name from a reverse lookup, and the name the owner gave it. SMB itself carries neither a model nor a name.
internal static class DeviceLookup
{
    private const uint ErrorNotFound = 1168;

    public static string DevicesPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CPFS", "devices.json");

    // Normalized address to MAC, IPv4 (ARP) and IPv6 (neighbor discovery) together. Reading needs no rights.
    public static Dictionary<string, string>? ReadNeighbors()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var status = Native.GetIpNetTable2(Native.AfUnspec, out var table);
        if (status == ErrorNotFound)
        {
            return result;
        }

        if (status != 0)
        {
            throw new Win32Exception((int)status);
        }

        try
        {
            var count = Marshal.ReadInt32(table);
            var row = new byte[DeviceIdentity.NeighborRowSize];
            for (var i = 0; i < count && i < 65536; i++)
            {
                Marshal.Copy(IntPtr.Add(table, DeviceIdentity.NeighborTableHeader + (i * DeviceIdentity.NeighborRowSize)), row, 0, row.Length);
                if (DeviceIdentity.TryParseNeighborRow(row, out var address, out var mac) && !result.ContainsKey(address))
                {
                    result[address] = mac;
                }
            }
        }
        finally
        {
            Native.FreeMibTable(table);
        }

        return result;
    }

    public static DeviceNameStore LoadNames()
    {
        try
        {
            if (!File.Exists(DevicesPath))
            {
                return new DeviceNameStore();
            }

            var store = DeviceNameStore.Parse(File.ReadAllText(DevicesPath, Encoding.UTF8));
            AppLog.Write("devices", "Loaded " + store.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " device name(s) from " + DevicesPath);
            return store;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            AppLog.Write("devices", "Could not read " + DevicesPath + ": " + ex.GetType().Name + ": " + ex.Message);
            return new DeviceNameStore();
        }
    }

    // Null when saved, else the reason.
    public static string? SaveNames(DeviceNameStore store)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DevicesPath)!);
            var temp = DevicesPath + ".tmp";
            File.WriteAllText(temp, store.Serialize(), new UTF8Encoding(false));
            if (File.Exists(DevicesPath))
            {
                File.Replace(temp, DevicesPath, null);
            }
            else
            {
                File.Move(temp, DevicesPath);
            }

            AppLog.Write("devices", "Saved " + DevicesPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            AppLog.Write("devices", "Could not save " + DevicesPath + ": " + Core.Apply.StepErrors.ForLog(ex));
            return UiErrors.ForUser(ex);
        }
    }
}

// Network names per client address for one Monitor session. Get is called on the UI thread and never waits: an
// unknown address starts a lookup on the thread pool, and Resolved is raised on the UI thread when it ends.
internal sealed class HostNameCache
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, string?> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Resolved;

    public string? Get(string address)
    {
        if (_names.TryGetValue(address, out var name))
        {
            return name;
        }

        if (_pending.Add(address))
        {
            _ = ResolveAsync(address);
        }

        return null;
    }

    public void Clear() => _names.Clear();

    private async Task ResolveAsync(string address)
    {
        string? name = null;
        string how;
        try
        {
            (name, how) = await Task.Run(() => Lookup(address)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            how = ex.GetType().Name + ": " + ex.Message;
        }

        _pending.Remove(address);
        _names[address] = name;
        AppLog.Write("devices", "Network name for " + address + ": " + (name ?? "no name") + " (" + how + ")");
        if (name is not null)
        {
            Resolved?.Invoke(this, EventArgs.Empty);
        }
    }

    // Reverse lookup through the Windows resolver (DNS PTR, plus whatever else Windows uses for it), cut off after
    // about a second; a late answer is dropped.
    private static (string? Name, string How) Lookup(string address)
    {
        if (!IPAddress.TryParse(address, out var ip))
        {
            return (null, "not an IP address");
        }

        var watch = Stopwatch.StartNew();
        var task = Dns.GetHostEntryAsync(ip);
        try
        {
            if (!task.Wait(Timeout))
            {
                _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                return (null, "reverse lookup timed out after " + StateReader.Ms(watch));
            }

            var name = DeviceIdentity.CleanHostName(task.Result.HostName, address);
            return (name, "reverse lookup, " + StateReader.Ms(watch));
        }
        catch (AggregateException ex) when (ex.InnerException is SocketException or ArgumentException)
        {
            var inner = ex.InnerException!;
            var code = inner is SocketException s ? " " + s.ErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            return (null, "reverse lookup: " + inner.GetType().Name + code + ", " + StateReader.Ms(watch));
        }
    }
}
