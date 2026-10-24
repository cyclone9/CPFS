using System;
using System.Globalization;

namespace Cpfs.Core.Monitor;

// The lines of activity-log.txt, the user's own record of Recent activity. It holds file names, so it stays apart
// from cpfs-log.txt and is never part of Copy report.
public static class ActivityLogFormat
{
    public const string FileName = "activity-log.txt";
    public const string OldFileName = "activity-log.old.txt";
    public const long RotateAt = 5 * 1024 * 1024;

    // "2026-10-24T14:45:09+03:30<TAB>Added<TAB>Photos\a.jpg", in the row's own offset (the caller passes local time).
    // The change is the English word whatever the UI language, so the file reads the same everywhere.
    public static string Line(ActivityRow row)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        var what = row.Kind switch
        {
            ActivityKind.Missed => "(some changes were not reported: too many arrived at once)",
            ActivityKind.Renamed => row.OldPath + " → " + row.Path,
            _ => row.Path,
        };
        return row.Time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) + "\t" + row.Kind + "\t" + what;
    }

    // The file moves to the .old name before a write that would take it past the limit; an empty file never does.
    public static bool ShouldRotate(long currentSize, long nextBytes, long limit = RotateAt) =>
        currentSize > 0 && currentSize + nextBytes > limit;
}
