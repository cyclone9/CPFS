using System;

namespace Cpfs.Core.Apply;

// Whether System Restore's list shows the point CreateRestorePoint was asked for. The call returns 0 also when Windows
// skips the point, and on the owner's laptop (0.9.0 dev4) a created point was missing from the list read right after
// the call returned, so the app re-reads it for a while before it calls the point skipped.
public static class RestorePointRules
{
    // CreationTime and the clock read before the call can differ by a little.
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan RereadLimit = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan RereadStep = TimeSpan.FromSeconds(1);

    // A newer sequence number, or a newest point created at or after the call began (less the slack).
    public static bool ShowsNewPoint(uint? sequenceBefore, uint? sequenceNow, DateTimeOffset? newestCreated, DateTimeOffset callStart)
    {
        if (sequenceNow is { } now && (sequenceBefore is null || now > sequenceBefore))
        {
            return true;
        }

        return newestCreated is { } created && created >= callStart - Slack;
    }
}
