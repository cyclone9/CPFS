using System;
using System.Collections.Generic;

namespace Cpfs.Core.Monitor;

public enum ActivityKind
{
    Added,
    Changed,
    Renamed,
    Deleted,

    // The watcher's buffer overflowed: some changes were not reported.
    Missed,
}

// One change as the watcher reported it. Path and OldPath are full paths (OldPath only for Renamed); At is a monotonic
// clock (time since Monitor started), Time the wall clock shown on the row.
public sealed record FolderChange(ActivityKind Kind, string Path, string? OldPath, TimeSpan At, DateTimeOffset Time);

// One row of Recent activity. Paths are relative to the shared folder; OldPath is empty unless Kind is Renamed.
public sealed record ActivityRow(long Id, ActivityKind Kind, string Path, string OldPath, DateTimeOffset Time);

// The rows of Monitor's Recent activity, newest first, built from the watcher's raw events. One copy raises Created
// and a burst of Changed events, and its folder gets a Changed of its own; each copy becomes one row.
public sealed class FolderActivity
{
    private readonly List<Entry> _rows = new();
    private long _nextId;

    public FolderActivity(TimeSpan mergeWindow, int max, TimeSpan keep)
    {
        MergeWindow = mergeWindow;
        Max = max;
        Keep = keep;
    }

    // Events for the same path closer together than this are one row.
    public TimeSpan MergeWindow { get; }

    public int Max { get; }

    public TimeSpan Keep { get; }

    public string Root { get; private set; } = "";

    // Sets the folder the paths are relative to; rows of another folder go.
    public void Watch(string root)
    {
        root = (root ?? "").Trim();
        if (!string.Equals(Trimmed(root), Trimmed(Root), StringComparison.OrdinalIgnoreCase))
        {
            _rows.Clear();
        }

        Root = root;
    }

    // True when the rows changed.
    public bool Add(FolderChange change)
    {
        if (change is null)
        {
            throw new ArgumentNullException(nameof(change));
        }

        if (change.Kind == ActivityKind.Missed)
        {
            return AddMissed(change);
        }

        var kind = change.Kind;
        var path = RelativePath(Root, change.Path);
        var old = change.OldPath is null ? "" : RelativePath(Root, change.OldPath);

        // A rename from outside the folder arrives without an old name, one to outside without a new name.
        if (kind == ActivityKind.Renamed && old.Length == 0)
        {
            kind = ActivityKind.Added;
        }
        else if (kind == ActivityKind.Renamed && path.Length == 0)
        {
            kind = ActivityKind.Deleted;
            path = old;
            old = "";
        }

        if (path.Length == 0)
        {
            return false;
        }

        var changed = DropFolderChanges(path, change.At);
        if (old.Length > 0)
        {
            changed |= DropFolderChanges(old, change.At);
        }

        if (kind == ActivityKind.Changed)
        {
            // A folder's own write time moves when something inside it changes; that change has its row.
            if (HasRecentInside(path, change.At))
            {
                return changed;
            }

            if (NewestFor(path) is { Kind: ActivityKind.Added or ActivityKind.Changed } recent && change.At - recent.LastAt <= MergeWindow)
            {
                recent.LastAt = change.At;
                return changed;
            }
        }

        Insert(new Entry(++_nextId, kind, path, kind == ActivityKind.Renamed ? old : "", change.At, change.Time));
        return true;
    }

    // Removes rows older than Keep; true when any went.
    public bool Expire(TimeSpan now) => _rows.RemoveAll(e => now - e.FirstAt > Keep) > 0;

    public IReadOnlyList<ActivityRow> Rows()
    {
        var rows = new List<ActivityRow>(_rows.Count);
        foreach (var e in _rows)
        {
            rows.Add(new ActivityRow(e.Id, e.Kind, e.Path, e.OldPath, e.Time));
        }

        return rows;
    }

    public void Clear() => _rows.Clear();

    // The path below root, or the full path when it is not inside root; empty for root itself.
    public static string RelativePath(string root, string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return "";
        }

        var r = Trimmed(root ?? "");
        if (r.Length == 0)
        {
            return fullPath;
        }

        if (fullPath.StartsWith(r, StringComparison.OrdinalIgnoreCase))
        {
            if (fullPath.Length == r.Length)
            {
                return "";
            }

            if (IsSeparator(fullPath[r.Length]))
            {
                return fullPath.Substring(r.Length).TrimStart('\\', '/');
            }
        }

        return fullPath;
    }

    // A new overflow row only when the newest row is not one already: overflows in a row stay one row.
    private bool AddMissed(FolderChange change)
    {
        if (_rows.Count > 0 && _rows[0].Kind == ActivityKind.Missed)
        {
            _rows[0].LastAt = change.At;
            return false;
        }

        Insert(new Entry(++_nextId, ActivityKind.Missed, "", "", change.At, change.Time));
        return true;
    }

    private void Insert(Entry entry)
    {
        _rows.Insert(0, entry);
        Expire(entry.LastAt);
        if (_rows.Count > Max)
        {
            _rows.RemoveRange(Max, _rows.Count - Max);
        }
    }

    // Removes recent Changed rows of the folders that hold path.
    private bool DropFolderChanges(string path, TimeSpan at) =>
        _rows.RemoveAll(e => e.Kind == ActivityKind.Changed && at - e.LastAt <= MergeWindow && IsInside(path, e.Path)) > 0;

    private bool HasRecentInside(string folder, TimeSpan at)
    {
        foreach (var e in _rows)
        {
            if (e.Kind != ActivityKind.Missed && at - e.LastAt <= MergeWindow && (IsInside(e.Path, folder) || IsInside(e.OldPath, folder)))
            {
                return true;
            }
        }

        return false;
    }

    private Entry? NewestFor(string path)
    {
        foreach (var e in _rows)
        {
            if (e.Kind != ActivityKind.Missed && string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return e;
            }
        }

        return null;
    }

    private static bool IsInside(string path, string folder) =>
        folder.Length > 0 && path.Length > folder.Length + 1
        && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && IsSeparator(path[folder.Length]);

    private static bool IsSeparator(char c) => c is '\\' or '/';

    private static string Trimmed(string path) => path.TrimEnd('\\', '/');

    private sealed class Entry
    {
        public Entry(long id, ActivityKind kind, string path, string oldPath, TimeSpan at, DateTimeOffset time)
        {
            Id = id;
            Kind = kind;
            Path = path;
            OldPath = oldPath;
            FirstAt = at;
            LastAt = at;
            Time = time;
        }

        public long Id { get; }

        public ActivityKind Kind { get; }

        public string Path { get; }

        public string OldPath { get; }

        public TimeSpan FirstAt { get; }

        public TimeSpan LastAt { get; set; }

        public DateTimeOffset Time { get; }
    }
}
