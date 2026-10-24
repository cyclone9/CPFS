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

            // Waits for a later version: not counted as a change.
            Item("auto.task", PlanSection.AutoPrivate, PlanAction.Create),
        });

        var summary = PlanSummary.Setup(plan, Same, Installed);

        Assert.True(summary.NothingToChange);
        Assert.Equal(new[] { "Summary_Nothing" }, Keys(summary));
        Assert.Equal(1, summary.Deferred);
    }

    [Fact]
    public void APasswordChangeIsNamedBeforeTheCount()
    {
        var plan = new Plan(new[]
        {
            Item("share.main", PlanSection.Share, PlanAction.Keep),
            Item("user.password", PlanSection.User, PlanAction.Change),
        });

        var summary = PlanSummary.Setup(plan, Same, Installed);

        Assert.Equal(new[] { "Summary_Password", "Summary_Changes_One" }, Keys(summary));
        Assert.Equal("cpfsuser", summary.Lines[0].Args[0]);
        Assert.Equal(1, summary.Lines[1].Args[0]);
    }

    [Fact]
    public void PhoneFactsComeFirstAndOtherFactsAreCappedAtThree()
    {
        var answers = Same with { ShareName = "Family", Strict = true };
        var plan = new Plan(new[]
        {
            Item("share.main", PlanSection.Share, PlanAction.Change, reasonArgs: "EncryptData, Description"),
            Item("server.reject", PlanSection.Server, PlanAction.Change),
            Item("hardening.lockout", PlanSection.Hardening, PlanAction.Change),
            Item("fw.rule:CPFS SMB (LAN)", PlanSection.Firewall, PlanAction.Create),
            Item("net:Home", PlanSection.Networks, PlanAction.Ask, ticked: true, subject: "Home"),
            Item("net:Cafe", PlanSection.Networks, PlanAction.Ask, subject: "Cafe"),
            Item("data.folder", PlanSection.DataFolder, PlanAction.Change),
        });

        var summary = PlanSummary.Setup(plan, answers, Installed);

        Assert.Equal(new[]
        {
            "Summary_ShareRenamed", "Summary_StrictOn", "Summary_StaysPublic",
            "Summary_Changes_Many",
            "Summary_Security_Many", "Summary_Firewall_Created_One", "Summary_Networks_One",
        }, Keys(summary));
        Assert.Equal("Cafe", summary.Lines[2].Args[0]);
        Assert.Equal(6, summary.Changes);
    }

    [Fact]
    public void AFirstSetupNamesTheFolderTheShareAndTheAccount()
    {
        var answers = new SetupAnswers { SharePath = @" E:\Photos ", ShareName = "Photos", SmbUser = "phone" };
        var plan = new Plan(new[]
        {
            Item("backup", PlanSection.Backup, PlanAction.Create),
            Item("user.account", PlanSection.User, PlanAction.Create),
            Item("user.password", PlanSection.User, PlanAction.Create),
            Item("share.main", PlanSection.Share, PlanAction.Create),
        });

        var summary = PlanSummary.Setup(plan, answers, null);

        Assert.Equal(new[] { "Summary_Shares", "Summary_Changes_Many", "Summary_Backup" }, Keys(summary));
        Assert.Equal(new object[] { @"E:\Photos", "Photos", "phone" }, summary.Lines[0].Args);
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
    public void UninstallNamesWhatPhonesLoseAndThatFilesStay()
    {
        var plan = new Plan(new[]
        {
            Item("u.savecopy", PlanSection.DataFolder, PlanAction.Create),
            Item("u.share:Share", PlanSection.Share, PlanAction.RemoveIfOurs, subject: "Share"),
            new PlanItem { Key = "u.user.delete", Section = PlanSection.User, Action = PlanAction.Ask, Ticked = true, Current = PlanValue.Of("cpfsuser") },
            Item("u.user.profile", PlanSection.User, PlanAction.Ask),
            Item("u.fw:CPFS SMB (LAN)", PlanSection.Firewall, PlanAction.RemoveIfOurs),
            Item("u.reg:NoLMHash", PlanSection.Hardening, PlanAction.Change),
            Item("u.drive", PlanSection.Drive, PlanAction.RemoveIfOurs),
        });

        var summary = PlanSummary.Uninstall(plan);

        Assert.Equal(new[]
        {
            "Summary_U_Share", "Summary_U_DeleteUser", "Summary_Changes_Many", "Summary_U_FilesStay",
            "Summary_U_Copy", "Summary_U_Restore_One", "Summary_U_Firewall_Removed_One",
        }, Keys(summary));
        Assert.Equal("cpfsuser", summary.Lines[1].Args[0]);
        Assert.Equal(6, summary.Changes);
        Assert.Equal(0, summary.Deferred);
    }

    [Fact]
    public void AKeptAccountAndEachKindOfFirewallChangeHaveTheirOwnLine()
    {
        var uninstall = PlanSummary.Uninstall(new Plan(new[]
        {
            new PlanItem { Key = "u.user.delete", Section = PlanSection.User, Action = PlanAction.Ask, Current = PlanValue.Of("phone") },
            Item("u.fw:CPFS SMB (LAN)", PlanSection.Firewall, PlanAction.RemoveIfOurs),
            Item("u.fw:CPFS SMB (VPN)", PlanSection.Firewall, PlanAction.RemoveIfOurs),
            Item("u.builtin:FPS-SMB-In-TCP", PlanSection.Firewall, PlanAction.Change),
        }));

        Assert.Equal(new[] { "Summary_U_KeepUser", "Summary_Changes_Many", "Summary_U_FilesStay", "Summary_U_Firewall_Removed_Many", "Summary_U_Firewall_TurnedOn_One" },
            Keys(uninstall));
        Assert.Equal("phone", uninstall.Lines[0].Args[0]);

        var setup = PlanSummary.Setup(new Plan(new[]
        {
            Item("fw.rule:CPFS SMB (LAN)", PlanSection.Firewall, PlanAction.Change),
            Item("fw.builtin:FPS-SMB-In-TCP", PlanSection.Firewall, PlanAction.Change),
            Item("fw.extra:CyShare SMB", PlanSection.Firewall, PlanAction.RemoveIfOurs),
        }), Same, Installed);

        Assert.Equal(new[] { "Summary_Changes_Many", "Summary_Firewall_Changed_One", "Summary_Firewall_TurnedOff_One", "Summary_Firewall_Removed_One" }, Keys(setup));
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
