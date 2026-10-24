using System;
using System.Collections.Generic;

namespace Cpfs.Core.Monitor;

// One row of Monitor's open files: still open, or closed and kept for a few seconds so a quick transfer can be seen.
public sealed record RecentFile<T>(string Key, T Item, bool Closed);

// The open files of each reading, plus each file that closed within the last Keep, marked closed. Open files come
// first in the order they were first seen, then closed ones, newest close first; at most Max rows.
public sealed class RecentFiles<T>
{
    private readonly List<Entry> _entries = new();

    public RecentFiles(TimeSpan keep, int max)
    {
        Keep = keep;
        Max = max;
    }

    public TimeSpan Keep { get; }

    public int Max { get; }

    // now: a monotonic clock (time since Monitor started), so a change of the PC's clock moves nothing.
    public IReadOnlyList<RecentFile<T>> Update(IEnumerable<KeyValuePair<string, T>> open, TimeSpan now)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in open)
        {
            if (!seen.Add(pair.Key))
            {
                continue;
            }

            var entry = _entries.Find(e => string.Equals(e.Key, pair.Key, StringComparison.Ordinal));
            if (entry is null)
            {
                _entries.Add(new Entry(pair.Key, pair.Value));
            }
            else
            {
                entry.Item = pair.Value;
                entry.ClosedAt = null;
            }
        }

        foreach (var entry in _entries)
        {
            if (entry.ClosedAt is null && !seen.Contains(entry.Key))
            {
                entry.ClosedAt = now;
            }
        }

        _entries.RemoveAll(e => e.ClosedAt is { } at && now - at >= Keep);

        var openRows = _entries.FindAll(e => e.ClosedAt is null);
        var closedRows = _entries.FindAll(e => e.ClosedAt is not null);
        closedRows.Sort((a, b) => b.ClosedAt!.Value.CompareTo(a.ClosedAt!.Value));

        // Past Max, the oldest closed rows go first; open files beyond Max are only not shown.
        if (openRows.Count + closedRows.Count > Max)
        {
            var closedRoom = Math.Max(0, Max - openRows.Count);
            foreach (var dropped in closedRows.GetRange(closedRoom, closedRows.Count - closedRoom))
            {
                _entries.Remove(dropped);
            }

            closedRows.RemoveRange(closedRoom, closedRows.Count - closedRoom);
        }

        var rows = new List<RecentFile<T>>(Math.Min(Max, openRows.Count + closedRows.Count));
        foreach (var e in openRows)
        {
            if (rows.Count == Max)
            {
                break;
            }

            rows.Add(new RecentFile<T>(e.Key, e.Item, false));
        }

        foreach (var e in closedRows)
        {
            rows.Add(new RecentFile<T>(e.Key, e.Item, true));
        }

        return rows;
    }

    public void Clear() => _entries.Clear();

    private sealed class Entry
    {
        public Entry(string key, T item)
        {
            Key = key;
            Item = item;
        }

        public string Key { get; }

        public T Item { get; set; }

        public TimeSpan? ClosedAt { get; set; }
    }
}
