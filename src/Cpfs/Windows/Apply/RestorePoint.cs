using System;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Threading;
using Cpfs.Core.Apply;
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

// NewestName is the newest point's Description, as System Restore lists it.
internal sealed record RestorePointResult(RestorePointOutcome Outcome, DateTimeOffset? Newest, string? Error, string? NewestName = null);

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
            var (beforeSequence, _, _, _) = Newest();
            var callStart = DateTimeOffset.Now;
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

            var (afterSequence, newest, raw, name) = Newest();
            AppLog.Write("apply", "Restore point: return " + code.ToString(CultureInfo.InvariantCulture) + ", newest sequence "
                + (beforeSequence?.ToString(CultureInfo.InvariantCulture) ?? "none") + " -> " + (afterSequence?.ToString(CultureInfo.InvariantCulture) ?? "none")
                + ", call started " + CimDateTime.Iso(callStart));
            var created = RestorePointRules.ShowsNewPoint(beforeSequence, afterSequence, newest, callStart);

            // The list can lag behind a point the call did create (owner's laptop, 0.9.0 dev4).
            if (!created && code == 0)
            {
                var waited = Stopwatch.StartNew();
                var reads = 0;
                while (!created && waited.Elapsed < RestorePointRules.RereadLimit)
                {
                    Thread.Sleep(RestorePointRules.RereadStep);
                    (afterSequence, newest, raw, name) = Newest();
                    reads++;
                    created = RestorePointRules.ShowsNewPoint(beforeSequence, afterSequence, newest, callStart);
                }

                AppLog.Write("apply", "Restore point: read the list again " + reads.ToString(CultureInfo.InvariantCulture) + " times over "
                    + waited.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s: " + (created ? "a new point appeared" : "no new point")
                    + ", newest sequence " + (afterSequence?.ToString(CultureInfo.InvariantCulture) ?? "none") + ", CreationTime " + (raw ?? "none"));
            }

            if (created)
            {
                AppLog.Write("apply", "Restore point: created, sequence " + (afterSequence?.ToString(CultureInfo.InvariantCulture) ?? "none")
                    + ", CreationTime " + (raw ?? "none"));
                return new RestorePointResult(RestorePointOutcome.Created, newest, null);
            }

            if (code != 0)
            {
                return new RestorePointResult(RestorePointOutcome.Unavailable, newest, "0x" + code.ToString("X", CultureInfo.InvariantCulture));
            }

            AppLog.Write("apply", "Restore point: skipped by Windows; newest existing point "
                + (newest is { } n ? CimDateTime.Iso(n) + " (CreationTime " + raw + ", Description \"" + name + "\")" : "none (System Protection may be off)"));
            return new RestorePointResult(RestorePointOutcome.Skipped, newest, null, name);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Write("apply", "Restore point: " + ex.GetType().Name + ": " + ex.Message);
            return new RestorePointResult(RestorePointOutcome.Unavailable, null, ex.Message);
        }
    }

    // SystemRestore: SequenceNumber, CreationTime and Description (Learn, SystemRestore class).
    private static (uint? Sequence, DateTimeOffset? Created, string? Raw, string? Name) Newest()
    {
        uint? sequence = null;
        DateTimeOffset? created = null;
        string? raw = null;
        string? name = null;
        foreach (var point in Wmi.All(Scope, "SELECT SequenceNumber, CreationTime, Description FROM SystemRestore"))
        {
            using (point)
            {
                var n = Convert.ToUInt32(StateReader.Prop(point, "SequenceNumber") ?? 0u, CultureInfo.InvariantCulture);
                if (sequence is null || n > sequence)
                {
                    sequence = n;
                    raw = StateReader.Prop(point, "CreationTime") as string;
                    created = CimDateTime.TryParse(raw, out var t) ? t : null;
                    name = StateReader.Prop(point, "Description") as string;
                }
            }
        }

        return (sequence, created, raw, name);
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
