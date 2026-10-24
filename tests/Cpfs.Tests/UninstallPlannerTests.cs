using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Cpfs.Core.Apply;
using Cpfs.Core.Config;
using Cpfs.Core.Drives;
using Cpfs.Core.Planning;
using Cpfs.Core.Validation;
using Xunit;

namespace Cpfs.Tests;

public class UninstallPlannerTests
{
    private const string UserSid = "S-1-5-21-1-2-3-1005";

    private static ConfigDocument Fixture() => ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.3-anonymised.json"));

    // The machine the 0.2.3 fixture describes after Setup: CPFS's share and account, its two LAN rules, hardening at
    // CPFS's values, no shortcut on the desktop.
    private static SystemSnapshot Installed() => new()
    {
        Elevated = true,
        WindowsUserNames = new[] { "Owner" },
        ComputerName = "HOST-01",
        ComputerNames = Reading.Ok(new ComputerNameInfo("HOST-01", null)),
        Shares = Reading.Ok<IReadOnlyList<ShareInfo>>(new[]
        {
            new ShareInfo("Shared", @"D:\Shared", Planner.ShareDescription, false, 0, 0, false),
            new ShareInfo("Media", @"E:\Media", "Mine", false, 1, 0, false),
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
            new FirewallRuleInfo(FirewallRules.LanRule, true, 1, 1, 6, "445", 2, "LocalSubnet", Array.Empty<string>()),
            new FirewallRuleInfo(FirewallRules.MdnsRule, true, 1, 1, 17, "5353", 2, "*", Array.Empty<string>()),
        }),
        BuiltInRules = Reading.Ok<IReadOnlyList<BuiltInRuleInfo>>(new[]
        {
            new BuiltInRuleInfo("FPS-SMB-In-TCP", "File and Printer Sharing (SMB-In)", false, 1, 4, new[] { "445" }),
        }),
        Networks = Reading.Ok<IReadOnlyList<NetworkInfo>>(new[] { new NetworkInfo("Home", "Wi-Fi", 1) }),
        Tasks = Reading.Ok<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>
        {
            [Planner.AutoPrivateTask] = false, [Planner.RunAsUserTask] = false, [Planner.LegacyAutoPrivateTask] = false,
        }),
        DataFolder = Reading.Ok(new DataFolderInfo(true, true, true, false)),
        DosDevices = Reading.Ok<IReadOnlyDictionary<char, string>>(new Dictionary<char, string>()),
        DriveLabels = Reading.Ok<IReadOnlyDictionary<char, string>>(new Dictionary<char, string>()),
        DriveIcons = Reading.Ok<IReadOnlyDictionary<char, string>>(new Dictionary<char, string>()),
        Letters = new LetterScan(),
        PublicDesktopFiles = Reading.Ok<IReadOnlyList<string>>(new[] { "desktop.ini" }),
        User = Reading.Ok<UserInfo?>(new UserInfo(UserSid, true, true, false, false, true, 0)),
        Folder = Reading.Ok(new FolderInfo(true, true)),
        ShareAccess = Reading.Ok<IReadOnlyList<ShareAccessEntry>?>(Array.Empty<ShareAccessEntry>()),
    };

    // The fixture with a built-in rule and a network recorded, as a 0.2.x Setup on a Public Wi-Fi leaves it.
    private static ConfigDocument WithTracked()
    {
        var doc = Fixture();
        var tracked = (JsonObject)ConfigJson.Get(doc.Root, "Tracked")!;
        tracked["FirewallRules"] = new JsonArray("FPS-SMB-In-TCP");
        tracked["Networks"] = new JsonArray("Home");
        return ConfigReader.Parse(ConfigWriter.Serialize(doc.Root));
    }

    private static Plan PlanFor(SystemSnapshot s, ConfigDocument? c, params (string Key, bool Ticked)[] choices) =>
        UninstallPlanner.Uninstall(new UninstallInput
        {
            Snapshot = s,
            Config = c,
            DesktopUser = @"HOST-01\Owner",
            ProfilePath = @"C:\Users\ShareUser",
            Choices = choices.ToDictionary(c => c.Key, c => c.Ticked),
        });

    private static PlanItem Item(Plan plan, string key) => plan.Find(key) ?? throw new InvalidOperationException("No plan item " + key);

    [Fact]
    public void AScriptInstallIsUndoneInTheScriptsOrderWithTheDataFolderLast()
    {
        var plan = PlanFor(Installed(), WithTracked());
        var run = ApplyList.Uninstall(plan).Select(e => e.Item.Key).ToList();

        Assert.Equal(PlanAction.RemoveIfOurs, Item(plan, "u.share:Shared").Action);
        Assert.Null(plan.Find("u.share:Media"));
        Assert.True(Item(plan, "u.owner").Ticked);
        Assert.True(Item(plan, "u.user.delete").Ticked);
        Assert.False(Item(plan, "u.user.profile").Ticked);
        Assert.Equal(PlanAction.Change, Item(plan, "u.builtin:FPS-SMB-In-TCP").Action);
        Assert.True(Item(plan, "u.net:Home").Ticked);
        Assert.Equal("u.data", run[^1]);
        Assert.DoesNotContain(UninstallPlanner.RestoreKey, run);
        Assert.True(run.IndexOf("u.share:Shared") < run.IndexOf("u.owner"));
        Assert.True(run.IndexOf("u.owner") < run.IndexOf("u.user.delete"));
        Assert.True(run.IndexOf("u.user.profile") < 0);

        // The fixture's original values: Smb2 on, RejectUnencrypted off, Manual start, AutoShareWks absent.
        Assert.Equal(PlanAction.Change, Item(plan, "u.reject").Action);
        Assert.Equal(PlanAction.Keep, Item(plan, "u.smb2").Action);
        Assert.Equal(PlanAction.Change, Item(plan, "u.serverstart").Action);
        Assert.Equal(PlanAction.Change, Item(plan, "u.reg:AutoShareWks").Action);
        Assert.Equal(PlanAction.Keep, Item(plan, "u.reg:NoLMHash").Action);
        Assert.Null(plan.Find("u.lockout"));
        Assert.Equal(PlanAction.Keep, Item(plan, "u.keep").Action);
        Assert.Empty(ApplyList.Blocking(plan));
    }

    [Fact]
    public void DecliningTheRestoreKeepsTheSettingsSavesACopyAndStillTurnsTheBuiltInRulesOn()
    {
        var plan = PlanFor(Installed(), WithTracked(), (UninstallPlanner.RestoreKey, false));

        Assert.DoesNotContain(plan.Items, i => i.Key.StartsWith("u.reg:", StringComparison.Ordinal));
        Assert.Equal(PlanAction.Keep, Item(plan, "u.settings").Action);
        Assert.Equal(PlanAction.Create, Item(plan, "u.savecopy").Action);
        Assert.Equal(PlanAction.Change, Item(plan, "u.builtin:FPS-SMB-In-TCP").Action);
        Assert.False(Item(plan, "u.net:Home").Ticked);
        var run = ApplyList.Uninstall(plan).Select(e => e.Item.Key).ToList();
        Assert.True(run.IndexOf("u.savecopy") < run.IndexOf("u.data"));
    }

    [Fact]
    public void AnAccountThatSignsInToWindowsStopsTheWholeUninstall()
    {
        var plan = PlanFor(Installed() with { WindowsUserNames = new[] { "ShareUser" } }, Fixture());

        Assert.Equal(PlanAction.CannotPlan, Item(plan, "u.guard").Action);
        Assert.Single(plan.Items);
        Assert.NotEmpty(ApplyList.Blocking(plan));
    }

    [Fact]
    public void AnAccountThatExistedBeforeIsKeptByDefaultAndItsProfileIsNeverOffered()
    {
        var doc = Fixture();
        var users = (JsonArray)ConfigJson.Get((JsonObject)ConfigJson.Get(doc.Root, "Tracked")!, "Users")!;
        ((JsonObject)users[0]!)["Existed"] = true;
        ((JsonObject)users[0]!)["WasAdmin"] = true;
        var config = ConfigReader.Parse(ConfigWriter.Serialize(doc.Root));

        var plan = PlanFor(Installed(), config);
        Assert.False(Item(plan, "u.user.delete").Ticked);
        Assert.False(Item(plan, "u.user.admin").Ticked);
        Assert.Equal(PlanAction.RemoveIfOurs, Item(plan, "u.user.hidden").Action);

        var deleting = PlanFor(Installed(), config, ("u.user.delete", true));
        Assert.Null(deleting.Find("u.user.profile"));
    }

    [Fact]
    public void WithoutABackupWindowsDefaultsAreOfferedUnticked()
    {
        var plan = PlanFor(Installed(), ConfigReader.Parse(TestFiles.ConfigBytes("v0.1.json")));

        Assert.Equal(PlanAction.Ask, Item(plan, "u.revert").Action);
        Assert.False(Item(plan, "u.revert").Ticked);
        Assert.Equal(PlanAction.Create, Item(plan, "u.savecopy").Action);
        Assert.Null(plan.Find(UninstallPlanner.RestoreKey));

        // A 0.1 install's configured account counts as created by CPFS.
        Assert.NotNull(plan.Find("u.user.profile"));
    }

    [Fact]
    public void NothingIsInstalledOnlyWithoutConfigAndWithoutAnythingOfCpfsFound()
    {
        var none = Installed() with { OwnRules = Reading.Ok<IReadOnlyList<FirewallRuleInfo>>(Array.Empty<FirewallRuleInfo>()) };
        var empty = PlanFor(none, null);
        Assert.True(UninstallPlanner.NothingInstalled(empty));
        Assert.Equal(new[] { "Summary_U_Nothing" }, PlanSummary.Uninstall(empty).Lines.Select(l => l.Key));

        // A CPFS rule left behind, rules that could not be read, or any install at all keep the plan on screen.
        Assert.False(UninstallPlanner.NothingInstalled(PlanFor(Installed(), null)));
        Assert.False(UninstallPlanner.NothingInstalled(PlanFor(new SystemSnapshot { Elevated = true }, null)));
        Assert.False(UninstallPlanner.NothingInstalled(PlanFor(Installed(), WithTracked())));
    }

    [Fact]
    public void TheRecordedShortcutIsRemovedAndTheDataFolderGoesLast()
    {
        var s = Installed() with { PublicDesktopFiles = Reading.Ok<IReadOnlyList<string>>(new[] { "Shared (CPFS).lnk" }) };
        var plan = PlanFor(s, Fixture());

        var entries = ApplyList.Uninstall(plan);
        Assert.False(entries.Single(e => e.Item.Key == "u.shortcut").Deferred);
        Assert.Equal(PlanAction.RemoveIfOurs, Item(plan, "u.data").Action);
        Assert.Equal("u.data", entries[entries.Count - 1].Item.Key);
    }

    // What Setup records for the drive (letter, the folder it points to, its name) is exactly what Uninstall removes.
    [Fact]
    public void UninstallRemovesExactlyTheDriveSetupRecorded()
    {
        var book = ConfigBook.ForSetup(Fixture());
        book.SetField("DriveLetter", "X");
        book.TrackCreated("DrivePath", @"E:\Moved");
        book.SetField("DriveLabel", "Photos");
        var config = ConfigReader.Parse(book.Serialize("0.6.0-test", new DateTime(2026, 10, 24, 12, 0, 0)));
        Assert.Equal(@"E:\Moved", config.OwnDrivePath);

        var s = Installed() with
        {
            DosDevices = Reading.Ok<IReadOnlyDictionary<char, string>>(new Dictionary<char, string> { ['X'] = @"\??\E:\Moved", ['Y'] = @"\??\D:\Shared" }),
            DriveLabels = Reading.Ok<IReadOnlyDictionary<char, string>>(new Dictionary<char, string> { ['X'] = "Photos", ['Y'] = "CPFS - Shared" }),
        };
        var plan = PlanFor(s, config);
        var keys = ApplyList.Uninstall(plan).Select(e => e.Item.Key).Where(k => k.StartsWith("u.drive", StringComparison.Ordinal)).ToList();

        Assert.Equal(new[] { "u.drive.label", "u.drive.session", "u.drive" }, keys);
        Assert.All(keys, k => Assert.Equal(PlanAction.RemoveIfOurs, Item(plan, k).Action));
        Assert.Equal(@"X: \??\E:\Moved", Item(plan, "u.drive").Current!.Data);
    }

    // A leftover CPFS-RunAsUser task is CPFS's and goes; Auto-Private's tasks are still left for a later version.
    [Fact]
    public void UninstallRemovesAStaleRunAsUserTask()
    {
        var s = Installed() with
        {
            Tasks = Reading.Ok<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>
            {
                [Planner.AutoPrivateTask] = true, [Planner.RunAsUserTask] = true, [Planner.LegacyAutoPrivateTask] = false,
            }),
        };
        var plan = PlanFor(s, Fixture());

        var item = Item(plan, "u.task.runasuser");
        Assert.Equal(PlanAction.RemoveIfOurs, item.Action);
        Assert.Contains(ApplyList.Uninstall(plan), e => e.Item.Key == "u.task.runasuser" && !e.Deferred);
        Assert.DoesNotContain(plan.Items, i => i.Key == "u.task:" + Planner.RunAsUserTask);
        Assert.Contains(plan.Items, i => i.Key == "u.task:" + Planner.AutoPrivateTask);
    }

    [Fact]
    public void AMappingOrNameThatIsNoLongerCpfsIsLeft()
    {
        var book = ConfigBook.ForSetup(Fixture());
        book.SetField("DriveLetter", "X");
        var config = ConfigReader.Parse(book.Serialize("0.6.0-test", new DateTime(2026, 10, 24, 12, 0, 0)));
        var s = Installed() with
        {
            DosDevices = Reading.Ok<IReadOnlyDictionary<char, string>>(new Dictionary<char, string> { ['X'] = @"\??\E:\Elsewhere" }),
            DriveLabels = Reading.Ok<IReadOnlyDictionary<char, string>>(new Dictionary<char, string> { ['X'] = "CPFS - Somebody else" }),
        };

        var plan = PlanFor(s, config);

        Assert.Equal(PlanAction.Keep, Item(plan, "u.drive").Action);
        Assert.DoesNotContain(plan.Items, i => i.Key is "u.drive.session" or "u.drive.label");
    }

    // The scripts before 0.2.3 recorded no drive name and always wrote "CPFS - <share>": that one is CPFS's.
    [Fact]
    public void AnOlderConfigWithoutADriveNameUsesTheScriptsDefaultName()
    {
        var book = ConfigBook.ForSetup(Fixture());
        book.SetField("DriveLetter", "X");
        book.SetField("DriveLabel", null);
        var config = ConfigReader.Parse(book.Serialize("0.6.0-test", new DateTime(2026, 10, 24, 12, 0, 0)));
        var s = Installed() with
        {
            DosDevices = Reading.Ok<IReadOnlyDictionary<char, string>>(new Dictionary<char, string> { ['X'] = @"\??\D:\Shared" }),
            DriveLabels = Reading.Ok<IReadOnlyDictionary<char, string>>(new Dictionary<char, string> { ['X'] = "CPFS - Shared" }),
        };

        Assert.Equal(PlanAction.RemoveIfOurs, Item(PlanFor(s, config), "u.drive.label").Action);
    }

    [Fact]
    public void ADriveNameThatWasThereBeforeCpfsIsPutBack()
    {
        var book = ConfigBook.ForSetup(Fixture());
        book.SetField("DriveLetter", "X");
        Assert.Equal(BackupWrite.Recorded, book.BackupDriveLabel('X', "Music"));
        book.SetField("DriveLabel", "CPFS - Shared");
        var config = ConfigReader.Parse(book.Serialize("0.6.0-test", new DateTime(2026, 10, 24, 12, 0, 0)));
        var s = Installed() with
        {
            DriveLabels = Reading.Ok<IReadOnlyDictionary<char, string>>(new Dictionary<char, string> { ['X'] = "CPFS - Shared" }),
        };

        var item = Item(PlanFor(s, config), "u.drive.label");
        Assert.Equal(PlanAction.Change, item.Action);
        Assert.Equal("Music", item.Target!.Data);
    }

    // The File Explorer entry goes only while it opens the recorded folder; when its user's hive is not loaded it
    // stays, and the summary says so.
    [Fact]
    public void TheExplorerEntryIsRemovedOnlyWhenItOpensTheRecordedFolder()
    {
        var entry = ExplorerEntryTests.Entry("CPFS - Shared", @"D:\Shared");
        Assert.Null(PlanFor(Installed() with { ExplorerEntry = ExplorerEntryTests.Hive(entry) }, Fixture()).Find("u.explorer"));

        var recorded = ExplorerEntryTests.Recorded(Fixture(), @"D:\Shared", "CPFS - Shared");
        var ours = Item(PlanFor(Installed() with { ExplorerEntry = ExplorerEntryTests.Hive(entry with { TargetFolderPath = @"D:\Shared\" }) }, recorded), "u.explorer");
        Assert.Equal(PlanAction.RemoveIfOurs, ours.Action);
        Assert.Equal(PlanSection.Drive, ours.Section);

        var elsewhere = Item(PlanFor(Installed() with { ExplorerEntry = ExplorerEntryTests.Hive(entry with { TargetFolderPath = @"E:\Other" }) }, recorded), "u.explorer");
        Assert.Equal(PlanAction.Keep, elsewhere.Action);
        Assert.Equal(UninstallPlanner.R.ExplorerForeign, elsewhere.ReasonKey);

        Assert.Null(PlanFor(Installed() with { ExplorerEntry = ExplorerEntryTests.Hive(null) }, recorded).Find("u.explorer"));

        var notLoadedPlan = PlanFor(Installed() with { ExplorerEntry = ExplorerEntryTests.Hive(null, loaded: false) }, recorded);
        var notLoaded = Item(notLoadedPlan, "u.explorer");
        Assert.Equal(PlanAction.Keep, notLoaded.Action);
        Assert.Equal(UninstallPlanner.R.ExplorerNotLoaded, notLoaded.ReasonKey);
        Assert.Contains(PlanSummary.Uninstall(notLoadedPlan).Lines, l => l.Key == "Summary_U_ExplorerLeft");
        Assert.Empty(ApplyList.Blocking(notLoadedPlan));

        var otherUser = PlanFor(Installed() with { ExplorerEntry = ExplorerEntryTests.Hive(entry, "S-1-5-21-9-9-9-1002") }, recorded);
        Assert.Equal(UninstallPlanner.R.ExplorerNotLoaded, Item(otherUser, "u.explorer").ReasonKey);
    }

    // The CPFS icon goes, the icon from before CPFS comes back, and an icon someone else set since stays.
    [Fact]
    public void TheDriveIconIsRemovedOrPutBackAndAForeignOneStays()
    {
        var book = ConfigBook.ForSetup(Fixture());
        book.SetField("DriveLetter", "X");
        var plain = ConfigReader.Parse(book.Serialize("0.6.0-test", new DateTime(2026, 10, 24, 12, 0, 0)));
        Assert.Equal(BackupWrite.Recorded, book.BackupDriveIcon('X', @"C:\Icons\music.ico"));
        var backedUp = ConfigReader.Parse(book.Serialize("0.6.0-test", new DateTime(2026, 10, 24, 12, 0, 0)));
        SystemSnapshot WithIcon(string? icon) => Installed() with
        {
            DriveIcons = Reading.Ok<IReadOnlyDictionary<char, string>>(icon is null ? new Dictionary<char, string>() : new Dictionary<char, string> { ['X'] = icon }),
        };
        const string Own = @"C:\ProgramData\CPFS\cpfs.ico";

        Assert.Equal(PlanAction.RemoveIfOurs, Item(PlanFor(WithIcon(Own), plain), "u.drive.icon").Action);
        var put = Item(PlanFor(WithIcon(Own), backedUp), "u.drive.icon");
        Assert.Equal(PlanAction.Change, put.Action);
        Assert.Equal(@"C:\Icons\music.ico", put.Target!.Data);
        Assert.Null(PlanFor(WithIcon(@"C:\Icons\other.ico"), plain).Find("u.drive.icon"));
        Assert.Null(PlanFor(WithIcon(null), plain).Find("u.drive.icon"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACopyOfConfigJsonIsSavedFirstWhetherOrNotTheSettingsAreRestored(bool restore)
    {
        var plan = PlanFor(Installed(), WithTracked(), (UninstallPlanner.RestoreKey, restore));

        Assert.Equal("u.savecopy", ApplyList.Uninstall(plan)[0].Item.Key);
    }

    [Fact]
    public void AShareWithCpfsNameOnAnotherFolderIsRemovedOnlyWhenTicked()
    {
        var s = Installed() with
        {
            Shares = Reading.Ok<IReadOnlyList<ShareInfo>>(new[] { new ShareInfo("Shared", @"E:\Other", "Mine", false, 1, 0, false) }),
        };

        var plan = PlanFor(s, Fixture());
        Assert.Equal(PlanAction.Ask, Item(plan, "u.share:Shared").Action);
        Assert.False(Item(plan, "u.share:Shared").Ticked);
        Assert.DoesNotContain(ApplyList.Uninstall(plan), e => e.Item.Key == "u.share:Shared");

        // CPFS's own folder under the same name, or CPFS's description on any share, is CPFS's.
        var own = Installed() with
        {
            Shares = Reading.Ok<IReadOnlyList<ShareInfo>>(new[]
            {
                new ShareInfo("Shared", @"D:\Shared\", "Mine", false, 1, 0, false),
                new ShareInfo("Old", @"E:\Old", Planner.ShareDescription, false, 1, 0, false),
            }),
        };
        var ownPlan = PlanFor(own, Fixture());
        Assert.Equal(PlanAction.RemoveIfOurs, Item(ownPlan, "u.share:Shared").Action);
        Assert.Equal(PlanAction.RemoveIfOurs, Item(ownPlan, "u.share:Old").Action);
    }

    [Fact]
    public void AnAccountConfigJsonNeverRecordedIsKeptByDefault()
    {
        var doc = Fixture();
        ((JsonObject)ConfigJson.Get(doc.Root, "Tracked")!)["Users"] = new JsonArray();
        var plan = PlanFor(Installed(), ConfigReader.Parse(ConfigWriter.Serialize(doc.Root)));

        Assert.Equal(PlanAction.Ask, Item(plan, "u.user.delete").Action);
        Assert.False(Item(plan, "u.user.delete").Ticked);
        Assert.Equal("Uninst_R_UserDeleteUntracked", Item(plan, "u.user.delete").ReasonKey);
        Assert.Null(plan.Find("u.user.profile"));

        // Recorded as created by CPFS: deleted unless unticked.
        Assert.True(Item(PlanFor(Installed(), Fixture()), "u.user.delete").Ticked);
    }

    [Fact]
    public void ABackedUpValueWithoutAValueIsLeftAsItIsNeverWrittenAsZero()
    {
        var doc = Fixture();
        var registry = (JsonArray)ConfigJson.Get((JsonObject)ConfigJson.Get(doc.Root, "Backup")!, "Registry")!;
        var entry = registry.OfType<JsonObject>().Single(e => (string?)e["Name"] == "RestrictAnonymous");
        entry["Exists"] = true;
        entry["Value"] = null;
        var s = Installed();
        s = s with { Hardening = new Dictionary<string, Reading<int?>>(s.Hardening) { ["RestrictAnonymous"] = Reading.Ok<int?>(1) } };

        var plan = PlanFor(s, ConfigReader.Parse(ConfigWriter.Serialize(doc.Root)));
        var item = Item(plan, "u.reg:RestrictAnonymous");
        Assert.Equal(PlanAction.Keep, item.Action);
        Assert.True(UninstallPlanner.IsUnknownOriginal(item));
        Assert.DoesNotContain(ApplyList.Uninstall(plan), e => e.Item.Key == "u.reg:RestrictAnonymous");
        Assert.Empty(ApplyList.Blocking(plan));
    }

    [Theory]
    [InlineData(@"C:\ProgramData\CPFS")]
    [InlineData(@"C:\Windows\Temp\Shared")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Users")]
    [InlineData(@"Shared")]
    public void ASharePathSetupWouldRefuseStopsTheWholeUninstall(string path)
    {
        var doc = Fixture();
        doc.Root["SharePath"] = path;
        var plan = UninstallPlanner.Uninstall(new UninstallInput
        {
            Snapshot = Installed(),
            Config = ConfigReader.Parse(ConfigWriter.Serialize(doc.Root)),
            ProtectedFolders = new ProtectedFolders
            {
                Trees = { @"C:\Windows", @"C:\Program Files", @"C:\ProgramData" },
                Exact = { @"C:\Users", @"C:\Users\Public" },
            },
        });

        Assert.Equal(PlanAction.CannotPlan, Item(plan, "u.guard").Action);
        Assert.Single(plan.Items);
        Assert.NotEmpty(ApplyList.Blocking(plan));
    }

    [Fact]
    public void ARestoredLockoutUsesTheBackedUpThreshold()
    {
        var doc = Fixture();
        ((JsonObject)ConfigJson.Get(doc.Root, "Backup")!)["Lockout"] = 0;
        var plan = PlanFor(Installed(), ConfigReader.Parse(ConfigWriter.Serialize(doc.Root)));

        Assert.Equal(PlanAction.Change, Item(plan, "u.lockout").Action);
        Assert.Equal(Planner.W.Off, Item(plan, "u.lockout").Target!.WordKey);
    }
}
