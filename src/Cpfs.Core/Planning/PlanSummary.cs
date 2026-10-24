using System;
using System.Collections.Generic;
using Cpfs.Core.Apply;
using Cpfs.Core.Config;

namespace Cpfs.Core.Planning;

// One sentence of a summary: a resource key and its arguments. A string argument is data (a path, a name) that keeps
// its own direction in right-to-left text; an int argument is a count the UI formats for its language.
public sealed record SummaryLine(string Key, IReadOnlyList<object> Args)
{
    public static SummaryLine Of(string key, params object[] args) => new(key, args);
}

// The plain-language summary above Setup's and Uninstall's main button: a few short lines, each about something the
// user will notice (the folder and account, a new password, a drive or shortcut added or removed). Counts of settings,
// firewall rules, the backup and the data folder stay under Show details. Every line is a whole sentence, so a
// translation never has to glue fragments together.
public sealed class PlanSummary
{
    // The quick-access objects one sentence names, as a bit mask: drive 1, shortcut 2, File Explorer entry 4.
    private static readonly string?[] AddsKeys =
    {
        null, "Summary_Adds_D", "Summary_Adds_S", "Summary_Adds_DS", "Summary_Adds_E", "Summary_Adds_DE", "Summary_Adds_SE", "Summary_Adds_DSE",
    };

    private static readonly string?[] RemovesKeys =
    {
        null, "Summary_Removes_D", "Summary_Removes_S", "Summary_Removes_DS", "Summary_Removes_E", "Summary_Removes_DE", "Summary_Removes_SE",
        "Summary_Removes_DSE",
    };

    private PlanSummary(IReadOnlyList<SummaryLine> lines, int changes, int deferred, int blocking)
    {
        Lines = lines;
        Changes = changes;
        Deferred = deferred;
        Blocking = blocking;
    }

    public IReadOnlyList<SummaryLine> Lines { get; }

    // Changes Apply will make now: runnable items, ticked Ask items included, deferred ones not.
    public int Changes { get; }

    public int Deferred { get; }

    // Items that could not be planned and keep the button off.
    public int Blocking { get; }

    public bool NothingToChange => Changes == 0 && Blocking == 0;

    // Keys only, for the log: the arguments can hold paths and account names.
    public string LogText()
    {
        var keys = new List<string>();
        foreach (var line in Lines)
        {
            keys.Add(line.Key);
        }

        return Changes + " changes, " + Deferred + " deferred, " + Blocking + " blocking; " + string.Join(", ", keys);
    }

    public static PlanSummary Setup(Plan plan, SetupAnswers answers, ConfigDocument? config)
    {
        var (changes, deferred) = Count(ApplyList.Setup(plan));
        var facts = new List<SummaryLine>();
        var shareName = answers.ShareName.Trim();
        var user = answers.SmbUser.Trim();
        var share = plan.Find("share.main");
        var shareRuns = share is not null && Runs(share);

        if (config is null)
        {
            if (share is not null && share.Action != PlanAction.CannotPlan)
            {
                facts.Add(SummaryLine.Of("Summary_SharesWith", answers.SharePath.Trim(), user));
            }

            if (answers.Strict && shareRuns)
            {
                facts.Add(SummaryLine.Of("Summary_StrictOn"));
            }
        }
        else
        {
            if (plan.Find("user.password") is { } password && Runs(password))
            {
                facts.Add(SummaryLine.Of("Summary_Password", user));
            }

            if (shareRuns && share!.ReasonArgs.Count > 0 && share.ReasonArgs[0].IndexOf("EncryptData", StringComparison.Ordinal) >= 0)
            {
                facts.Add(SummaryLine.Of(answers.Strict ? "Summary_StrictOn" : "Summary_StrictOff"));
            }

            if (shareRuns && !SameText(config.ShareName, shareName))
            {
                facts.Add(SummaryLine.Of("Summary_ShareRenamed", shareName));
            }

            if (shareRuns && !SamePath(config.SharePath, answers.SharePath))
            {
                facts.Add(SummaryLine.Of("Summary_ShareMoved", shareName, answers.SharePath.Trim()));
            }

            if (!SameText(config.SmbUser, user) && (plan.Find("user.account") is { Action: PlanAction.Create } || plan.Find("user.takeover") is { Ticked: true }))
            {
                facts.Add(SummaryLine.Of("Summary_NewAccount", user));
            }
        }

        if (plan.Find("computer.name") is { } rename && Runs(rename) && !ApplyList.IsDeferred(rename) && rename.Target?.Data is { } newName)
        {
            facts.Add(SummaryLine.Of("Summary_Rename", newName));
        }

        QuickAccess(plan, answers, config, facts);

        // A network left Public is a safety notice: shown whatever else the summary says.
        var notices = new List<SummaryLine>();
        foreach (var item in plan.Items)
        {
            if (item.Section == PlanSection.Networks && item.Action == PlanAction.Ask && !item.Ticked)
            {
                notices.Add(SummaryLine.Of("Summary_StaysPublic", item.Subject.Data ?? "?"));
            }
        }

        var closing = new List<SummaryLine>();
        if (config is null)
        {
            closing.Add(SummaryLine.Of("Summary_UndoAll"));
        }

        var onlyTechnical = changes == 1 ? SummaryLine.Of("Summary_Corrects_One", changes) : SummaryLine.Of("Summary_Corrects_Many", changes);
        return Build(facts, notices, closing, onlyTechnical, changes, deferred, plan, "Summary_Nothing", "Summary_Blocked_Answers");
    }

    public static PlanSummary Uninstall(Plan plan)
    {
        var (changes, deferred) = Count(ApplyList.Uninstall(plan));
        var shares = new List<string>();
        string? deletes = null;
        string? keeps = null;
        var putsBack = false;
        foreach (var item in plan.Items)
        {
            if (item.Key == "u.user.delete" && item.Action == PlanAction.Ask && !item.Ticked)
            {
                keeps = item.Current?.Data ?? "?";
                continue;
            }

            if (!Runs(item) || ApplyList.IsDeferred(item))
            {
                continue;
            }

            if (item.Key.StartsWith("u.share:", StringComparison.Ordinal))
            {
                shares.Add(item.Subject.Data ?? "?");
            }
            else if (item.Key == "u.user.delete")
            {
                deletes = item.Current?.Data ?? "?";
            }
            else if (item.Section is PlanSection.Server or PlanSection.Hardening
                || item.Key.StartsWith("u.builtin:", StringComparison.Ordinal) || item.Key.StartsWith("u.net:", StringComparison.Ordinal))
            {
                putsBack = true;
            }
        }

        var facts = new List<SummaryLine>();
        if (shares.Count == 1 && (deletes ?? keeps) is { } account)
        {
            facts.Add(SummaryLine.Of(deletes is not null ? "Summary_U_ShareAndUser" : "Summary_U_ShareKeepUser", shares[0], account));
        }
        else
        {
            foreach (var name in shares)
            {
                facts.Add(SummaryLine.Of("Summary_U_RemovesShare", name));
            }

            if (deletes is not null)
            {
                facts.Add(SummaryLine.Of("Summary_U_DeleteUser", deletes));
            }
            else if (keeps is not null)
            {
                facts.Add(SummaryLine.Of("Summary_U_KeepUser", keeps));
            }
        }

        if (putsBack)
        {
            facts.Add(SummaryLine.Of("Summary_U_PutsBack"));
        }

        // What Uninstall leaves behind is said in the simple view, never only in the details.
        var notices = new List<SummaryLine>();
        if (plan.Find("u.explorer") is { Action: PlanAction.Keep } left && left.ReasonKey == UninstallPlanner.R.ExplorerNotLoaded)
        {
            notices.Add(SummaryLine.Of("Summary_U_ExplorerLeft", left.Current?.Data ?? "?"));
        }

        var closing = new List<SummaryLine> { SummaryLine.Of("Summary_U_FilesStay") };
        return Build(facts, notices, closing, SummaryLine.Of("Summary_U_RemovesSetup"), changes, deferred, plan, "Summary_U_Nothing", "Summary_U_Blocked");
    }

    // The blocked lines, then the facts (or one line for changes nothing else names), the notices, and the closing line.
    private static PlanSummary Build(List<SummaryLine> facts, List<SummaryLine> notices, List<SummaryLine> closing, SummaryLine onlyTechnical,
        int changes, int deferred, Plan plan, string nothingKey, string answersKey)
    {
        var blockers = ApplyList.Blocking(plan);
        var blocking = blockers.Count;
        var lines = new List<SummaryLine>();
        if (NeedsAdmin(blockers))
        {
            // The page's top card explains it and has the button; one short sentence here is enough.
            lines.Add(SummaryLine.Of("Summary_NeedsAdmin"));
            return new PlanSummary(lines, changes, deferred, blocking);
        }

        if (blocking > 0)
        {
            lines.AddRange(BlockedLines(blockers, answersKey));
        }
        else if (changes == 0)
        {
            lines.Add(SummaryLine.Of(nothingKey));
            return new PlanSummary(lines, changes, deferred, blocking);
        }

        lines.AddRange(facts);
        if (facts.Count == 0 && changes > 0)
        {
            lines.Add(onlyTechnical);
        }

        lines.AddRange(notices);
        if (changes > 0)
        {
            lines.AddRange(closing);
        }

        return new PlanSummary(lines, changes, deferred, blocking);
    }

    // The drive, the desktop shortcut and the File Explorer entry: one sentence for those added and one for those
    // removed, and a line each for a drive on a new letter and a shortcut under a new name.
    private static void QuickAccess(Plan plan, SetupAnswers answers, ConfigDocument? config, List<SummaryLine> facts)
    {
        var newLetter = answers.DriveLetter is { } l ? char.ToUpperInvariant(l) + ":" : null;
        var oldLetter = config?.DriveLetter is { Length: 1 } o ? o.ToUpperInvariant() + ":" : "?";
        var mapsNew = newLetter is not null && Creates(plan.Find("drive.map"));
        var dropsOld = plan.Find("drive.oldmap") is { Action: PlanAction.RemoveIfOurs } oldMap && Runs(oldMap);
        var makesLnk = Creates(plan.Find("shortcut.file"));
        var dropsLnk = plan.Find("shortcut.old") is { } oldLnk && Runs(oldLnk);
        var entry = plan.Find("explorer.entry");
        int added = 0, removed = 0;
        string? moved = null;

        if (mapsNew && dropsOld)
        {
            // The same letter on a new folder is the share's move, said already.
            if (!SameText(oldLetter, newLetter))
            {
                moved = newLetter;
            }
        }
        else if (mapsNew)
        {
            added |= 1;
        }
        else if (dropsOld)
        {
            removed |= 1;
        }

        if (makesLnk && !dropsLnk)
        {
            added |= 2;
        }
        else if (dropsLnk && !makesLnk)
        {
            removed |= 2;
        }

        if (Creates(entry))
        {
            added |= 4;
        }
        else if (entry is { Action: PlanAction.RemoveIfOurs } && Runs(entry))
        {
            removed |= 4;
        }

        if (AddsKeys[added] is { } adds)
        {
            facts.Add((added & 1) != 0 ? SummaryLine.Of(adds, newLetter!) : SummaryLine.Of(adds));
        }

        if (RemovesKeys[removed] is { } removes)
        {
            facts.Add((removed & 1) != 0 ? SummaryLine.Of(removes, oldLetter) : SummaryLine.Of(removes));
        }

        if (moved is not null)
        {
            facts.Add(SummaryLine.Of("Summary_DriveMoves", oldLetter, moved));
        }

        if (makesLnk && dropsLnk)
        {
            facts.Add(SummaryLine.Of("Summary_ShortcutRenamed", answers.ShortcutName.Trim()));
        }
    }

    private static bool Creates(PlanItem? item) => item is { Action: PlanAction.Create or PlanAction.Ask } && Runs(item);

    private static (int Changes, int Deferred) Count(IReadOnlyList<ApplyEntry> entries)
    {
        int changes = 0, deferred = 0;
        foreach (var e in entries)
        {
            if (e.Deferred)
            {
                deferred++;
            }
            else
            {
                changes++;
            }
        }

        return (changes, deferred);
    }

    private static bool Runs(PlanItem item) => ApplyList.IsRunnable(item);

    // What keeps the button off, said as what to do: enter the password, correct the answers, or wait for what could
    // not be read. "Can't be checked yet" is only for the last.
    private static List<SummaryLine> BlockedLines(IReadOnlyList<PlanItem> blockers, string answersKey)
    {
        bool password = false, answers = false, unread = false;
        foreach (var item in blockers)
        {
            if (item.Cause != PlanCause.InvalidAnswer)
            {
                unread = true;
            }
            else if (item.ReasonKey == Planner.R.PasswordNeeded)
            {
                password = true;
            }
            else
            {
                answers = true;
            }
        }

        var lines = new List<SummaryLine>();
        if (password)
        {
            lines.Add(SummaryLine.Of("Summary_Blocked_Password"));
        }

        if (answers)
        {
            lines.Add(SummaryLine.Of(answersKey));
        }

        if (unread)
        {
            lines.Add(SummaryLine.Of("Summary_Blocked"));
        }

        return lines;
    }

    private static bool NeedsAdmin(IReadOnlyList<PlanItem> blockers)
    {
        foreach (var item in blockers)
        {
            if (item.Cause == PlanCause.NeedsAdmin)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameText(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string? a, string? b) => SameText((a ?? "").Trim().TrimEnd('\\'), (b ?? "").Trim().TrimEnd('\\'));
}
