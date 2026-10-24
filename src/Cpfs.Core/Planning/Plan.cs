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

// A value in the plan: data shown as found (a path, a number), a word the UI translates (a resource key), a word
// with one number in it (both set: the key's {0} takes Data), or a list of words.
public sealed record PlanValue(string? Data, string? WordKey)
{
    public IReadOnlyList<string>? WordList { get; init; }

    public static PlanValue Of(string? data) => new(data, null);

    public static PlanValue Of(long data) => new(data.ToString(System.Globalization.CultureInfo.InvariantCulture), null);

    public static PlanValue Word(string key) => new(null, key);

    public static PlanValue WordWith(string key, long data) => new(data.ToString(System.Globalization.CultureInfo.InvariantCulture), key);

    public static PlanValue Words(IReadOnlyList<string> keys) => new(null, null) { WordList = keys };
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

    // True when both plans hold the same items with the same action and tick, so what the page showed is still what
    // Apply would run. Values and reasons may differ: the executor reads each item again before it writes.
    public bool SameItemsAs(Plan other) => DifferencesFrom(other).Count == 0;

    // For the log: "-" for an item only this plan has, "+" for one only the other has, as key, action and tick.
    public IReadOnlyList<string> DifferencesFrom(Plan other)
    {
        if (other is null)
        {
            throw new ArgumentNullException(nameof(other));
        }

        var mine = Signature(this);
        var theirs = Signature(other);
        var list = new List<string>();
        foreach (var line in mine)
        {
            if (!theirs.Remove(line))
            {
                list.Add("-" + line);
            }
        }

        foreach (var line in theirs)
        {
            list.Add("+" + line);
        }

        return list;
    }

    private static List<string> Signature(Plan plan)
    {
        var list = new List<string>();
        foreach (var item in plan.Items)
        {
            list.Add(item.Key + " " + item.Action + (item.Ticked ? " (ticked)" : ""));
        }

        list.Sort(StringComparer.Ordinal);
        return list;
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
