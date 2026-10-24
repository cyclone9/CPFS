using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Cpfs.Core.Monitor;

namespace Cpfs;

// activity-log.txt beside cpfs-log.txt: Monitor's Recent activity, written only while the user has switched it on.
// It holds file names, so nothing here goes to AppLog (counts only) and Copy report never reads it.
internal static class ActivityLog
{
    private static readonly object Gate = new();
    private static Task _queue = Task.CompletedTask;

    public static string? FilePath =>
        AppLog.FilePath is { } log && Path.GetDirectoryName(log) is { Length: > 0 } dir ? Path.Combine(dir, ActivityLogFormat.FileName) : null;

    public static bool Exists()
    {
        try
        {
            return FilePath is { } path && File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    // Queues the rows and returns at once; one batch at a time, in order, off the UI thread.
    public static void Append(IReadOnlyList<ActivityRow> rows)
    {
        if (rows.Count == 0 || FilePath is not { } path)
        {
            return;
        }

        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.Append(ActivityLogFormat.Line(row with { Time = row.Time.ToLocalTime() })).Append("\r\n");
        }

        var lines = text.ToString();
        var count = rows.Count;
        lock (Gate)
        {
            _queue = _queue.ContinueWith(_ => Write(path, lines, count), TaskScheduler.Default);
        }
    }

    private static void Write(string path, string lines, int count)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(lines);
            var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (ActivityLogFormat.ShouldRotate(size, bytes.Length))
            {
                var old = Path.Combine(Path.GetDirectoryName(path)!, ActivityLogFormat.OldFileName);
                File.Delete(old);
                File.Move(path, old);
                size = 0;
            }

            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            if (size == 0 && fs.Length == 0)
            {
                var bom = Encoding.UTF8.GetPreamble();
                fs.Write(bom, 0, bom.Length);
            }

            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // The type only: Windows' message can name the file.
            AppLog.Write("monitor", "Activity log: " + count.ToString(CultureInfo.InvariantCulture) + " lines not written (" + ex.GetType().Name + ")");
        }
    }
}
