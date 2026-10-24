using System;
using System.Globalization;
using System.Management;
using Cpfs.Core.Text;

namespace Cpfs.Windows.Apply;

internal enum RestorePointOutcome
{
    Created,

    // The call succeeded but no new point appeared: Windows skips one within 24 hours of the last
    // (SystemRestorePointCreationFrequency), or System Protection is off.
    Skipped,

    // The call failed (System Protection off on the system drive, or no System Restore on this edition).
    Unavailable,
}

internal sealed record RestorePointResult(RestorePointOutcome Outcome, DateTimeOffset? Newest, string? Error);

// A System Restore point before CPFS changes Windows (owner decision 10): a second way back next to the backup in
// config.json. It never switches System Protection on and never changes the frequency setting.
internal static class RestorePoint
{
    private const string Scope = @"\\.\root\default";

    // RestorePointType 12 MODIFY_SETTINGS, EventType 100 BEGIN_SYSTEM_CHANGE [Learn: CreateRestorePoint method of
    // the SystemRestore class]; the method returns 0 also when it skips the point, so the list is read again.
    public static RestorePointResult Create(string description)
    {
        try
        {
            var (beforeSequence, _, _) = Newest();
            uint code;
            using (var type = new ManagementClass(new ManagementScope(Scope), new ManagementPath("SystemRestore"), null))
            using (var args = type.GetMethodParameters("CreateRestorePoint"))
            {
                Wmi.Set(args, "Description", description);
                Wmi.Set(args, "RestorePointType", 12);
                Wmi.Set(args, "EventType", 100);
                using var result = type.InvokeMethod("CreateRestorePoint", args, null);
                code = Convert.ToUInt32(result?["ReturnValue"] ?? 0u, CultureInfo.InvariantCulture);
            }

            var (afterSequence, newest, raw) = Newest();
            AppLog.Write("apply", "Restore point: return " + code.ToString(CultureInfo.InvariantCulture) + ", newest sequence "
                + (beforeSequence?.ToString(CultureInfo.InvariantCulture) ?? "none") + " -> " + (afterSequence?.ToString(CultureInfo.InvariantCulture) ?? "none"));
            if (afterSequence is { } a && (beforeSequence is null || a > beforeSequence))
            {
                return new RestorePointResult(RestorePointOutcome.Created, newest, null);
            }

            if (code != 0)
            {
                return new RestorePointResult(RestorePointOutcome.Unavailable, newest, "0x" + code.ToString("X", CultureInfo.InvariantCulture));
            }

            AppLog.Write("apply", "Restore point: skipped by Windows; newest existing point "
                + (newest is { } n ? CimDateTime.Iso(n) + " (CreationTime " + raw + ")" : "none (System Protection may be off)"));
            return new RestorePointResult(RestorePointOutcome.Skipped, newest, null);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Write("apply", "Restore point: " + ex.GetType().Name + ": " + ex.Message);
            return new RestorePointResult(RestorePointOutcome.Unavailable, null, ex.Message);
        }
    }

    private static (uint? Sequence, DateTimeOffset? Created, string? Raw) Newest()
    {
        uint? sequence = null;
        DateTimeOffset? created = null;
        string? raw = null;
        foreach (var point in Wmi.All(Scope, "SELECT SequenceNumber, CreationTime FROM SystemRestore"))
        {
            using (point)
            {
                var n = Convert.ToUInt32(StateReader.Prop(point, "SequenceNumber") ?? 0u, CultureInfo.InvariantCulture);
                if (sequence is null || n > sequence)
                {
                    sequence = n;
                    raw = StateReader.Prop(point, "CreationTime") as string;
                    created = CimDateTime.TryParse(raw, out var t) ? t : null;
                }
            }
        }

        return (sequence, created, raw);
    }
}

internal static class ConfigStore
{
    // Atomic: a temporary file next to config.json, flushed to disk, then swapped in (File.Replace keeps the
    // existing file's permissions). The temporary file is never written through a link.
    public static void Save(byte[] bytes)
    {
        var path = StateReader.ConfigPath;
        var temp = path + ".tmp";
        using (var stream = PlainFiles.OpenWrite(temp, append: false))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        if (System.IO.File.Exists(path))
        {
            System.IO.File.Replace(temp, path, null);
        }
        else
        {
            System.IO.File.Move(temp, path);
        }
    }
}
