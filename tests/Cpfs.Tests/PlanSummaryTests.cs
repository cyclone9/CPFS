using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cpfs.Core.Config;
using Cpfs.Core.Planning;
using Xunit;

namespace Cpfs.Tests;

public class PlanSummaryTests
{
    private static readonly ConfigDocument Installed = ConfigReader.Parse(Encoding.UTF8.GetBytes(
        """{ "Version": "0.2.3", "SmbUser": "cpfsuser", "SharePath": "D:\\Share", "ShareName": "Share" }"""));

    private static readonly SetupAnswers Same = new() { SharePath = @"D:\Share", ShareName = "Share", SmbUser = "cpfsuser" };

    private static PlanItem Item(string key, PlanSection section, PlanAction action, bool ticked = false, string? subject = null, params string[] reasonArgs) =>
        new() { Key = key, Section = section, Action = action, Ticked = ticked, Subject = PlanValue.Of(subject ?? key), ReasonArgs = reasonArgs };

    private static List<string> Keys(PlanSummary summary) => summary.Lines.Select(l => l.Key).ToList();

    [Fact]
    public void NothingToChangeWhenEverythingIsKept()
    {
        var plan = new Plan(new[]
        {
            Item("share.main", PlanSection.Share, PlanAction.Keep),
            Item("user.password", PlanSection.User, PlanAction.Keep),

            // An unticked question changes nothing; it is shown under Your decisions instead.
            Item("share.adminshares", PlanSection.Share, PlanAction.Ask),
            Item("auto.task", PlanSection.AutoPrivate, PlanAction.Keep),
        });

        var summary = PlanSummary.Setup(plan, Same, Installed);

        Assert.True(summary.NothingToChange);
        Assert.Equal(new[] { "Summary_Nothing" }, Keys(summary));
        Assert.Equal(0, summary.Deferred);
    }

    [Fact]
    public void APasswordChangeIsTheOnlyLine()
    {
        var plan = new Plan(new[]
        {
            Item("share.main", PlanSection.Share, PlanAction.Keep),
            Item("user.password", PlanSection.User, PlanAction.Change),

            // Technical changes that come with it stay under Show details.
            Item("fw.rule:CPFS SMB (LAN)", PlanSection.Firewall, PlanAction.Change),
        });

        var summary = PlanSummary.Setup(plan, Same, Installed);

        Assert.Equal(new[] { "Summary_Password" }, Keys(summary));
        Assert.Equal("cpfsuser", summary.Lines[0].Args[0]);
        Assert.Equal(2, summary.Changes);
    }

    [Fact]
    public void OnlyTechnicalChangesAreOneCountedLine()
    {
        var plan = new Plan(new[]
        {
            Item("share.main", PlanSection.Share, PlanAction.Keep),
            Item("server.reject", PlanSection.Server, PlanAction.Change),
            Item("hardening.lockout", PlanSection.Hardening, PlanAction.Change),
            Item("fw.rule:CPFS SMB (LAN)", PlanSection.Firewall, PlanAction.Create),
            Item("fw.builtin:FPS-SMB-In-TCP", PlanSection.Firewall, PlanAction.Change),
            Item("data.folder", PlanSection.DataFolder, PlanAction.Change),
        });

        var summary = PlanSummary.Setup(plan, Same, Installed);

        Assert.Equal(new[] { "Summary_Corrects_Many" }, Keys(summary));
        Assert.Equal(5, summary.Lines[0].Args[0]);

        var one = PlanSummary.Setup(new Plan(new[] { Item("data.folder", PlanSection.DataFolder, PlanAction.Change) }), Same, Installed);
        Assert.Equal(new[] { "Summary_Corrects_One" }, Keys(one));
    }

    [Fact]
    public void WhatDevicesNoticeIsNamedAndANetworkLeftPublicIsNeverDropped()
    {
        var answers = Same with { ShareName = "Family", Strict = true };
        var plan = new Plan(new[]
        {
            Item("share.main", PlanSection.Share, PlanAction.Change, reasonArgs: "EncryptData, Description"),
            Item("server.reject", PlanSection.Server, PlanAction.Change),
            Item("fw.rule:CPFS SMB (LAN)", PlanSection.Firewall, PlanAction.Create),
            Item("net:Home", PlanSection.Networks, PlanAction.Ask, ticked: true, subject: "Home"),
            Item("net:Cafe", PlanSection.Networks, PlanAction.Ask, subject: "Cafe"),
            Item("data.folder", PlanSection.DataFolder, PlanAction.Change),
        });

        var summary = PlanSummary.Setup(plan, answers, Installed);

        Assert.Equal(new[] { "Summary_StrictOn", "Summary_ShareRenamed", "Summary_StaysPublic" }, Keys(summary));
        Assert.Equal("Cafe", summary.Lines[2].Args[0]);
        Assert.Equal(5, summary.Changes);

        // With nothing else to name, the network notice comes after the counted line.
        var technical = PlanSummary.Setup(new Plan(plan.Items.Where(i => i.Key != "share.main").ToList()), answers, Installed);
        Assert.Equal(new[] { "Summary_Corrects_Many", "Summary_StaysPublic" }, Keys(technical));
    }

    [Fact]
    public void AFirstSetupNamesTheFolderTheAccountAndWhatItAdds()
    {
        var answers = new SetupAnswers
        {
            SharePath = @" E:\Photos ", ShareName = "Photos", SmbUser = "phone", Drive = true, DriveLetter = 'z', DriveName = "CPFS - Photos",
            Shortcut = true, ShortcutName = "Photos (CPFS)", ExplorerEntry = true,
        };
        var plan = new Plan(new[]
        {
            Item("backup", PlanSection.Backup, PlanAction.Create),
            Item("user.account", PlanSection.User, PlanAction.Create),
            Item("user.password", PlanSection.User, PlanAction.Create),
            Item("share.main", PlanSection.Share, PlanAction.Create),
            Item("server.reject", PlanSection.Server, PlanAction.Change),
            Item("fw.rule:CPFS SMB (LAN)", PlanSection.Firewall, PlanAction.Create),
            Item("drive.map", PlanSection.Drive, PlanAction.Create),
            Item("drive.session", PlanSection.Drive, PlanAction.Create),
            Item("drive.label", PlanSection.Drive, PlanAction.Create),
            Item("drive.icon", PlanSection.Drive, PlanAction.Create),
            Item("shortcut.file", PlanSection.Shortcut, PlanAction.Create),
            Item("explorer.entry", PlanSection.Drive, PlanAction.Create),
            Item("data.folder", PlanSection.DataFolder, PlanAction.Change),
        });

        var summary = PlanSummary.Setup(plan, answers, null);

        Assert.Equal(new[] { "Summary_SharesWith", "Summary_Adds_DSE", "Summary_UndoAll" }, Keys(summary));
        Assert.Equal(new object[] { @"E:\Photos", "phone" }, summary.Lines[0].Args);
        Assert.Equal(new object[] { "Z:" }, summary.Lines[1].Args);

        // Only what is being added is named, in one sentence of its own.
        var noDrive = PlanSummary.Setup(new Plan(plan.Items.Where(i => !i.Key.StartsWith("drive.", System.StringComparison.Ordinal)).ToList()), answers with { Drive = false }, null);
        Assert.Equal(new[] { "Summary_SharesWith", "Summary_Adds_SE", "Summary_UndoAll" }, Keys(noDrive));
        Assert.Empty(noDrive.Lines[1].Args);
    }

    [Fact]
    public void ALaterRunSaysWhenTheDriveMovesAndWhatIsRemoved()
    {
        var config = ConfigReader.Parse(Encoding.UTF8.GetBytes(
            """{ "Version": "0.6.0", "SmbUser": "cpfsuser", "SharePath": "D:\\Share", "ShareName": "Share", "DriveLetter": "X", "Shortcut": "C:\\Users\\Public\\Desktop\\Share (CPFS).lnk" }"""));
        var moved = PlanSummary.Setup(new Plan(new[]
        {
            Item("drive.oldmap", PlanSection.Drive, PlanAction.RemoveIfOurs),
            Item("drive.map", PlanSection.Drive, PlanAction.Create),
            Item("shortcut.old", PlanSection.Shortcut, PlanAction.RemoveIfOurs),
            Item("explorer.entry", PlanSection.Drive, PlanAction.RemoveIfOurs),
        }), Same with { Drive = true, DriveLetter = 'Y', Shortcut = false }, config);

        Assert.Equal(new[] { "Summary_Removes_SE", "Summary_DriveMoves" }, Keys(moved));
        Assert.Equal(new object[] { "X:", "Y:" }, moved.Lines[1].Args);

        var renamed = PlanSummary.Setup(new Plan(new[]
        {
            Item("drive.oldmap", PlanSection.Drive, PlanAction.RemoveIfOurs),
            Item("shortcut.old", PlanSection.Shortcut, PlanAction.RemoveIfOurs),
            Item("shortcut.file", PlanSection.Shortcut, PlanAction.Create),
        }), Same with { Drive = false, Shortcut = true, ShortcutName = " My share " }, config);

        Assert.Equal(new[] { "Summary_Removes_D", "Summary_ShortcutRenamed" }, Keys(renamed));
        Assert.Equal(new object[] { "X:" }, renamed.Lines[0].Args);
        Assert.Equal("My share", renamed.Lines[1].Args[0]);
    }

    [Fact]
    public void ItemsThatCannotBePlannedSaySoFirst()
    {
        var plan = new Plan(new[]
        {
            new PlanItem { Key = "user.account", Section = PlanSection.User, Action = PlanAction.CannotPlan, Cause = PlanCause.ReadFailed },
            Item("user.password", PlanSection.User, PlanAction.Change),
        });

        var summary = PlanSummary.Setup(plan, Same, Installed);

        Assert.False(summary.NothingToChange);
        Assert.Equal("Summary_Blocked", summary.Lines[0].Key);
        Assert.Contains("Summary_Password", Keys(summary));
    }

    [Fact]
    public void AMissingPasswordSaysWhatToDoAndNotThatItemsCannotBeChecked()
    {
        var answers = new SetupAnswers { SharePath = @"E:\Photos", ShareName = "Photos", SmbUser = "phone" };
        var missing = new PlanItem
        {
            Key = "user.password", Section = PlanSection.User, Action = PlanAction.CannotPlan, Cause = PlanCause.InvalidAnswer, ReasonKey = "Plan_R_PasswordNeeded",
        };
        var badName = new PlanItem
        {
            Key = "share.name", Section = PlanSection.Share, Action = PlanAction.CannotPlan, Cause = PlanCause.InvalidAnswer, ReasonKey = "Plan_R_Invalid",
        };
        var unread = new PlanItem { Key = "server.config", Section = PlanSection.Server, Action = PlanAction.CannotPlan, Cause = PlanCause.ReadFailed };

        var onlyPassword = PlanSummary.Setup(new Plan(new[] { missing, Item("share.main", PlanSection.Share, PlanAction.Create) }), answers, null);
        Assert.Equal("Summary_Blocked_Password", onlyPassword.Lines[0].Key);
        Assert.DoesNotContain("Summary_Blocked", Keys(onlyPassword));

        var answersToo = PlanSummary.Setup(new Plan(new[] { missing, badName }), answers, null);
        Assert.Equal(new[] { "Summary_Blocked_Password", "Summary_Blocked_Answers" }, Keys(answersToo).Take(2));

        var mixed = PlanSummary.Setup(new Plan(new[] { badName, unread }), answers, null);
        Assert.Equal(new[] { "Summary_Blocked_Answers", "Summary_Blocked" }, Keys(mixed).Take(2));

        var guard = new PlanItem { Key = "u.guard", Section = PlanSection.User, Action = PlanAction.CannotPlan, Cause = PlanCause.InvalidAnswer, ReasonKey = "Uninst_R_RefuseUser" };
        Assert.Equal("Summary_U_Blocked", PlanSummary.Uninstall(new Plan(new[] { guard })).Lines[0].Key);
    }

    [Fact]
    public void WithoutAdministratorRightsTheSummaryIsOneShortSentence()
    {
        var plan = new Plan(new[]
        {
            new PlanItem { Key = "user.account", Section = PlanSection.User, Action = PlanAction.CannotPlan, Cause = PlanCause.NeedsAdmin },
            Item("user.password", PlanSection.User, PlanAction.Change),
        });

        var summary = PlanSummary.Setup(plan, Same, Installed);

        Assert.Equal(new[] { "Summary_NeedsAdmin" }, Keys(summary));
        Assert.False(summary.NothingToChange);
    }

    [Fact]
    public void UninstallNamesTheShareAndTheDeletedAccountAndThatFilesStay()
    {
        var plan = new Plan(new[]
        {
            Item("u.savecopy", PlanSection.DataFolder, PlanAction.Create),
            Item("u.share:Share", PlanSection.Share, PlanAction.RemoveIfOurs, subject: "Share"),
            new PlanItem { Key = "u.user.delete", Section = PlanSection.User, Action = PlanAction.Ask, Ticked = true, Current = PlanValue.Of("cpfsuser") },
            Item("u.user.profile", PlanSection.User, PlanAction.Ask),
            Item("u.fw:CPFS SMB (LAN)", PlanSection.Firewall, PlanAction.RemoveIfOurs),
            Item("u.reg:NoLMHash", PlanSection.Hardening, PlanAction.Change),
            Item("u.net:Home", PlanSection.Networks, PlanAction.Change),
            Item("u.drive", PlanSection.Drive, PlanAction.RemoveIfOurs),
            Item("u.data", PlanSection.DataFolder, PlanAction.RemoveIfOurs),
        });

        var summary = PlanSummary.Uninstall(plan);

        Assert.Equal(new[] { "Summary_U_ShareAndUser", "Summary_U_PutsBack", "Summary_U_FilesStay" }, Keys(summary));
        Assert.Equal(new object[] { "Share", "cpfsuser" }, summary.Lines[0].Args);
        Assert.Equal(8, summary.Changes);
        Assert.Equal(0, summary.Deferred);
    }

    [Fact]
    public void UninstallSaysWhenTheAccountIsKeptAndWhatStaysBehind()
    {
        var kept = PlanSummary.Uninstall(new Plan(new[]
        {
            Item("u.share:Share", PlanSection.Share, PlanAction.RemoveIfOurs, subject: "Share"),
            new PlanItem { Key = "u.user.delete", Section = PlanSection.User, Action = PlanAction.Ask, Current = PlanValue.Of("phone") },
            Item("u.fw:CPFS SMB (LAN)", PlanSection.Firewall, PlanAction.RemoveIfOurs),
            Item("u.builtin:FPS-SMB-In-TCP", PlanSection.Firewall, PlanAction.Change),
            new PlanItem
            {
                Key = "u.explorer", Section = PlanSection.Drive, Action = PlanAction.Keep, ReasonKey = UninstallPlanner.R.ExplorerNotLoaded,
                Subject = PlanValue.Of("u.explorer"), Current = PlanValue.Of("CPFS - Share"),
            },
        }));

        Assert.Equal(new[] { "Summary_U_ShareKeepUser", "Summary_U_PutsBack", "Summary_U_ExplorerLeft", "Summary_U_FilesStay" }, Keys(kept));
        Assert.Equal(new object[] { "Share", "phone" }, kept.Lines[0].Args);

        // Without the share or the account, the account and the rest have their own lines.
        var accountOnly = PlanSummary.Uninstall(new Plan(new[]
        {
            new PlanItem { Key = "u.user.delete", Section = PlanSection.User, Action = PlanAction.Ask, Ticked = true, Current = PlanValue.Of("phone") },
        }));
        Assert.Equal(new[] { "Summary_U_DeleteUser", "Summary_U_FilesStay" }, Keys(accountOnly));

        var rest = PlanSummary.Uninstall(new Plan(new[] { Item("u.fw:CPFS SMB (LAN)", PlanSection.Firewall, PlanAction.RemoveIfOurs) }));
        Assert.Equal(new[] { "Summary_U_RemovesSetup", "Summary_U_FilesStay" }, Keys(rest));
    }

    [Fact]
    public void EveryKeyTheSummaryCanUseExists()
    {
        var english = XDocument.Load(Path.Combine(TestFiles.RepoRoot, "src", "Cpfs", "Strings", "Strings.resx")).Root!.Elements("data")
            .Select(d => (string)d.Attribute("name")!).ToHashSet();
        var source = File.ReadAllText(Path.Combine(TestFiles.RepoRoot, "src", "Cpfs.Core", "Planning", "PlanSummary.cs"));
        var keys = Regex.Matches(source, "\"(Summary_[A-Za-z_]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();

        Assert.NotEmpty(keys);

        // A counted fact is a stem with a _One and a _Many form.
        Assert.DoesNotContain(keys, k => !english.Contains(k) && !(english.Contains(k + "_One") && english.Contains(k + "_Many")));
    }
}
