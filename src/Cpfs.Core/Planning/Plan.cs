using System;
using System.Collections.Generic;

namespace Cpfs.Core.Planning;

public enum PlanAction
{
    Keep,
    Change,
    Create,

    // Removed only because CPFS made it (its name, or what config.json recorded).
    RemoveIfOurs,

    // Done only when the user ticks it; skipped otherwise.
    Ask,

    // The current value could not be read, or the answer is not valid yet.
    CannotPlan,
}

public enum PlanCause
{
    None,
    NeedsAdmin,
    ReadFailed,
    InvalidAnswer,
}

public enum PlanSection
{
    Backup,
    User,
    Folder,
    Share,
    Server,
    Hardening,
    Firewall,
    Networks,
    AutoPrivate,
    Drive,
    Shortcut,
    DataFolder,
    Computer,
}

// A value in the plan: data shown as found (a path, a number), or a word the UI translates (a resource key).
public sealed record PlanValue(string? Data, string? WordKey)
{
    public static PlanValue Of(string? data) => new(data, null);

    public static PlanValue Of(long data) => new(data.ToString(System.Globalization.CultureInfo.InvariantCulture), null);

    public static PlanValue Word(string key) => new(null, key);
}

public sealed record PlanItem
{
    // Stable across recomputes; the key of an Ask item is what SetupAnswers.Ticked holds.
    public string Key { get; init; } = "";

    public PlanSection Section { get; init; }

    public PlanAction Action { get; init; }

    public PlanCause Cause { get; init; }

    // The object, as data (a share name, a registry value name, a rule name) or a resource key.
    public PlanValue Subject { get; init; } = PlanValue.Of("");

    public PlanValue? Current { get; init; }

    public PlanValue? Target { get; init; }

    // The original value goes into config.json's Backup before this change.
    public bool BackupNeeded { get; init; }

    // For an Ask item: whether it is ticked in these answers. A ticked Ask is carried out like a Change.
    public bool Ticked { get; init; }

    public string ReasonKey { get; init; } = "";

    public IReadOnlyList<string> ReasonArgs { get; init; } = Array.Empty<string>();
}

public sealed class Plan
{
    public Plan(IReadOnlyList<PlanItem> items)
    {
        Items = items;
    }

    public IReadOnlyList<PlanItem> Items { get; }

    public int Count(PlanAction action)
    {
        var n = 0;
        foreach (var item in Items)
        {
            if (item.Action == action)
            {
                n++;
            }
        }

        return n;
    }

    // One line per item that is not Keep, for the log: action, cause, key and reason key. Reason arguments stay out,
    // since they can hold paths and account names the log does not need.
    public IReadOnlyList<string> LogLines()
    {
        var lines = new List<string>();
        foreach (var item in Items)
        {
            if (item.Action != PlanAction.Keep)
            {
                lines.Add(item.Action + (item.Cause == PlanCause.None ? "" : "/" + item.Cause) + (item.Ticked ? " (ticked)" : "") + " " + item.Key + ": " + item.ReasonKey);
            }
        }

        return lines;
    }

    public PlanItem? Find(string key)
    {
        foreach (var item in Items)
        {
            if (string.Equals(item.Key, key, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }
}
