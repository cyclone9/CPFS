using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Cpfs.Core.Apply;
using Cpfs.Core.Config;
using Cpfs.Core.Planning;
using Xunit;

namespace Cpfs.Tests;

// The release security review: a planted or linked ProgramData\CPFS (B1) and the restore floor for anonymous,
// null-session and LM hash settings (S1).
public class ReleaseReviewTests
{
    private static Reading<DataFolderInfo> Folder(OwnerKind owner, OwnerKind? config, bool link = false) =>
        Reading.Ok(new DataFolderInfo(true, owner == OwnerKind.Administrators, false, true) { Owner = owner, ConfigOwner = config, IsLink = link });

    [Theory]
    [InlineData(OwnerKind.Administrators, OwnerKind.Administrators, false)]
    [InlineData(OwnerKind.System, OwnerKind.TrustedInstaller, false)]
    [InlineData(OwnerKind.AdminMember, OwnerKind.AdminMember, false)]
    [InlineData(OwnerKind.Other, OwnerKind.Administrators, true)]
    [InlineData(OwnerKind.Administrators, OwnerKind.Other, true)]
    [InlineData(OwnerKind.AdminMember, OwnerKind.Unknown, true)]
    [InlineData(OwnerKind.Unknown, OwnerKind.System, true)]
    public void ConfigJsonIsTrustedOnlyWhenAnAdministratorOwnsItAndItsFolder(OwnerKind folder, OwnerKind config, bool ignored)
    {
        Assert.Equal(ignored, ConfigTrust.Ignored(Folder(folder, config)));
    }

    [Fact]
    public void NoConfigJsonALinkedFolderOrAnUnreadFolderAreDecidedSafely()
    {
        Assert.False(ConfigTrust.Ignored(Folder(OwnerKind.Other, null)));
        Assert.True(ConfigTrust.Ignored(Folder(OwnerKind.Administrators, OwnerKind.Administrators, link: true)));
        Assert.False(ConfigTrust.Ignored(Reading.Failed<DataFolderInfo>("x")));

        // A locked install read before the owner kind existed still counts as Administrators'.
        Assert.False(ConfigTrust.Ignored(Reading.Ok(new DataFolderInfo(true, true, true, false) { ConfigOwner = OwnerKind.Administrators })));
    }

    [Fact]
    public void SetupRefusesADataFolderThatIsALink()
    {
        var s = PlannerTests.Installed() with { DataFolder = Folder(OwnerKind.Unknown, null, link: true) };
        var plan = Planner.Setup(s, SetupDefaults.From(PlannerTests.Fixture(), s.Letters), PlannerTests.Fixture(), PlannerTests.Protected);

        var item = plan.Find("data.folder")!;
        Assert.Equal(PlanAction.CannotPlan, item.Action);
        Assert.Equal(Planner.R.DataLink, item.ReasonKey);
        Assert.Contains(item, ApplyList.Blocking(plan));
    }

    [Fact]
    public void SetupSetsAnUntrustedConfigAsideFirstAndTakesAFreshBackup()
    {
        var s = PlannerTests.Installed() with { DataFolder = Folder(OwnerKind.Other, OwnerKind.Other) };
        var config = PlannerTests.Fixture();
        var plan = Planner.Setup(s, SetupDefaults.From(config, s.Letters), config, PlannerTests.Protected);

        var ignored = plan.Find(Planner.IgnoredConfigKey)!;
        Assert.Equal(PlanAction.Ask, ignored.Action);
        Assert.True(ignored.Ticked);
        Assert.Equal(Planner.R.ConfigIgnored, ignored.ReasonKey);
        Assert.Equal(Planner.R.BackupFresh, plan.Find("backup")!.ReasonKey);

        var order = ApplyList.Setup(plan).Select(e => e.Item.Key).ToList();
        Assert.Equal(new[] { Planner.IgnoredConfigKey, "data.folder", "backup" }, order.Take(3));

        // Unticked, nothing runs: the backup can't be planned over a file CPFS didn't write.
        var answers = SetupDefaults.From(config, s.Letters);
        answers = answers with { Choices = new Dictionary<string, bool>(answers.Choices) { [Planner.IgnoredConfigKey] = false } };
        var refused = Planner.Setup(s, answers, config, PlannerTests.Protected);
        Assert.Equal(PlanAction.CannotPlan, refused.Find("backup")!.Action);
        Assert.Equal(Planner.R.ConfigIgnoredNeeded, refused.Find("backup")!.ReasonKey);
    }

    [Fact]
    public void SetupKeepsTheBackupOfAConfigAnAdministratorPutBackByHand()
    {
        var s = PlannerTests.Installed() with { DataFolder = Folder(OwnerKind.AdminMember, OwnerKind.AdminMember) };
        var config = PlannerTests.Fixture();
        var plan = Planner.Setup(s, SetupDefaults.From(config, s.Letters), config, PlannerTests.Protected);

        Assert.Null(plan.Find(Planner.IgnoredConfigKey));
        Assert.Equal(Planner.R.BackupTaken, plan.Find("backup")!.ReasonKey);
    }

    [Fact]
    public void UninstallWithAnUntrustedConfigRestoresAndDeletesNothingFromIt()
    {
        var s = UninstallPlannerTests.Installed() with { DataFolder = Folder(OwnerKind.Other, OwnerKind.Other) };
        var plan = UninstallPlanner.Uninstall(new UninstallInput { Snapshot = s, Config = PlannerTests.Fixture() });

        Assert.Equal(UninstallPlanner.R.ConfigIgnored, plan.Find("u.config")!.ReasonKey);
        Assert.False(UninstallPlanner.NothingInstalled(plan));
        Assert.DoesNotContain(plan.Items, i => i.Key.StartsWith("u.reg:", StringComparison.Ordinal) || i.Key.StartsWith("u.user", StringComparison.Ordinal)
            || i.Key == UninstallPlanner.RestoreKey || i.Key == "u.savecopy" || i.Key == "u.data" || i.Key.StartsWith("u.share", StringComparison.Ordinal));
        Assert.All(ApplyList.Uninstall(plan), e => Assert.StartsWith("u.fw", e.Item.Key, StringComparison.Ordinal));
        Assert.Contains(PlanSummary.Uninstall(plan).Lines, l => l.Key == "Summary_U_ConfigIgnored");
    }

    [Fact]
    public void SetAsideSettingsFilesAreCopiedToDocumentsBeforeTheDataFolderGoes()
    {
        var names = new[] { "config-ignored-20261024-153000.json", "config-ignored-20261025-090000.json" };
        var s = UninstallPlannerTests.Installed() with
        {
            DataFolder = Reading.Ok(new DataFolderInfo(true, true, true, false) { ConfigOwner = OwnerKind.Administrators, IgnoredConfigs = names }),
        };
        var plan = UninstallPlanner.Uninstall(new UninstallInput { Snapshot = s, Config = PlannerTests.Fixture() });

        var copy = plan.Find(UninstallPlanner.IgnoredCopyKey)!;
        Assert.Equal(PlanAction.Create, copy.Action);
        Assert.Equal(UninstallPlanner.R.SaveIgnored, copy.ReasonKey);
        Assert.Equal(string.Join(", ", names), copy.ReasonArgs.Single());
        var order = ApplyList.Uninstall(plan).Select(e => e.Item.Key).ToList();
        Assert.True(order.IndexOf("u.savecopy") < order.IndexOf(UninstallPlanner.IgnoredCopyKey));
        Assert.True(order.IndexOf(UninstallPlanner.IgnoredCopyKey) < order.IndexOf("u.data"));

        Assert.Null(UninstallPlanner.Uninstall(new UninstallInput { Snapshot = UninstallPlannerTests.Installed(), Config = PlannerTests.Fixture() })
            .Find(UninstallPlanner.IgnoredCopyKey));
        Assert.True(ConfigTrust.IsIgnoredFileName(ConfigTrust.IgnoredFileName(new DateTime(2026, 10, 24, 15, 30, 0))));
        Assert.False(ConfigTrust.IsIgnoredFileName("config.json"));
    }

    // A backup recording a value weaker than the Windows default, with the setting now at CPFS's value.
    private static Plan UninstallWithOriginal(string name, long? value)
    {
        var doc = PlannerTests.Fixture();
        var registry = (JsonArray)ConfigJson.Get((JsonObject)ConfigJson.Get(doc.Root, "Backup")!, "Registry")!;
        var entry = registry.OfType<JsonObject>().Single(e => (string?)e["Name"] == name);
        entry["Exists"] = value is not null;
        entry["Value"] = value is { } v ? JsonValue.Create(v) : null;
        return UninstallPlanner.Uninstall(new UninstallInput
        {
            Snapshot = UninstallPlannerTests.Installed(),
            Config = ConfigReader.Parse(ConfigWriter.Serialize(doc.Root)),
        });
    }

    [Theory]
    [InlineData("RestrictAnonymousSAM", 0L)]
    [InlineData("RestrictNullSessAccess", 0L)]
    [InlineData("NoLMHash", 0L)]
    [InlineData("EveryoneIncludesAnonymous", 1L)]
    public void AWeakerThanDefaultOriginalIsKeptAtTheWindowsDefault(string name, long weak)
    {
        var item = UninstallWithOriginal(name, weak).Find("u.reg:" + name)!;
        Assert.Equal(PlanAction.Keep, item.Action);
        Assert.Equal(UninstallPlanner.R.RestoreFloor, item.ReasonKey);
        Assert.Equal(Planner.HardeningFor(name)!.WindowsDefault, Planner.HardeningFor(name)!.RestoreValue(weak));
    }

    [Theory]
    [InlineData("RestrictAnonymousSAM")]
    [InlineData("RestrictNullSessAccess")]
    [InlineData("NoLMHash")]
    [InlineData("EveryoneIncludesAnonymous")]
    public void AnAbsentOriginalOfAFloorKeyIsRestoredAsAbsent(string name)
    {
        var item = UninstallWithOriginal(name, null).Find("u.reg:" + name)!;
        Assert.Equal(PlanAction.Change, item.Action);
        Assert.Equal(UninstallPlanner.R.RestoreValue, item.ReasonKey);
        Assert.Null(Planner.HardeningFor(name)!.RestoreValue(null));
    }

    [Theory]
    [InlineData("RestrictAnonymous", 0L)]
    [InlineData("forceguest", 1L)]
    [InlineData("LmCompatibilityLevel", 0L)]
    public void TheOtherKeysAreRestoredExactly(string name, long original)
    {
        var item = UninstallWithOriginal(name, original).Find("u.reg:" + name)!;
        Assert.Equal(PlanAction.Change, item.Action);
        Assert.Equal(UninstallPlanner.R.RestoreValue, item.ReasonKey);
        Assert.Equal(original, Planner.HardeningFor(name)!.RestoreValue(original));
    }
}
