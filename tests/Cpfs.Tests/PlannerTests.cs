using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Cpfs.Core.Apply;
using Cpfs.Core.Config;
using Cpfs.Core.Drives;
using Cpfs.Core.Planning;
using Cpfs.Core.Text;
using Cpfs.Core.Validation;
using Xunit;

namespace Cpfs.Tests;

public class PlannerTests
{
    private static readonly ProtectedFolders Protected = new()
    {
        Trees = { @"C:\Windows", @"C:\Program Files", @"C:\ProgramData" },
        Exact = { @"C:\Users", @"C:\Users\Public" },
    };

    private static ConfigDocument Fixture() => ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.3-anonymised.json"));

    // The machine a 0.2.3 Setup leaves behind for v0.2.3-anonymised.json (share "Shared" on D:\Shared, account
    // "ShareUser", shortcut only), read elevated.
    private static SystemSnapshot Installed()
    {
        var letters = new LetterScan();
        letters.Add('C', LetterSource.SystemDrive);
        letters.Add('D', LetterSource.ThisProcess);
        return new SystemSnapshot
        {
            Elevated = true,
            WindowsUserNames = new[] { "Owner" },
            ComputerName = "HOST-01",
            ComputerNames = Reading.Ok(new ComputerNameInfo("HOST-01", null)),
            Shares = Reading.Ok<IReadOnlyList<ShareInfo>>(new[]
            {
                new ShareInfo("Shared", @"D:\Shared", Planner.ShareDescription, false, 0, 0, false),
                new ShareInfo("IPC$", null, "Remote IPC", false, 1, 0, true),
            }),
            SmbServer = Reading.Ok(new SmbServerInfo(false, true, true)),
            ServerStart = Reading.Ok<int?>(2),
            Hardening = Planner.Hardening.ToDictionary(h => h.Name, h => Reading.Ok<int?>(h.Target)),
            Lockout = Reading.Ok(new LockoutInfo(10, 600, 600)),
            AuditLogon = Reading.Ok(new AuditSetting(true, true)),
            Smb1Feature = Reading.Ok<int?>(2),
            GuestEnabled = Reading.Ok<bool?>(false),
            OwnRules = Reading.Ok<IReadOnlyList<FirewallRuleInfo>>(new[]
            {
                Rule(FirewallRules.LanRule, FirewallRules.Tcp, "445", FirewallRules.ProfilePrivate, "LocalSubnet"),
                Rule(FirewallRules.MdnsRule, FirewallRules.Udp, "5353", FirewallRules.ProfilePrivate, "*"),
            }),
            BuiltInRules = Reading.Ok<IReadOnlyList<BuiltInRuleInfo>>(new[]
            {
                new BuiltInRuleInfo("FPS-SMB-In-TCP", "File and Printer Sharing (SMB-In)", false, 1, 4, new[] { "445" }),
                new BuiltInRuleInfo("FPS-SMB-In-TCP-Private", "File and Printer Sharing (SMB-In)", true, 1, 2, new[] { "445" }),
            }),
            Networks = Reading.Ok<IReadOnlyList<NetworkInfo>>(new[] { new NetworkInfo("Home", "Wi-Fi", 1) }),
            Tasks = Reading.Ok<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>
            {
                [Planner.AutoPrivateTask] = false, [Planner.RunAsUserTask] = false, [Planner.LegacyAutoPrivateTask] = false,
            }),
            DataFolder = Reading.Ok(new DataFolderInfo(true, true, true, false)),
            DosDevices = Reading.Ok<IReadOnlyDictionary<char, string>>(new Dictionary<char, string>()),
            DriveLabels = Reading.Ok<IReadOnlyDictionary<char, string>>(new Dictionary<char, string>()),
            Letters = letters,
            PublicDesktopFiles = Reading.Ok<IReadOnlyList<string>>(new[] { "Shared (CPFS).lnk", "desktop.ini" }),
            User = Reading.Ok<UserInfo?>(new UserInfo(UserSid, true, true, false, false, true, 0)),
            Folder = Reading.Ok(new FolderInfo(true, true)),
            ShareAccess = Reading.Ok<IReadOnlyList<ShareAccessEntry>?>(new[] { new ShareAccessEntry(@"HOST-01\ShareUser", UserSid, 0, 0) }),
        };
    }

    private const string UserSid = "S-1-5-21-1-2-3-1005";

    private static FirewallRuleInfo Rule(string name, int protocol, string ports, int profiles, string remote, params string[] interfaces) =>
        new(name, true, FirewallRules.Inbound, FirewallRules.Allow, protocol, ports, profiles, remote, interfaces);

    private static SetupAnswers Answers(ConfigDocument? config, SystemSnapshot s) => SetupDefaults.From(config, s.Letters);

    private static Plan PlanFor(SystemSnapshot s, ConfigDocument? config, Func<SetupAnswers, SetupAnswers>? change = null)
    {
        var answers = Answers(config, s);
        return Planner.Setup(s, change is null ? answers : change(answers), config, Protected);
    }

    private static PlanItem Item(Plan plan, string key) => plan.Find(key) ?? throw new InvalidOperationException("No plan item " + key);

    private static SetupAnswers Tick(SetupAnswers a, string key, bool ticked = true)
    {
        var choices = new Dictionary<string, bool>(a.Choices) { [key] = ticked };
        return a with { Choices = choices };
    }

    [Fact]
    public void An023InstallReadAsInstalledPlansOnlyKeep()
    {
        var plan = PlanFor(Installed(), Fixture());

        Assert.All(plan.Items, i => Assert.True(i.Action == PlanAction.Keep, i.Key + " is " + i.Action + " (" + i.ReasonKey + ")"));
        Assert.Equal(PlanAction.Keep, Item(plan, "backup").Action);
        Assert.Equal(Planner.R.BackupTaken, Item(plan, "backup").ReasonKey);
        Assert.Equal(Planner.R.PasswordKeep, Item(plan, "user.password").ReasonKey);
        Assert.Contains(plan.Items, i => i.Key == "shortcut.file");
        Assert.DoesNotContain(plan.Items, i => i.BackupNeeded);
    }

    [Fact]
    public void ReRunWithTheSameAnswersOnAMachineAtTargetChangesNothing()
    {
        var snapshot = Installed();
        var answers = Answers(Fixture(), snapshot);
        var first = Planner.Setup(snapshot, answers, Fixture(), Protected);
        var second = Planner.Setup(snapshot, answers, Fixture(), Protected);

        Assert.Equal(first.Items.Select(i => (i.Key, i.Action)), second.Items.Select(i => (i.Key, i.Action)));
    }

    [Fact]
    public void TheBackupIsTakenOnlyOnAFirstSetup()
    {
        var loose = Installed() with
        {
            Hardening = Planner.Hardening.ToDictionary(h => h.Name, h => Reading.Ok<int?>(h.Name == "LmCompatibilityLevel" ? 3 : h.Target)),
        };

        var fresh = PlanFor(loose, null);
        Assert.Equal(PlanAction.Create, Item(fresh, "backup").Action);
        Assert.True(Item(fresh, "hardening.LmCompatibilityLevel").BackupNeeded);

        var existing = PlanFor(loose, Fixture());
        Assert.Equal(PlanAction.Keep, Item(existing, "backup").Action);
        Assert.Equal(PlanAction.Change, Item(existing, "hardening.LmCompatibilityLevel").Action);
        Assert.False(Item(existing, "hardening.LmCompatibilityLevel").BackupNeeded);

        var noBackup = ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.1-upgraded-from-0.1.json"));
        var upgraded = PlanFor(loose, noBackup);
        Assert.Equal(Planner.R.BackupNone, Item(upgraded, "backup").ReasonKey);
        Assert.False(Item(upgraded, "hardening.LmCompatibilityLevel").BackupNeeded);
    }

    [Theory]
    [InlineData("RestrictAnonymous", 2)]
    [InlineData("LmCompatibilityLevel", 5)]
    public void AStricterSettingIsNeverLoosened(string name, int value)
    {
        var s = Installed() with
        {
            Hardening = Planner.Hardening.ToDictionary(h => h.Name, h => Reading.Ok<int?>(h.Name == name ? value : h.Target)),
        };

        var item = Item(PlanFor(s, Fixture()), "hardening." + name);
        Assert.Equal(PlanAction.Keep, item.Action);
    }

    [Fact]
    public void ALooserOrMissingHardeningValueIsChanged()
    {
        var s = Installed() with
        {
            Hardening = Planner.Hardening.ToDictionary(h => h.Name, h => Reading.Ok<int?>(h.Name switch
            {
                "AutoShareWks" => null,
                "forceguest" => 1,
                _ => h.Target,
            })),
        };

        var plan = PlanFor(s, Fixture());
        Assert.Equal(PlanAction.Change, Item(plan, "hardening.AutoShareWks").Action);
        Assert.Equal(Planner.W.Absent, Item(plan, "hardening.AutoShareWks").Current!.WordKey);
        Assert.Equal(PlanAction.Change, Item(plan, "hardening.forceguest").Action);
    }

    [Theory]
    [InlineData(0u, PlanAction.Change)]
    [InlineData(3u, PlanAction.Keep)]
    [InlineData(50u, PlanAction.Keep)]
    public void TheLockoutThresholdIsSetOnlyWhenItIsZero(uint threshold, PlanAction expected)
    {
        var s = Installed() with { Lockout = Reading.Ok(new LockoutInfo(threshold, 600, 600)) };
        var item = Item(PlanFor(s, Fixture()), "hardening.lockout");

        Assert.Equal(expected, item.Action);

        // The fixture's Backup.Lockout is null (the scripts could not read it), so a 0 found now is the original.
        Assert.Equal(threshold == 0, item.BackupNeeded);
    }

    [Fact]
    public void ANetworkBecomesPrivateOnlyWhenTicked()
    {
        var s = Installed() with
        {
            Networks = Reading.Ok<IReadOnlyList<NetworkInfo>>(new[] { new NetworkInfo("Cafe", "Wi-Fi", 0), new NetworkInfo("Home", "Ethernet", 1) }),
        };

        var plan = PlanFor(s, Fixture());
        Assert.Equal(PlanAction.Ask, Item(plan, "net:Cafe").Action);
        Assert.False(Item(plan, "net:Cafe").Ticked);
        Assert.Equal(PlanAction.Keep, Item(plan, "net:Home").Action);

        var ticked = PlanFor(s, Fixture(), a => Tick(a, "net:Cafe"));
        Assert.True(Item(ticked, "net:Cafe").Ticked);
    }

    [Fact]
    public void TheVpnRuleIsBoundToTheVpnAdapter()
    {
        var s = Installed() with
        {
            VpnAdapters = new[] { new VpnAdapterInfo("Tailscale", "100.101.102.103") },
            Networks = Reading.Ok<IReadOnlyList<NetworkInfo>>(new[] { new NetworkInfo("Home", "Wi-Fi", 1), new NetworkInfo("tailnet", "Tailscale", 1) }),
            OwnRules = Reading.Ok<IReadOnlyList<FirewallRuleInfo>>(new[]
            {
                Rule(FirewallRules.LanRule, FirewallRules.Tcp, "445", FirewallRules.ProfilePrivate, "LocalSubnet"),
                Rule(FirewallRules.MdnsRule, FirewallRules.Udp, "5353", FirewallRules.ProfilePrivate, "*"),
                Rule(FirewallRules.VpnRule, FirewallRules.Tcp, "445", FirewallRules.ProfileAll, "100.64.0.0/255.192.0.0,fd7a:115c:a1e0::/48"),
            }),
        };

        var item = Item(PlanFor(s, Fixture()), "fw.rule:" + FirewallRules.VpnRule);
        Assert.Equal(PlanAction.Change, item.Action);
        Assert.Equal(new[] { "Interfaces" }, item.ReasonArgs);

        var bound = s with
        {
            OwnRules = Reading.Ok<IReadOnlyList<FirewallRuleInfo>>(new[]
            {
                Rule(FirewallRules.LanRule, FirewallRules.Tcp, "445", FirewallRules.ProfilePrivate, "LocalSubnet"),
                Rule(FirewallRules.MdnsRule, FirewallRules.Udp, "5353", FirewallRules.ProfilePrivate, "*"),
                Rule(FirewallRules.VpnRule, FirewallRules.Tcp, "445", FirewallRules.ProfileAll, "100.64.0.0/255.192.0.0,fd7a:115c:a1e0::/48", "Tailscale"),
            }),
        };
        Assert.Equal(PlanAction.Keep, Item(PlanFor(bound, Fixture()), "fw.rule:" + FirewallRules.VpnRule).Action);
    }

    [Fact]
    public void AVpnAdapterOnAPublicNetworkIsUsedOnlyWhenTicked()
    {
        var s = Installed() with
        {
            VpnAdapters = new[] { new VpnAdapterInfo("Hotel Wi-Fi", "100.70.1.2") },
            Networks = Reading.Ok<IReadOnlyList<NetworkInfo>>(new[] { new NetworkInfo("Hotel", "Hotel Wi-Fi", 0) }),
        };

        var plan = PlanFor(s, Fixture());
        Assert.Equal(PlanAction.Ask, Item(plan, "fw.vpnadapter:Hotel Wi-Fi").Action);
        Assert.Equal(Planner.R.VpnNone, Item(plan, "fw.rule:" + FirewallRules.VpnRule).ReasonKey);

        var ticked = PlanFor(s, Fixture(), a => Tick(a, "fw.vpnadapter:Hotel Wi-Fi"));
        Assert.Equal(PlanAction.Create, Item(ticked, "fw.rule:" + FirewallRules.VpnRule).Action);
    }

    [Fact]
    public void AVpnRuleWithoutAVpnAdapterIsRemovedAsCpfsOwn()
    {
        var s = Installed() with
        {
            OwnRules = Reading.Ok<IReadOnlyList<FirewallRuleInfo>>(new[]
            {
                Rule(FirewallRules.LanRule, FirewallRules.Tcp, "445", FirewallRules.ProfilePrivate, "LocalSubnet"),
                Rule(FirewallRules.MdnsRule, FirewallRules.Udp, "5353", FirewallRules.ProfilePrivate, "*"),
                Rule(FirewallRules.VpnRule, FirewallRules.Tcp, "445", FirewallRules.ProfileAll, "100.64.0.0/10"),
                Rule("CyShare SMB", FirewallRules.Tcp, "445", FirewallRules.ProfileAll, "*"),
            }),
        };

        var plan = PlanFor(s, Fixture());
        Assert.Equal(PlanAction.RemoveIfOurs, Item(plan, "fw.rule:" + FirewallRules.VpnRule).Action);
        Assert.Equal(Planner.R.RuleLegacy, Item(plan, "fw.extra:CyShare SMB").ReasonKey);
    }

    [Fact]
    public void BuiltInRulesOpenOnPublicFor445AreDisabled()
    {
        var s = Installed() with
        {
            BuiltInRules = Reading.Ok<IReadOnlyList<BuiltInRuleInfo>>(new[]
            {
                new BuiltInRuleInfo("FPS-SMB-In-TCP", null, true, 1, 4, new[] { "445" }),
                new BuiltInRuleInfo("FPS-NB_Session-In-TCP", null, true, 1, 4, new[] { "139" }),
                new BuiltInRuleInfo("FPS-Unknown", null, true, 1, FirewallRules.ProfileAll, null),
            }),
        };

        var plan = PlanFor(s, Fixture());
        Assert.Equal(PlanAction.Change, Item(plan, "fw.builtin:FPS-SMB-In-TCP").Action);
        Assert.Null(plan.Find("fw.builtin:FPS-NB_Session-In-TCP"));
        Assert.Equal(PlanCause.ReadFailed, Item(plan, "fw.builtin:FPS-Unknown").Cause);
    }

    [Fact]
    public void ForeignSharesAreNeverRemovedSilently()
    {
        var s = Installed() with
        {
            Shares = Reading.Ok<IReadOnlyList<ShareInfo>>(new[]
            {
                new ShareInfo("Shared", @"E:\Other", "Someone else's", false, 1, 0, false),
                new ShareInfo("Media", @"D:\Shared", "", false, 1, 0, false),
                new ShareInfo("ADMIN$", @"C:\Windows", "Remote Admin", false, 1, 0, true),
            }),
        };

        var plan = PlanFor(s, Fixture());
        Assert.Equal(PlanAction.Ask, Item(plan, "share.foreign:Shared").Action);
        Assert.Equal(PlanAction.Ask, Item(plan, "share.foreign:Media").Action);
        Assert.Equal(PlanAction.Ask, Item(plan, "share.adminshares").Action);
        Assert.DoesNotContain(plan.Items, i => i.Section == PlanSection.Share && i.Action == PlanAction.RemoveIfOurs);
        Assert.All(plan.Items.Where(i => i.Action == PlanAction.Ask), i => Assert.False(i.Ticked));
    }

    [Fact]
    public void ATickedForeignShareWithTheSameNameIsReplacedByCpfsOwn()
    {
        var s = Installed() with
        {
            Shares = Reading.Ok<IReadOnlyList<ShareInfo>>(new[] { new ShareInfo("Shared", @"E:\Other", "Someone else's", false, 1, 0, false) }),
        };

        var plan = PlanFor(s, Fixture(), a => Tick(a, "share.foreign:Shared"));
        var keys = plan.Items.Select(i => i.Key).ToList();
        Assert.Equal(PlanAction.Create, Item(plan, "share.main").Action);
        Assert.True(keys.IndexOf("share.foreign:Shared") < keys.IndexOf("share.main"));
        Assert.Null(PlanFor(s, Fixture()).Find("share.main"));
    }

    [Fact]
    public void AShortcutTheEarlierInstallMadeAndTheUserDeletedComesBackOnlyWhenTicked()
    {
        var s = Installed() with { PublicDesktopFiles = Reading.Ok<IReadOnlyList<string>>(new[] { "desktop.ini" }) };

        var plan = PlanFor(s, Fixture());
        Assert.Equal(PlanAction.Ask, Item(plan, "shortcut.file").Action);
        Assert.False(Item(plan, "shortcut.file").Ticked);
        Assert.Equal(Planner.R.ShortcutGone, Item(plan, "shortcut.file").ReasonKey);
        Assert.True(Item(PlanFor(s, Fixture(), a => Tick(a, "shortcut.file")), "shortcut.file").Ticked);

        // A first Setup that asks for a shortcut creates it.
        Assert.Equal(PlanAction.Create, Item(PlanFor(s, null), "shortcut.file").Action);
    }

    [Fact]
    public void ADriveTheEarlierInstallMadeAndTheUserRemovedComesBackOnlyWhenTicked()
    {
        var doc = Fixture();
        doc.Root["DriveLetter"] = "X";
        doc.Root["Access"] = 3;
        var config = ConfigReader.Parse(ConfigWriter.Serialize(doc.Root));

        var plan = PlanFor(Installed(), config);
        Assert.Equal(PlanAction.Ask, Item(plan, "drive.map").Action);
        Assert.False(Item(plan, "drive.map").Ticked);
        Assert.Equal(Planner.R.DriveGone, Item(plan, "drive.map").ReasonKey);
    }

    [Fact]
    public void ARenamedShareRemovesOnlyTheOldOneCpfsMade()
    {
        var plan = PlanFor(Installed(), Fixture(), a => SetupDefaults.FollowShareName(a, a.ShareName, "Files"));

        Assert.Equal(PlanAction.Create, Item(plan, "share.main").Action);
        Assert.Equal(PlanAction.RemoveIfOurs, Item(plan, "share.old:Shared").Action);

        // The shortcut name was the default for the old share name, so it follows the new one.
        Assert.Equal(PlanAction.Create, Item(plan, "shortcut.file").Action);
        Assert.Equal(PlanAction.RemoveIfOurs, Item(plan, "shortcut.old").Action);
    }

    [Fact]
    public void AnAccountCpfsDidNotCreateIsUsedOnlyWhenTicked()
    {
        var plan = PlanFor(Installed(), Fixture(), a => a with { SmbUser = "Backup" });
        Assert.Equal(PlanAction.Ask, Item(plan, "user.takeover").Action);
        Assert.Null(plan.Find("user.enabled"));

        var s = Installed() with { User = Reading.Ok<UserInfo?>(new UserInfo("S-1-5-21-1-2-3-1006", false, false, false, true, true, null)) };
        var ticked = PlanFor(s, Fixture(), a => Tick(a with { SmbUser = "Backup" }, "user.takeover"));
        Assert.True(Item(ticked, "user.takeover").BackupNeeded);
        Assert.Equal(PlanAction.Change, Item(ticked, "user.enabled").Action);
        Assert.True(Item(ticked, "user.enabled").BackupNeeded);
        Assert.Equal(PlanAction.Ask, Item(ticked, "user.admin").Action);
        Assert.True(Item(ticked, "user.admin").Ticked);
    }

    [Theory]
    [InlineData(PasswordEntry.Empty, PlanAction.Keep)]
    [InlineData(PasswordEntry.Valid, PlanAction.Change)]
    [InlineData(PasswordEntry.Mismatch, PlanAction.CannotPlan)]
    public void ThePlanOnlySaysWhetherThePasswordIsSet(PasswordEntry entry, PlanAction expected)
    {
        var item = Item(PlanFor(Installed(), Fixture(), a => a with { Password = entry }), "user.password");

        Assert.Equal(expected, item.Action);
        Assert.Null(item.Current);
    }

    [Fact]
    public void TypingThePasswordOnAnInstallAtTargetGivesOneRunnableItemOnly()
    {
        var installed = PlanFor(Installed(), Fixture());
        Assert.DoesNotContain(ApplyList.Setup(installed), e => !e.Deferred);

        var plan = PlanFor(Installed(), Fixture(), a => a with { Password = PasswordEntry.Valid });
        var run = ApplyList.Setup(plan).Where(e => !e.Deferred).ToList();
        Assert.Single(run);
        Assert.Equal("user.password", run[0].Item.Key);
        Assert.Equal(PlanAction.Change, run[0].Item.Action);
        Assert.False(run[0].Item.BackupNeeded);
        Assert.Empty(ApplyList.Blocking(plan));
    }

    [Fact]
    public void ANewAccountNeedsAPassword()
    {
        var s = Installed() with { User = Reading.Ok<UserInfo?>(null) };

        var plan = PlanFor(s, null);
        Assert.Equal(PlanAction.Create, Item(plan, "user.account").Action);
        Assert.Equal(PlanCause.InvalidAnswer, Item(plan, "user.password").Cause);
        Assert.Equal(PlanAction.Create, Item(PlanFor(s, null, a => a with { Password = PasswordEntry.Valid }), "user.password").Action);
    }

    // NetUserAdd's defaults differ between Windows editions, so nothing about a new account is left to them.
    [Fact]
    public void ANewAccountPlansEveryPropertyExplicitly()
    {
        var plan = PlanFor(Installed() with { User = Reading.Ok<UserInfo?>(null) }, null, a => a with { Password = PasswordEntry.Valid });
        var run = ApplyList.Setup(plan).Select(e => e.Item.Key).ToList();

        foreach (var key in new[] { "user.account", "user.enabled", "user.neverexpires", "user.users", "user.admin", "user.hidden", "user.password" })
        {
            Assert.Equal(PlanAction.Create, Item(plan, key).Action);
            Assert.Contains(key, run);
        }

        Assert.True(run.IndexOf("user.account") < run.IndexOf("user.users"));
        Assert.Equal(PlanValue.Word(Planner.W.NotMember), Item(plan, "user.admin").Target);
    }

    [Fact]
    public void UnelevatedReadsThatNeedAnAdministratorCannotBePlanned()
    {
        var s = Installed() with
        {
            Elevated = false,
            SmbServer = Reading.NeedsAdmin<SmbServerInfo>(),
            AuditLogon = Reading.NeedsAdmin<AuditSetting>(),
        };

        var plan = PlanFor(s, Fixture());
        foreach (var key in new[] { "server.smb2", "server.reject", "server.smb1", "hardening.audit" })
        {
            Assert.Equal(PlanAction.CannotPlan, Item(plan, key).Action);
            Assert.Equal(PlanCause.NeedsAdmin, Item(plan, key).Cause);
        }
    }

    [Fact]
    public void TheDriveLetterMustBeFreeAndCpfsOwnLetterCountsAsFree()
    {
        var letters = new LetterScan();
        letters.Add('C', LetterSource.SystemDrive);
        letters.Add('X', LetterSource.DosDevices);
        letters.Add('Y', LetterSource.UserNetwork);
        var dos = new Dictionary<char, string> { ['X'] = @"\??\D:\Shared" };
        var s = Installed() with
        {
            Letters = letters,
            DosDevices = Reading.Ok<IReadOnlyDictionary<char, string>>(dos),
            DriveLabels = Reading.Ok<IReadOnlyDictionary<char, string>>(new Dictionary<char, string> { ['X'] = "CPFS - Shared" }),
        };
        var config = WithDrive('X');

        var keep = PlanFor(s, config, a => a with { Drive = true });
        Assert.Equal('X', SetupDefaults.From(config, letters).DriveLetter);
        Assert.Equal(PlanAction.Keep, Item(keep, "drive.map").Action);
        Assert.Equal(PlanAction.Keep, Item(keep, "drive.label").Action);

        var taken = PlanFor(s, config, a => a with { Drive = true, DriveLetter = 'Y' });
        Assert.Equal(PlanCause.InvalidAnswer, Item(taken, "drive.map").Cause);

        var moved = PlanFor(s, config, a => a with { Drive = true, DriveLetter = 'S' });
        Assert.Equal(PlanAction.Create, Item(moved, "drive.map").Action);
        Assert.Equal(PlanAction.RemoveIfOurs, Item(moved, "drive.oldmap").Action);
        Assert.Equal(PlanAction.RemoveIfOurs, Item(moved, "drive.oldlabel").Action);
    }

    [Fact]
    public void AnOldDriveMappingThatIsNotCpfsIsLeftAlone()
    {
        var s = Installed() with
        {
            DosDevices = Reading.Ok<IReadOnlyDictionary<char, string>>(new Dictionary<char, string> { ['X'] = @"\??\E:\Elsewhere" }),
        };

        var plan = PlanFor(s, WithDrive('X'), a => a with { Drive = false });
        Assert.Equal(PlanAction.Keep, Item(plan, "drive.oldmap").Action);
        Assert.Equal(Planner.R.DriveOldForeign, Item(plan, "drive.oldmap").ReasonKey);
    }

    [Fact]
    public void AShortcutNameTakenByAnotherFileIsRefused()
    {
        var s = Installed() with
        {
            PublicDesktopFiles = Reading.Ok<IReadOnlyList<string>>(new[] { "Shared (CPFS).lnk", "Files.lnk" }),
        };

        var item = Item(PlanFor(s, Fixture(), a => a with { ShortcutName = "Files" }), "shortcut.file");
        Assert.Equal(PlanAction.CannotPlan, item.Action);
        Assert.Equal(Planner.R.ShortcutForeign, item.ReasonKey);
    }

    [Fact]
    public void AnInvalidAnswerIsShownInsteadOfPlanned()
    {
        var plan = PlanFor(Installed(), Fixture(), a => a with { SharePath = @"C:\Windows\Temp", ShareName = "bad name" });

        Assert.Equal(PlanCause.InvalidAnswer, Item(plan, "folder.exists").Cause);
        Assert.Equal(PlanCause.InvalidAnswer, Item(plan, "share.main").Cause);
    }

    [Fact]
    public void OnlyTheSmbAccountKeepsShareAccessAndOthersAreAsked()
    {
        var s = Installed() with
        {
            ShareAccess = Reading.Ok<IReadOnlyList<ShareAccessEntry>?>(new[]
            {
                new ShareAccessEntry(@"HOST-01\ShareUser", UserSid, 0, 1),
                new ShareAccessEntry("Everyone", "S-1-1-0", 0, 2),
                new ShareAccessEntry("Everyone", "S-1-1-0", 0, 0),
                new ShareAccessEntry("Guests", "S-1-5-32-546", 1, 0),
                new ShareAccessEntry("Orphan", null, 0, 3),
            }),
        };

        var plan = PlanFor(s, Fixture());
        Assert.Equal(PlanAction.Change, Item(plan, "share.access").Action);
        Assert.Equal(Planner.R.ShareAccessGrant, Item(plan, "share.access").ReasonKey);
        Assert.Equal(PlanAction.Ask, Item(plan, "share.access:S-1-1-0").Action);
        Assert.False(Item(plan, "share.access:S-1-1-0").Ticked);
        Assert.Equal(PlanAction.Ask, Item(plan, "share.access:Orphan").Action);
        Assert.Single(plan.Items, i => i.Key == "share.access:S-1-1-0");
        Assert.DoesNotContain(plan.Items, i => i.Key == "share.access:S-1-5-32-546");

        var denied = Installed() with
        {
            ShareAccess = Reading.Ok<IReadOnlyList<ShareAccessEntry>?>(new[]
            {
                new ShareAccessEntry(@"HOST-01\ShareUser", UserSid, 0, 0),
                new ShareAccessEntry(@"HOST-01\ShareUser", UserSid, 1, 0),
            }),
        };
        var deny = PlanFor(denied, Fixture());
        Assert.Equal(PlanAction.Keep, Item(deny, "share.access").Action);
        Assert.Equal(Planner.R.ShareAccessDeny, Item(deny, "share.access:" + UserSid).ReasonKey);

        var created = PlanFor(Installed() with { User = Reading.Ok<UserInfo?>(null) }, Fixture(), a => a with { Password = PasswordEntry.Valid });
        Assert.Equal(PlanAction.Create, Item(created, "share.access").Action);

        var unread = PlanFor(Installed() with { Elevated = false, ShareAccess = Reading.NeedsAdmin<IReadOnlyList<ShareAccessEntry>?>() }, Fixture());
        Assert.Equal(PlanCause.NeedsAdmin, Item(unread, "share.access").Cause);
    }

    [Fact]
    public void TheLiveComputerNameIsKeptAndASavedNameThatDiffersIsOnlyShown()
    {
        var same = PlanFor(Installed(), Fixture());
        Assert.Equal(PlanAction.Keep, Item(same, "computer.name").Action);
        Assert.Null(same.Find("computer.saved"));

        var renamed = PlanFor(Installed() with { ComputerNames = Reading.Ok(new ComputerNameInfo("LAPTOP", null)) }, Fixture());
        Assert.Equal(PlanAction.Keep, Item(renamed, "computer.name").Action);
        Assert.Equal(Planner.R.AtTarget, Item(renamed, "computer.name").ReasonKey);
        Assert.Equal(PlanAction.Keep, Item(renamed, "computer.saved").Action);
        Assert.Equal(Planner.R.ComputerSavedDiffers, Item(renamed, "computer.saved").ReasonKey);
        Assert.Equal(new[] { "HOST-01", "LAPTOP" }, Item(renamed, "computer.saved").ReasonArgs);
        Assert.DoesNotContain(renamed.Items, i => i.Section == PlanSection.Computer && i.Action != PlanAction.Keep);

        var pending = PlanFor(Installed() with { ComputerNames = Reading.Ok(new ComputerNameInfo("LAPTOP", "HOST-01")) }, Fixture());
        Assert.Equal(Planner.R.ComputerPending, Item(pending, "computer.name").ReasonKey);
        Assert.Null(pending.Find("computer.saved"));

        Assert.Null(PlanFor(Installed() with { ComputerNames = Reading.Ok(new ComputerNameInfo("LAPTOP", null)) }, null).Find("computer.saved"));
    }

    [Fact]
    public void TheLogListsEveryItemThatIsNotKeepWithoutItsArguments()
    {
        var s = Installed() with
        {
            ShareAccess = Reading.Ok<IReadOnlyList<ShareAccessEntry>?>(new[]
            {
                new ShareAccessEntry(@"HOST-01\ShareUser", UserSid, 0, 0),
                new ShareAccessEntry("Everyone", "S-1-1-0", 0, 2),
            }),
            AuditLogon = Reading.NeedsAdmin<AuditSetting>(),
        };

        var plan = PlanFor(s, Fixture());
        var lines = plan.LogLines();
        Assert.Equal(plan.Items.Count(i => i.Action != PlanAction.Keep), lines.Count);
        Assert.Contains("Ask share.access:S-1-1-0: " + Planner.R.ShareAccessOther, lines);
        Assert.Contains("CannotPlan/NeedsAdmin hardening.audit: " + Planner.R.NeedsAdmin, lines);
        Assert.DoesNotContain(lines, l => l.Contains("Everyone", StringComparison.Ordinal));
    }

    [Fact]
    public void FirewallAddressesCompareInMaskOrPrefixForm()
    {
        Assert.Equal(
            FirewallRules.NormalizeAddresses("100.64.0.0/10, FD7A:115C:A1E0::/48"),
            FirewallRules.NormalizeAddresses("fd7a:115c:a1e0::/48,100.64.0.0/255.192.0.0"));
        Assert.Equal(new[] { "LocalSubnet" }, FirewallRules.NormalizeAddresses("localsubnet"));
        Assert.Empty(FirewallRules.NormalizeAddresses("*"));
    }

    [Fact]
    public void EveryReasonAndWordHasAnEnglishString()
    {
        var english = XDocument.Load(Path.Combine(TestFiles.RepoRoot, "src", "Cpfs", "Strings", "Strings.resx")).Root!
            .Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet();

        Assert.DoesNotContain(Planner.ReasonKeys, k => !english.Contains(k));
        Assert.DoesNotContain(Planner.WordKeys, k => !english.Contains(k));
        Assert.DoesNotContain(UninstallPlanner.ReasonKeys, k => !english.Contains(k));
        Assert.DoesNotContain(UninstallPlanner.WordKeys, k => !english.Contains(k));
        foreach (var section in Enum.GetNames<PlanSection>())
        {
            Assert.Contains("Plan_Section_" + section, english);
        }
    }

    [Fact]
    public void EveryItemUsesAKnownReason()
    {
        var plans = new[]
        {
            PlanFor(Installed(), Fixture()),
            PlanFor(Installed() with { User = Reading.Ok<UserInfo?>(null), Lockout = Reading.Ok(new LockoutInfo(0, 0, 0)) }, null),
            PlanFor(Installed() with { Shares = Reading.Failed<IReadOnlyList<ShareInfo>>("boom") }, Fixture()),
        };

        foreach (var item in plans.SelectMany(p => p.Items))
        {
            Assert.Contains(item.ReasonKey, Planner.ReasonKeys);
        }
    }

    [Theory]
    [InlineData("0.5.0-dev1+3d20e71b5c0a8f1e2d3c4b5a69788796a5b4c3d2", "0.5.0-dev1 (3d20e71)")]
    [InlineData("0.5.0", "0.5.0")]
    [InlineData("0.5.0-dev1+", "0.5.0-dev1")]
    public void TheVersionShowsAShortCommit(string informational, string expected)
    {
        Assert.Equal(expected, VersionText.Display(informational));
    }

    [Theory]
    [InlineData("0.5.0-dev3+3d20e71b5c0a8f1e2d3c4b5a69788796a5b4c3d2", "0.5.0-dev3")]
    [InlineData("0.5.0", "0.5.0")]
    public void TheVersionNumberHasNoCommit(string informational, string expected)
    {
        Assert.Equal(expected, VersionText.Number(informational));
    }

    private static ConfigDocument WithDrive(char letter)
    {
        var text = System.Text.Encoding.UTF8.GetString(TestFiles.ConfigBytes("v0.2.3-anonymised.json"))
            .Replace("\"DriveLetter\":  null", "\"DriveLetter\":  \"" + letter + "\"", StringComparison.Ordinal)
            .Replace("\"Access\":  2", "\"Access\":  3", StringComparison.Ordinal)
            .Replace("\"DriveLabel\":  null", "\"DriveLabel\":  \"CPFS - Shared\"", StringComparison.Ordinal);
        return ConfigReader.Parse(System.Text.Encoding.UTF8.GetBytes(text));
    }
}
