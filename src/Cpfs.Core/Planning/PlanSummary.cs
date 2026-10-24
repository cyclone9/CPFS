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

// The plain-language summary above Setup's and Uninstall's main button. Changes that alter how phones connect come
// first and are never dropped; then the count of changes; then at most three other facts in a fixed order. Every
// line is a whole sentence, so a translation never has to glue fragments together.
public sealed class PlanSummary
{
    public const int MaxOtherFacts = 3;

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
        var phone = new List<SummaryLine>();
        var shareName = answers.ShareName.Trim();
        var user = answers.SmbUser.Trim();
        var share = plan.Find("share.main");
        var shareRuns = share is not null && Runs(share);

        if (config is null)
        {
            if (share is not null && share.Action != PlanAction.CannotPlan)
            {
                phone.Add(SummaryLine.Of("Summary_Shares", answers.SharePath.Trim(), shareName, user));
            }

            if (answers.Strict && shareRuns)
            {
                phone.Add(SummaryLine.Of("Summary_StrictOn"));
            }
        }
        else
        {
            if (shareRuns && !SameText(config.ShareName, shareName))
            {
                phone.Add(SummaryLine.Of("Summary_ShareRenamed", shareName));
            }

            if (shareRuns && !SamePath(config.SharePath, answers.SharePath))
            {
                phone.Add(SummaryLine.Of("Summary_ShareMoved", shareName, answers.SharePath.Trim()));
            }

            if (!SameText(config.SmbUser, user) && (plan.Find("user.account") is { Action: PlanAction.Create } || plan.Find("user.takeover") is { Ticked: true }))
            {
                phone.Add(SummaryLine.Of("Summary_NewAccount", user));
            }

            if (plan.Find("user.password") is { } password && Runs(password))
            {
                phone.Add(SummaryLine.Of("Summary_Password", user));
            }

            if (shareRuns && share!.ReasonArgs.Count > 0 && share.ReasonArgs[0].IndexOf("EncryptData", StringComparison.Ordinal) >= 0)
            {
                phone.Add(SummaryLine.Of(answers.Strict ? "Summary_StrictOn" : "Summary_StrictOff"));
            }
        }

        if (plan.Find("computer.name") is { } rename && Runs(rename) && !ApplyList.IsDeferred(rename) && rename.Target?.Data is { } newName)
        {
            phone.Add(SummaryLine.Of("Summary_Rename", newName));
        }

        var madePrivate = 0;
        foreach (var item in plan.Items)
        {
            if (item.Section == PlanSection.Networks && item.Action == PlanAction.Ask)
            {
                if (item.Ticked)
                {
                    madePrivate++;
                }
                else
                {
                    phone.Add(SummaryLine.Of("Summary_StaysPublic", item.Subject.Data ?? "?"));
                }
            }
        }

        // The drive and the shortcut the user named are never dropped either: they are what shows up on this PC.
        var addsDrive = plan.Find("drive.map") is { Action: PlanAction.Create or PlanAction.Ask } map && Runs(map) && answers.DriveLetter is not null;
        var addsShortcut = plan.Find("shortcut.file") is { Action: PlanAction.Create or PlanAction.Ask } lnk && Runs(lnk);
        var driveName = answers.DriveName.Trim();
        var withIcon = plan.Find("drive.icon") is { } icon && (icon.Action == PlanAction.Keep || Runs(icon));
        if (addsDrive && addsShortcut)
        {
            phone.Add(SummaryLine.Of(withIcon ? "Summary_DriveIconAndShortcut" : "Summary_DriveAndShortcut", answers.DriveLetter + ":", driveName));
        }
        else if (addsDrive)
        {
            phone.Add(SummaryLine.Of(withIcon ? "Summary_DriveIcon" : "Summary_Drive", answers.DriveLetter + ":", driveName));
        }
        else if (addsShortcut)
        {
            phone.Add(SummaryLine.Of("Summary_Shortcut", answers.ShortcutName.Trim()));
        }

        if (plan.Find("explorer.entry") is { Action: PlanAction.Create or PlanAction.Ask } entry && Runs(entry))
        {
            phone.Add(SummaryLine.Of("Summary_Explorer", driveName));
        }

        var other = new List<SummaryLine>();
        if (plan.Find("backup") is { } backup && Runs(backup))
        {
            other.Add(SummaryLine.Of("Summary_Backup"));
        }

        AddCount(other, "Summary_Security", CountRunnable(plan, PlanSection.Server, PlanSection.Hardening));
        AddCount(other, "Summary_Firewall_Created", CountFirewall(plan, PlanAction.Create, builtIn: false));
        AddCount(other, "Summary_Firewall_Changed", CountFirewall(plan, PlanAction.Change, builtIn: false));
        AddCount(other, "Summary_Firewall_TurnedOff", CountFirewall(plan, PlanAction.Change, builtIn: true));
        AddCount(other, "Summary_Firewall_Removed", CountFirewall(plan, PlanAction.RemoveIfOurs, builtIn: false));
        AddCount(other, "Summary_Networks", madePrivate);
        if (plan.Find("data.folder") is { } data && Runs(data))
        {
            other.Add(SummaryLine.Of("Summary_DataFolder"));
        }

        return Build(phone, other, changes, deferred, plan, "Summary_Nothing", null, "Summary_Blocked_Answers");
    }

    public static PlanSummary Uninstall(Plan plan)
    {
        var (changes, deferred) = Count(ApplyList.Uninstall(plan));
        var phone = new List<SummaryLine>();
        var other = new List<SummaryLine>();
        var publicAgain = 0;
        foreach (var item in plan.Items)
        {
            if (item.Key == "u.user.delete" && item.Action == PlanAction.Ask && !item.Ticked)
            {
                phone.Add(SummaryLine.Of("Summary_U_KeepUser", item.Current?.Data ?? "?"));
                continue;
            }

            if (!Runs(item) || ApplyList.IsDeferred(item))
            {
                continue;
            }

            if (item.Key.StartsWith("u.share:", StringComparison.Ordinal))
            {
                phone.Add(SummaryLine.Of("Summary_U_Share", item.Subject.Data ?? "?"));
            }
            else if (item.Key == "u.user.delete")
            {
                phone.Add(SummaryLine.Of("Summary_U_DeleteUser", item.Current?.Data ?? "?"));
            }
            else if (item.Key.StartsWith("u.net:", StringComparison.Ordinal))
            {
                publicAgain++;
            }
        }

        // The entry stays when its user's hive is not loaded: said in the simple view, not only in the details.
        if (plan.Find("u.explorer") is { Action: PlanAction.Keep } left && left.ReasonKey == UninstallPlanner.R.ExplorerNotLoaded)
        {
            phone.Add(SummaryLine.Of("Summary_U_ExplorerLeft", left.Current?.Data ?? "?"));
        }

        if (plan.Find("u.savecopy") is { } copy && Runs(copy))
        {
            other.Add(SummaryLine.Of("Summary_U_Copy"));
        }

        AddCount(other, "Summary_U_Restore", CountRunnable(plan, PlanSection.Server, PlanSection.Hardening));
        AddCount(other, "Summary_U_Firewall_Removed", CountFirewall(plan, PlanAction.RemoveIfOurs, builtIn: false));
        AddCount(other, "Summary_U_Firewall_TurnedOn", CountFirewall(plan, PlanAction.Change, builtIn: true));
        AddCount(other, "Summary_U_Networks", publicAgain);
        if (plan.Find("u.data") is { } data && Runs(data))
        {
            other.Add(SummaryLine.Of("Summary_U_Data"));
        }

        return Build(phone, other, changes, deferred, plan, "Summary_U_Nothing", "Summary_U_FilesStay", "Summary_U_Blocked");
    }

    private static PlanSummary Build(List<SummaryLine> phone, List<SummaryLine> other, int changes, int deferred, Plan plan,
        string nothingKey, string? afterCount, string answersKey)
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

        lines.AddRange(phone);
        if (changes > 0)
        {
            lines.Add(SummaryLine.Of(changes == 1 ? "Summary_Changes_One" : "Summary_Changes_Many", changes));
            if (afterCount is not null)
            {
                lines.Add(SummaryLine.Of(afterCount));
            }
        }

        for (var i = 0; i < other.Count && i < MaxOtherFacts; i++)
        {
            lines.Add(other[i]);
        }

        return new PlanSummary(lines, changes, deferred, blocking);
    }

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

    private static int CountRunnable(Plan plan, params PlanSection[] sections)
    {
        var n = 0;
        foreach (var item in plan.Items)
        {
            // An Ask item that only carries a choice (the VPN adapter) changes nothing itself.
            if (Array.IndexOf(sections, item.Section) >= 0 && Runs(item) && !ApplyList.IsDeferred(item)
                && !item.Key.StartsWith("fw.vpnadapter:", StringComparison.Ordinal))
            {
                n++;
            }
        }

        return n;
    }

    // CPFS's own rules (Setup's fw.rule and fw.extra, Uninstall's u.fw) apart from Windows' built-in ones, which Setup
    // turns off and Uninstall turns back on.
    private static int CountFirewall(Plan plan, PlanAction action, bool builtIn)
    {
        var n = 0;
        foreach (var item in plan.Items)
        {
            var isBuiltIn = item.Key.StartsWith("fw.builtin:", StringComparison.Ordinal) || item.Key.StartsWith("u.builtin:", StringComparison.Ordinal);
            if (item.Section == PlanSection.Firewall && item.Action == action && isBuiltIn == builtIn && Runs(item) && !ApplyList.IsDeferred(item))
            {
                n++;
            }
        }

        return n;
    }

    private static void AddCount(List<SummaryLine> lines, string stem, int n)
    {
        if (n > 0)
        {
            lines.Add(SummaryLine.Of(stem + (n == 1 ? "_One" : "_Many"), n));
        }
    }

    private static bool SameText(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string? a, string? b) => SameText((a ?? "").Trim().TrimEnd('\\'), (b ?? "").Trim().TrimEnd('\\'));
}
