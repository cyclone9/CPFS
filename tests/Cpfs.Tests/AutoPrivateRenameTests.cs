using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Cpfs.Core.Apply;
using Cpfs.Core.Config;
using Cpfs.Core.Planning;
using Cpfs.Core.Validation;
using Xunit;

namespace Cpfs.Tests;

// Auto-Private (the SYSTEM task, its exe copy, the skip list and the verb's choice of networks) and the computer
// rename, in Setup and Uninstall.
public class AutoPrivateRenameTests
{
    private const string Exe = @"C:\Program Files\CPFS\Cpfs.exe";
    private const string Folder = @"C:\Program Files\CPFS";
    private const string OldExe = @"C:\ProgramData\CPFS\Cpfs.exe";

    private static ScheduledTaskInfo Wanted() => new(
        AutoPrivateTaskSpec.SystemSid,
        AutoPrivateTaskSpec.LogonServiceAccount,
        AutoPrivateTaskSpec.RunLevelHighest,
        new[] { new TaskTriggerInfo(AutoPrivateTaskSpec.TriggerEvent, AutoPrivateTaskSpec.Subscription, AutoPrivateTaskSpec.Delay) },
        new[] { new TaskActionInfo(AutoPrivateTaskSpec.ActionExec, Exe, AutoPrivateTaskSpec.Arguments) },
        AutoPrivateTaskSpec.ExecutionTimeLimit,
        AutoPrivateTaskSpec.InstancesIgnoreNew);

    // The copy's folder exists and is locked whenever the copy does, unless the test says otherwise.
    private static SystemSnapshot Machine(ScheduledTaskInfo? task = null, AutoPrivateFiles? files = null, bool taskListed = false, AutoPrivateFolderInfo? folder = null) =>
        PlannerTests.Installed() with
        {
            Join = Reading.Ok(new ComputerJoin(false, false)),
            AutoPrivateTask = Reading.Ok(task),
            AutoPrivateFiles = Reading.Ok(files ?? new AutoPrivateFiles(false, false, false)),
            AutoPrivateFolder = Reading.Ok(folder ?? new AutoPrivateFolderInfo(files?.ExeExists == true, files?.ExeExists == true, false)),
            AutoPrivateExe = Exe,
            AutoPrivateFolderPath = Folder,
            AutoPrivateOldExe = OldExe,
            Tasks = Reading.Ok<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>
            {
                [Planner.AutoPrivateTask] = taskListed || task is not null, [Planner.RunAsUserTask] = false, [Planner.LegacyAutoPrivateTask] = false,
            }),
        };

    private static Plan Setup(SystemSnapshot s, ConfigDocument? config, Func<SetupAnswers, SetupAnswers> change) =>
        Planner.Setup(s, change(SetupDefaults.From(config, s.Letters)), config, PlannerTests.Protected);

    private static PlanItem Item(Plan plan, string key) => plan.Find(key) ?? throw new InvalidOperationException("No plan item " + key);

    private static ConfigDocument With(ConfigDocument doc, Action<JsonObject> change)
    {
        var root = (JsonObject)doc.Root.DeepClone();
        change(root);
        return ConfigReader.Parse(ConfigWriter.Serialize(root));
    }

    [Fact]
    public void SwitchedOnTheCopyComesBeforeTheTaskAndBothAreCreated()
    {
        var plan = Setup(Machine(), PlannerTests.Fixture(), a => a with { AutoPrivate = true });
        Assert.Equal(PlanAction.Create, Item(plan, "auto.exe").Action);
        Assert.Equal(PlanAction.Create, Item(plan, "auto.task").Action);
        Assert.Equal(PlanAction.Create, Item(plan, "auto.folder").Action);
        var keys = ApplyList.Setup(plan).Select(e => e.Item.Key).ToList();
        Assert.True(keys.IndexOf("data.folder") < keys.IndexOf("auto.exe") || !keys.Contains("data.folder"));
        Assert.True(keys.IndexOf("auto.folder") < keys.IndexOf("auto.exe"));
        Assert.True(keys.IndexOf("auto.exe") < keys.IndexOf("auto.task"));
        Assert.Empty(ApplyList.Blocking(plan));
    }

    [Fact]
    public void TheCopysFolderIsLockedAndAFolderCpfsDidNotCreateIsSaidToStay()
    {
        var locked = Setup(Machine(folder: new AutoPrivateFolderInfo(true, true, false)), PlannerTests.Fixture(), a => a with { AutoPrivate = true });
        Assert.Equal(PlanAction.Keep, Item(locked, "auto.folder").Action);

        var foreign = Setup(Machine(folder: new AutoPrivateFolderInfo(true, false, true)), PlannerTests.Fixture(), a => a with { AutoPrivate = true });
        Assert.Equal(PlanAction.Change, Item(foreign, "auto.folder").Action);
        Assert.Equal(Planner.R.AutoFolderForeign, Item(foreign, "auto.folder").ReasonKey);

        var ours = With(PlannerTests.Fixture(), r =>
            ((JsonObject)r["Tracked"]!)["Created"] = new JsonObject { ["AutoPrivateFolder"] = new JsonObject { ["Path"] = Folder, ["CreatedByCpfs"] = true } });
        var loosened = Setup(Machine(folder: new AutoPrivateFolderInfo(true, false, false)), ours, a => a with { AutoPrivate = true });
        Assert.Equal(Planner.R.AutoFolderLock, Item(loosened, "auto.folder").ReasonKey);

        var unread = Setup(Machine() with { AutoPrivateFolder = Reading.Failed<AutoPrivateFolderInfo>("x") }, PlannerTests.Fixture(), a => a with { AutoPrivate = true });
        Assert.Contains(ApplyList.Blocking(unread), i => i.Key == "auto.folder");
        Assert.Null(unread.Find("auto.exe"));
    }

    [Fact]
    public void TheDev3CopyInProgramDataGoesAfterTheTaskPointsElsewhere()
    {
        var dev3Task = Wanted() with { Actions = new[] { new TaskActionInfo(AutoPrivateTaskSpec.ActionExec, OldExe, AutoPrivateTaskSpec.Arguments) } };
        var on = Setup(Machine(dev3Task, new AutoPrivateFiles(false, false, false, OldExe: true)), PlannerTests.Fixture(), a => a with { AutoPrivate = true });
        Assert.Equal(PlanAction.Change, Item(on, "auto.task").Action);
        Assert.Equal(PlanAction.RemoveIfOurs, Item(on, "auto.exe.old").Action);
        var keys = ApplyList.Setup(on).Select(e => e.Item.Key).ToList();
        Assert.True(keys.IndexOf("auto.exe") < keys.IndexOf("auto.task") && keys.IndexOf("auto.task") < keys.IndexOf("auto.exe.old"));

        var off = Setup(Machine(dev3Task, new AutoPrivateFiles(false, false, false, OldExe: true)), PlannerTests.Fixture(), a => a with { AutoPrivate = false });
        var offKeys = ApplyList.Setup(off).Select(e => e.Item.Key).ToList();
        Assert.True(offKeys.IndexOf("auto.task") < offKeys.IndexOf("auto.exe.old"));

        Assert.Null(Setup(Machine(), PlannerTests.Fixture(), a => a with { AutoPrivate = true }).Find("auto.exe.old"));
    }

    [Fact]
    public void ACurrentCopyAndAMatchingTaskAreKeptAndADifferentOneIsChanged()
    {
        var same = Setup(Machine(Wanted(), new AutoPrivateFiles(true, true, false)), PlannerTests.Fixture(), a => a with { AutoPrivate = true });
        Assert.Equal(PlanAction.Keep, Item(same, "auto.exe").Action);
        Assert.Equal(PlanAction.Keep, Item(same, "auto.task").Action);

        // The 0.2.x task: PowerShell running set-private.ps1, no delay. Its script goes too.
        var legacy = Wanted() with
        {
            Actions = new[] { new TaskActionInfo(0, "powershell.exe", @"-File C:\ProgramData\CPFS\set-private.ps1") },
            Triggers = new[] { new TaskTriggerInfo(0, AutoPrivateTaskSpec.Subscription, null) },
        };
        var old = Setup(Machine(legacy, new AutoPrivateFiles(true, false, true)), PlannerTests.Fixture(), a => a with { AutoPrivate = true });
        Assert.Equal(PlanAction.Change, Item(old, "auto.exe").Action);
        Assert.Equal(PlanAction.Change, Item(old, "auto.task").Action);
        Assert.Equal("Trigger, Action", Item(old, "auto.task").ReasonArgs[0]);
        Assert.Equal(PlanAction.RemoveIfOurs, Item(old, "auto.script").Action);
    }

    [Fact]
    public void SwitchedOffTheTaskGoesBeforeTheCopyAndAnUnrecordedCopyStays()
    {
        var recorded = With(PlannerTests.Fixture(), r =>
            ((JsonObject)r["Tracked"]!)["Created"] = new JsonObject { ["AutoPrivateExe"] = new JsonObject { ["Path"] = Exe, ["Sha256"] = "ab" } });
        var off = Setup(Machine(Wanted(), new AutoPrivateFiles(true, true, false)), recorded, a => a with { AutoPrivate = false });
        var keys = ApplyList.Setup(off).Select(e => e.Item.Key).ToList();
        Assert.Equal(PlanAction.RemoveIfOurs, Item(off, "auto.task").Action);
        Assert.Equal(PlanAction.RemoveIfOurs, Item(off, "auto.exe").Action);
        Assert.True(keys.IndexOf("auto.task") < keys.IndexOf("auto.exe"));

        var unrecorded = Setup(Machine(Wanted(), new AutoPrivateFiles(true, true, false)), PlannerTests.Fixture(), a => a with { AutoPrivate = false });
        Assert.Null(unrecorded.Find("auto.exe"));

        var nothing = Setup(Machine(), PlannerTests.Fixture(), a => a with { AutoPrivate = false });
        Assert.Equal(PlanAction.Keep, Item(nothing, "auto.task").Action);
    }

    [Fact]
    public void AutoPrivateCannotBePlannedWithoutTheDataFolderTheFilesOrTheTask()
    {
        foreach (var s in new[]
        {
            Machine() with { DataFolder = Reading.Failed<DataFolderInfo>("x") },
            Machine() with { AutoPrivateFiles = Reading.NeedsAdmin<AutoPrivateFiles>() },
            Machine() with { AutoPrivateTask = Reading.Failed<ScheduledTaskInfo?>("x") },
        })
        {
            var plan = Setup(s, PlannerTests.Fixture(), a => a with { AutoPrivate = true });
            Assert.Contains(ApplyList.Blocking(plan), i => i.Section == PlanSection.AutoPrivate);
        }
    }

    [Fact]
    public void TheSkipListKeepsEarlierNamesAddsUntickedPublicNetworksAndDropsTickedOnes()
    {
        var s = Machine() with
        {
            Networks = Reading.Ok<IReadOnlyList<NetworkInfo>>(new[]
            {
                new NetworkInfo("Home", "Wi-Fi", 1), new NetworkInfo("Cafe", "Wi-Fi 2", 0), new NetworkInfo("Hotel", "Ethernet", 0),
            }),
        };
        var config = With(PlannerTests.Fixture(), r => r["AutoPrivateSkip"] = new JsonArray("Airport", "Hotel"));
        var plan = Setup(s, config, a => a with { AutoPrivate = true, Choices = new Dictionary<string, bool> { ["net:Hotel"] = true } });
        var skip = AutoPrivateRules.SkipList(config.AutoPrivateSkip, plan);
        Assert.Equal(new[] { "Airport", "Cafe" }, skip);
        Assert.Equal(PlanAction.Change, Item(plan, "auto.skip").Action);
        var keys = ApplyList.Setup(plan).Select(e => e.Item.Key).ToList();
        Assert.True(keys.IndexOf("auto.skip") < keys.IndexOf("auto.task"));

        // The same list again: nothing to write.
        var again = Setup(s, With(config, r => r["AutoPrivateSkip"] = new JsonArray("Airport", "Cafe", "Hotel")), a => a with { AutoPrivate = true });
        Assert.Null(again.Find("auto.skip"));
    }

    [Fact]
    public void TheVerbSwitchesOnlyASavedPublicNetworkCpfsHasNeverSeen()
    {
        var config = With(PlannerTests.Fixture(), r =>
        {
            r["AutoPrivate"] = true;
            r["AutoPrivateSkip"] = new JsonArray("Left");
            ((JsonObject)r["Tracked"]!)["Networks"] = new JsonArray("Tracked");
        });
        var profiles = new[]
        {
            new NetworkInfo("Home", "Wi-Fi", 1),
            new NetworkInfo("Tracked", "Wi-Fi", 0),
            new NetworkInfo("left", "Wi-Fi", 0),
            new NetworkInfo("Logged", "Wi-Fi", 0),
            new NetworkInfo("Tailnet", "Tailscale", 0),
            new NetworkInfo("Unidentified network", "Ethernet", 0),
            new NetworkInfo("New", "Wi-Fi", 0),
        };
        var saved = new[] { "Home", "Tracked", "Left", "Logged", "Tailnet", "New" };
        var d = AutoPrivateRules.Decide(profiles, config, new[] { "Logged" }, new[] { "Tailscale" }, saved);
        Assert.Equal(
            new[]
            {
                AutoPrivateVerdict.NotPublic, AutoPrivateVerdict.Tracked, AutoPrivateVerdict.LeftPublic, AutoPrivateVerdict.Logged,
                AutoPrivateVerdict.Vpn, AutoPrivateVerdict.NotSaved, AutoPrivateVerdict.Switch,
            },
            d.Select(x => x.Verdict));
        Assert.Equal("Wi-Fi", d[^1].InterfaceAlias);

        Assert.True(AutoPrivateRules.Enabled(config));
        Assert.False(AutoPrivateRules.Enabled(PlannerTests.Fixture()));
        Assert.False(AutoPrivateRules.Enabled(null));
    }

    [Fact]
    public void TheLogIsReadInBothEncodingsAndWrittenAsUtf8WithABom()
    {
        var first = AutoPrivateLog.Append(null, "Café");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, first.Take(3));
        var both = first.Concat(AutoPrivateLog.Append(first, "Hotel")).ToArray();
        Assert.Contains("Café", AutoPrivateLog.Names(both, Encoding.Latin1));
        Assert.Contains("Hotel", AutoPrivateLog.Names(both, Encoding.Latin1));

        // A 0.2.x line in the ANSI code page, without a final line break.
        var ansi = Encoding.Latin1.GetBytes("Caf\u00e9");
        Assert.Contains("Café", AutoPrivateLog.Names(ansi, Encoding.Latin1));
        Assert.StartsWith("\r\n", Encoding.UTF8.GetString(AutoPrivateLog.Append(ansi, "x")), StringComparison.Ordinal);

        var run = "";
        for (var i = 0; i < AutoPrivateLog.RunLogLines + 5; i++)
        {
            run = AutoPrivateLog.RunLog(run, "line " + i);
        }

        var lines = run.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(AutoPrivateLog.RunLogLines, lines.Length);
        Assert.Equal("line " + (AutoPrivateLog.RunLogLines + 4), lines[^1]);
    }

    [Fact]
    public void TheTaskDefinitionIsComparedFieldByField()
    {
        Assert.Empty(AutoPrivateTaskSpec.Differences(Wanted(), Exe));
        var quoted = Wanted() with
        {
            Actions = new[] { new TaskActionInfo(0, "\"" + Exe.ToLowerInvariant() + "\"", AutoPrivateTaskSpec.Arguments) },
            Triggers = new[] { new TaskTriggerInfo(0, AutoPrivateTaskSpec.Subscription.Replace('"', '\'').Replace("><", ">\n  <", StringComparison.Ordinal), AutoPrivateTaskSpec.Delay) },
        };
        Assert.Empty(AutoPrivateTaskSpec.Differences(quoted, Exe));

        var user = Wanted() with { UserSid = "S-1-5-21-1-2-3-1001", LogonType = 3, RunLevel = 0, ExecutionTimeLimit = "PT72H", MultipleInstances = 0 };
        Assert.Equal(new[] { "Principal", "LogonType", "RunLevel", "ExecutionTimeLimit", "MultipleInstances" }, AutoPrivateTaskSpec.Differences(user, Exe));
        Assert.Equal(new[] { "Action" }, AutoPrivateTaskSpec.Differences(Wanted(), @"C:\Users\Owner\Downloads\Cpfs.exe"));
    }

    [Fact]
    public void ARenameIsPlannedWithTheRestartAndRecordedInTheBackup()
    {
        var plan = Setup(Machine(), PlannerTests.Fixture(), a => a with { ComputerName = "LAPTOP" });
        var item = Item(plan, "computer.name");
        Assert.Equal(PlanAction.Change, item.Action);
        Assert.True(item.BackupNeeded);
        Assert.Equal("LAPTOP", item.Target?.Data);
        Assert.Equal(Planner.R.ComputerRename, item.ReasonKey);
        Assert.Equal("computer.name", ApplyList.Setup(plan)[^1].Item.Key);
        Assert.Contains(PlanSummary.Setup(plan, SetupDefaults.From(PlannerTests.Fixture(), Machine().Letters) with { ComputerName = "LAPTOP" }, PlannerTests.Fixture()).Lines,
            l => l.Key == "Summary_Rename" && (string)l.Args[0] == "LAPTOP");

        // A case-only change, or an empty box, keeps the name.
        Assert.Equal(PlanAction.Keep, Item(Setup(Machine(), PlannerTests.Fixture(), a => a with { ComputerName = "host-01" }), "computer.name").Action);
        Assert.Equal(PlanAction.Keep, Item(Setup(Machine(), PlannerTests.Fixture(), a => a with { ComputerName = "" }), "computer.name").Action);

        // A first install takes the backup first.
        var fresh = Setup(Machine() with { DataFolder = Reading.Ok(new DataFolderInfo(false, false, false, false)) }, null, a => a with { ComputerName = "LAPTOP" });
        Assert.Equal(PlanAction.Change, Item(fresh, "computer.name").Action);
    }

    [Fact]
    public void ARenameIsRefusedOrAskedWhenItIsNotCpfssToMake()
    {
        Assert.Equal(PlanCause.InvalidAnswer, Item(Setup(Machine(), PlannerTests.Fixture(), a => a with { ComputerName = "12345" }), "computer.name").Cause);
        Assert.Equal(PlanCause.InvalidAnswer, Item(Setup(Machine(), PlannerTests.Fixture(), a => a with { ComputerName = "ShareUser" }), "computer.name").Cause);

        var domain = Setup(Machine() with { Join = Reading.Ok(new ComputerJoin(true, false)) }, PlannerTests.Fixture(), a => a with { ComputerName = "LAPTOP" });
        Assert.Equal(PlanAction.Keep, Item(domain, "computer.name").Action);
        Assert.Equal(Planner.R.ComputerDomain, Item(domain, "computer.name").ReasonKey);

        var v01 = ConfigReader.Parse(TestFiles.ConfigBytes("v0.1.json"));
        var noBackup = Setup(Machine(), v01, a => a with { ComputerName = "LAPTOP" });
        Assert.Equal(Planner.R.ComputerNoBackup, Item(noBackup, "computer.name").ReasonKey);

        var entra = Setup(Machine() with { Join = Reading.Ok(new ComputerJoin(false, true)) }, PlannerTests.Fixture(), a => a with { ComputerName = "LAPTOP" });
        Assert.Equal(PlanAction.Ask, Item(entra, "computer.name").Action);
        Assert.False(Item(entra, "computer.name").Ticked);
        Assert.DoesNotContain(ApplyList.Setup(entra), e => e.Item.Key == "computer.name");
        var ticked = Setup(Machine() with { Join = Reading.Ok(new ComputerJoin(false, true)) }, PlannerTests.Fixture(),
            a => a with { ComputerName = "LAPTOP", Choices = new Dictionary<string, bool> { ["computer.name"] = true } });
        Assert.Contains(ApplyList.Setup(ticked), e => e.Item.Key == "computer.name");

        var unread = Setup(Machine() with { Join = Reading.Failed<ComputerJoin>("x") }, PlannerTests.Fixture(), a => a with { ComputerName = "LAPTOP" });
        Assert.Equal(PlanAction.CannotPlan, Item(unread, "computer.name").Action);
        Assert.Equal(new[] { "computer.name" }, ApplyList.Blocking(unread).Select(i => i.Key));
    }

    [Fact]
    public void ARenameThatIsNotRequestedNeverDependsOnTheJoinRead()
    {
        var noJoin = Machine() with { Join = Reading.Failed<ComputerJoin>("Win32Exception") };
        foreach (var name in new[] { "", "HOST-01", "host-01" })
        {
            var plan = Setup(noJoin, PlannerTests.Fixture(), a => a with { ComputerName = name });
            Assert.Equal(PlanAction.Keep, Item(plan, "computer.name").Action);
            Assert.DoesNotContain(ApplyList.Blocking(plan), i => i.Key == "computer.name");
        }

        var pending = noJoin with { ComputerNames = Reading.Ok(new ComputerNameInfo("HOST-01", "LAPTOP")) };
        var same = Setup(pending, PlannerTests.Fixture(), a => a with { ComputerName = "LAPTOP" });
        Assert.Equal(PlanAction.Keep, Item(same, "computer.name").Action);
        Assert.Equal(Planner.R.ComputerPending, Item(same, "computer.name").ReasonKey);
    }

    [Fact]
    public void AnEntraJoinThatCouldNotBeReadAsksUntickedInsteadOfBlocking()
    {
        var unknown = Machine() with { Join = Reading.Ok(new ComputerJoin(false, null)) };
        var plan = Setup(unknown, PlannerTests.Fixture(), a => a with { ComputerName = "LAPTOP" });
        var item = Item(plan, "computer.name");
        Assert.Equal(PlanAction.Ask, item.Action);
        Assert.False(item.Ticked);
        Assert.Equal(Planner.R.ComputerEntraUnknown, item.ReasonKey);
        Assert.Equal("LAPTOP", item.ReasonArgs[0]);
        Assert.Empty(ApplyList.Blocking(plan));
        Assert.DoesNotContain(ApplyList.Setup(plan), e => e.Item.Key == "computer.name");

        var ticked = Setup(unknown, PlannerTests.Fixture(), a => a with { ComputerName = "LAPTOP", Choices = new Dictionary<string, bool> { ["computer.name"] = true } });
        Assert.Contains(ApplyList.Setup(ticked), e => e.Item.Key == "computer.name");

        // A domain member stays a Keep whatever the Entra read gave.
        var domain = Setup(Machine() with { Join = Reading.Ok(new ComputerJoin(true, null)) }, PlannerTests.Fixture(), a => a with { ComputerName = "LAPTOP" });
        Assert.Equal(Planner.R.ComputerDomain, Item(domain, "computer.name").ReasonKey);
    }

    [Fact]
    public void APendingRenameIsKeptAndAThirdNameChangesIt()
    {
        var pending = Machine() with { ComputerNames = Reading.Ok(new ComputerNameInfo("HOST-01", "LAPTOP")), ComputerName = "LAPTOP" };
        var same = Setup(pending, PlannerTests.Fixture(), a => a with { ComputerName = "LAPTOP" });
        Assert.Equal(PlanAction.Keep, Item(same, "computer.name").Action);
        Assert.Equal(Planner.R.ComputerPending, Item(same, "computer.name").ReasonKey);
        Assert.Contains(PlanSummary.Setup(same, SetupDefaults.From(PlannerTests.Fixture(), pending.Letters), PlannerTests.Fixture()).Lines,
            l => l.Key == "Summary_RenamePending");

        var third = Setup(pending, PlannerTests.Fixture(), a => a with { ComputerName = "DESK" });
        Assert.Equal(PlanAction.Change, Item(third, "computer.name").Action);
        Assert.Equal("LAPTOP", Item(third, "computer.name").Current?.Data);
    }

    [Fact]
    public void TheSummarySaysAutoPrivateIsOnWheneverItIs()
    {
        var plan = Setup(Machine(Wanted(), new AutoPrivateFiles(true, true, false)), PlannerTests.Fixture(),
            a => a with { AutoPrivate = true, Password = PasswordEntry.Valid });
        var summary = PlanSummary.Setup(plan, SetupDefaults.From(PlannerTests.Fixture(), Machine().Letters) with { AutoPrivate = true, Password = PasswordEntry.Valid },
            PlannerTests.Fixture());
        Assert.Contains(summary.Lines, l => l.Key == "Summary_AutoPrivateOn");
    }

    [Fact]
    public void ComputerNamesFollowMicrosoftsRulesAndNeverTheSmbUser()
    {
        Assert.Equal(ValidationError.ReservedName, Validators.ComputerName("system").Error);
        Assert.Equal(ValidationError.ReservedName, Validators.ComputerName("Users").Error);
        Assert.Equal(ValidationError.SameAsSmbUser, Validators.ComputerName("cyclone", "CyClone").Error);
        Assert.Equal(ValidationError.None, Validators.ComputerName("CPFS-CI-ABCD", "CyClone").Error);
        Assert.Equal(ValidationError.AllDigits, Validators.ComputerName("2026").Error);
    }

    private static UninstallInput Uninstall(SystemSnapshot s, ConfigDocument c, params (string Key, bool Ticked)[] choices) => new()
    {
        Snapshot = s,
        Config = c,
        DesktopUser = @"HOST-01\Owner",
        Choices = choices.ToDictionary(x => x.Key, x => x.Ticked),
    };

    private static SystemSnapshot Renamed(string current) => UninstallPlannerTests.Installed() with
    {
        ComputerNames = Reading.Ok(new ComputerNameInfo("HOST-01", current)),
        Join = Reading.Ok(new ComputerJoin(false, false)),
    };

    [Fact]
    public void UninstallOffersTheOldNameTickedOnlyWhenCpfsSetTheCurrentOne()
    {
        // The app recorded LAPTOP; the PC still waits for it.
        var app = With(PlannerTests.Fixture(), r =>
        {
            r["HostName"] = "LAPTOP";
            ((JsonObject)r["Tracked"]!)["ComputerNames"] = new JsonArray("LAPTOP");
        });
        var plan = UninstallPlanner.Uninstall(Uninstall(Renamed("LAPTOP"), app));
        var item = Item(plan, "u.computer");
        Assert.Equal(PlanAction.Ask, item.Action);
        Assert.True(item.Ticked);
        Assert.Equal("HOST-01", item.Target?.Data);
        Assert.Contains(PlanSummary.Uninstall(plan).Lines, l => l.Key == "Summary_U_Rename");
        Assert.True(ApplyList.Uninstall(plan).Select(e => e.Item.Key).ToList().IndexOf("u.computer") < ApplyList.Uninstall(plan).Select(e => e.Item.Key).ToList().IndexOf("u.data"));

        // Renamed since by someone else: asked, unticked.
        var since = Item(UninstallPlanner.Uninstall(Uninstall(Renamed("DESK"), app)), "u.computer");
        Assert.False(since.Ticked);
        Assert.Equal(UninstallPlanner.R.ComputerRenamedSince, since.ReasonKey);

        // A 0.2.x config has no Tracked.ComputerNames: its HostName is the name the script set.
        var script = With(PlannerTests.Fixture(), r => r["HostName"] = "LAPTOP");
        Assert.True(Item(UninstallPlanner.Uninstall(Uninstall(Renamed("LAPTOP"), script)), "u.computer").Ticked);
        Assert.False(Item(UninstallPlanner.Uninstall(Uninstall(Renamed("DESK"), script)), "u.computer").Ticked);

        // Not restoring: never ticked by default.
        Assert.False(Item(UninstallPlanner.Uninstall(Uninstall(Renamed("LAPTOP"), app, (UninstallPlanner.RestoreKey, false))), "u.computer").Ticked);

        // Same name: nothing to offer. An invalid backup is shown, never set.
        Assert.Null(UninstallPlanner.Uninstall(Uninstall(UninstallPlannerTests.Installed(), app)).Find("u.computer"));
        var bad = With(app, r => ((JsonObject)r["Backup"]!)["HostName"] = "12345");
        Assert.Equal(PlanAction.Keep, Item(UninstallPlanner.Uninstall(Uninstall(Renamed("LAPTOP"), bad)), "u.computer").Action);
    }

    [Fact]
    public void UninstallRemovesTheTasksBeforeTheDataFolderAndNoLongerKeepsIt()
    {
        var s = UninstallPlannerTests.Installed() with
        {
            Tasks = Reading.Ok<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>
            {
                [Planner.AutoPrivateTask] = true, [Planner.RunAsUserTask] = false, [Planner.LegacyAutoPrivateTask] = true,
            }),
        };
        var plan = UninstallPlanner.Uninstall(Uninstall(s, PlannerTests.Fixture()));
        var keys = ApplyList.Uninstall(plan).Select(e => e.Item.Key).ToList();
        Assert.Contains("u.task:" + Planner.AutoPrivateTask, keys);
        Assert.Contains("u.task:" + Planner.LegacyAutoPrivateTask, keys);
        Assert.Equal("u.data", keys[^1]);
        Assert.True(keys.IndexOf("u.task:" + Planner.AutoPrivateTask) < keys.IndexOf("u.data"));
        Assert.Equal(PlanAction.RemoveIfOurs, Item(plan, "u.data").Action);

        var unread = UninstallPlanner.Uninstall(Uninstall(s with { Tasks = Reading.Failed<IReadOnlyDictionary<string, bool>>("x") }, PlannerTests.Fixture()));
        Assert.Contains(ApplyList.Blocking(unread), i => i.Key == "u.task");
    }

    private static SystemSnapshot WithCopy(AutoPrivateFolderInfo folder, bool exe = true) => UninstallPlannerTests.Installed() with
    {
        Tasks = Reading.Ok<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>
        {
            [Planner.AutoPrivateTask] = true, [Planner.RunAsUserTask] = false, [Planner.LegacyAutoPrivateTask] = false,
        }),
        AutoPrivateFiles = Reading.Ok(new AutoPrivateFiles(exe, exe, false)),
        AutoPrivateFolder = Reading.Ok(folder),
        AutoPrivateExe = Exe,
        AutoPrivateFolderPath = Folder,
        AutoPrivateOldExe = OldExe,
    };

    private static ConfigDocument CopyRecorded(string exePath, bool? folderOurs) => With(PlannerTests.Fixture(), r =>
    {
        var created = new JsonObject { ["AutoPrivateExe"] = new JsonObject { ["Path"] = exePath, ["Sha256"] = "ab" } };
        if (folderOurs is { } ours)
        {
            created["AutoPrivateFolder"] = new JsonObject { ["Path"] = Folder, ["CreatedByCpfs"] = ours };
        }

        ((JsonObject)r["Tracked"]!)["Created"] = created;
    });

    [Fact]
    public void UninstallRemovesTheCopyAfterTheTaskThenItsFolderOnlyWhenCpfsCreatedItAndItIsEmpty()
    {
        var plan = UninstallPlanner.Uninstall(Uninstall(WithCopy(new AutoPrivateFolderInfo(true, true, false)), CopyRecorded(Exe, true)));
        Assert.Equal(PlanAction.RemoveIfOurs, Item(plan, "u.auto.exe").Action);
        Assert.Equal(PlanAction.RemoveIfOurs, Item(plan, "u.auto.folder").Action);
        var keys = ApplyList.Uninstall(plan).Select(e => e.Item.Key).ToList();
        Assert.True(keys.IndexOf("u.task:" + Planner.AutoPrivateTask) < keys.IndexOf("u.auto.exe"));
        Assert.True(keys.IndexOf("u.auto.exe") < keys.IndexOf("u.auto.folder"));
        Assert.Equal("u.data", keys[^1]);

        var foreign = UninstallPlanner.Uninstall(Uninstall(WithCopy(new AutoPrivateFolderInfo(true, true, false)), CopyRecorded(Exe, false)));
        Assert.Equal(PlanAction.Keep, Item(foreign, "u.auto.folder").Action);
        Assert.Equal(UninstallPlanner.R.AutoFolderForeign, Item(foreign, "u.auto.folder").ReasonKey);
        Assert.Equal(PlanAction.RemoveIfOurs, Item(foreign, "u.auto.exe").Action);

        var notEmpty = UninstallPlanner.Uninstall(Uninstall(WithCopy(new AutoPrivateFolderInfo(true, true, true)), CopyRecorded(Exe, true)));
        Assert.Equal(UninstallPlanner.R.AutoFolderNotEmpty, Item(notEmpty, "u.auto.folder").ReasonKey);

        // The dev3 copy in ProgramData\CPFS goes with u.data; an unrecorded copy in Program Files keeps its folder.
        var dev3 = UninstallPlanner.Uninstall(Uninstall(WithCopy(new AutoPrivateFolderInfo(true, true, false)), CopyRecorded(OldExe, true)));
        Assert.Null(dev3.Find("u.auto.exe"));
        Assert.Equal(UninstallPlanner.R.AutoFolderNotEmpty, Item(dev3, "u.auto.folder").ReasonKey);

        var gone = UninstallPlanner.Uninstall(Uninstall(WithCopy(new AutoPrivateFolderInfo(false, false, false), exe: false), CopyRecorded(Exe, true)));
        Assert.Null(gone.Find("u.auto.exe"));
        Assert.Null(gone.Find("u.auto.folder"));

        var unread = UninstallPlanner.Uninstall(Uninstall(WithCopy(new AutoPrivateFolderInfo(true, true, false)) with
        {
            AutoPrivateFolder = Reading.Failed<AutoPrivateFolderInfo>("x"),
        }, CopyRecorded(Exe, true)));
        Assert.Contains(ApplyList.Blocking(unread), i => i.Key == "u.auto.folder");
    }

    [Fact]
    public void TheCopysFolderIsRecordedAndOnlyTheCopyRecordedAtAPathIsForgotten()
    {
        var book = ConfigBook.ForSetup(PlannerTests.Fixture());
        book.TrackAutoPrivateFolder(Folder, true);
        book.TrackAutoPrivateExe(Exe, "abc");
        book.ForgetAutoPrivateExe(OldExe);
        var doc = ConfigReader.Parse(book.Serialize("0.7.0-test", new DateTime(2026, 10, 24, 12, 0, 0)));
        Assert.Equal(new TrackedFolder(Folder, true), doc.TrackedAutoPrivateFolder);
        Assert.Equal(new TrackedFile(Exe, "abc"), doc.TrackedAutoPrivateExe);

        book.ForgetAutoPrivateExe(Exe.ToUpperInvariant());
        Assert.Null(ConfigReader.Parse(book.Serialize("0.7.0-test", DateTime.Now)).TrackedAutoPrivateExe);
        Assert.Null(PlannerTests.Fixture().TrackedAutoPrivateFolder);
    }

    [Fact]
    public void TheNewFieldsAreReadAndWrittenAndTheOldOnesStay()
    {
        var book = ConfigBook.ForSetup(PlannerTests.Fixture());
        book.SetAutoPrivateSkip(new[] { "Cafe" });
        book.TrackAutoPrivateExe(Exe, "abc");
        Assert.True(book.TrackComputerName("LAPTOP"));
        Assert.False(book.TrackComputerName("LAPTOP"));
        var doc = ConfigReader.Parse(book.Serialize("0.7.0-test", new DateTime(2026, 10, 24, 12, 0, 0)));
        Assert.Equal(new[] { "Cafe" }, doc.AutoPrivateSkip);
        Assert.Equal(new TrackedFile(Exe, "abc"), doc.TrackedAutoPrivateExe);
        Assert.Equal(new[] { "LAPTOP" }, doc.TrackedComputerNames);
        Assert.True(doc.HasTrackedComputerNames);
        Assert.Equal("HOST-01", doc.BackupHostName);
        Assert.Equal(PlannerTests.Fixture().TrackedUsers, doc.TrackedUsers);

        book.TrackAutoPrivateExe(null, null);
        Assert.Null(ConfigReader.Parse(book.Serialize("0.7.0-test", DateTime.Now)).TrackedAutoPrivateExe);

        var old = PlannerTests.Fixture();
        Assert.Empty(old.AutoPrivateSkip);
        Assert.Null(old.TrackedAutoPrivateExe);
        Assert.False(old.HasTrackedComputerNames);
    }
}
