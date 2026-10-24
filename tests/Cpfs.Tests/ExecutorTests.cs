using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cpfs.Core.Apply;
using Cpfs.Core.Config;
using Cpfs.Core.Planning;
using Cpfs.Core.Text;
using Xunit;

namespace Cpfs.Tests;

public class ExecutorTests
{
    private static readonly DateTime Now = new(2000, 1, 8, 12, 0, 0);

    private static PlanItem Item(string key, PlanAction action, PlanSection section = PlanSection.Hardening, bool backup = false, bool ticked = false) =>
        new() { Key = key, Action = action, Section = section, BackupNeeded = backup, Ticked = ticked };

    private static BulkBackup Bulk() => new(
        "HOST-01",
        Planner.Hardening.Select(h => new RegistryBackup(@"HKLM:\" + h.Key, h.Name, true, h.Target == 0 ? 1 : 0)).ToList(),
        true,
        false,
        "Manual",
        new LockoutInfo(0, 1800, 1800),
        new AuditSetting(true, false));

    private static ConfigDocument Fixture() => ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.3-anonymised.json"));

    private sealed class Run
    {
        public List<byte[]> Saves { get; } = new();

        public List<string> Calls { get; } = new();

        public ExecutionResult Execute(IReadOnlyList<ApplyEntry> entries, ConfigBook? book, Func<ApplyEntry, IChangeStep> steps) =>
            Executor.Run(entries, steps, book, b => Saves.Add(b.Serialize("0.5.0-test", Now)), null);
    }

    [Fact]
    public void OnlyCreateChangeRemoveAndTickedAskItemsRunAndNothingIsDeferred()
    {
        var plan = new Plan(new[]
        {
            Item("hardening.a", PlanAction.Keep),
            Item("hardening.b", PlanAction.Change),
            Item("share.foreign:X", PlanAction.Ask, PlanSection.Share),
            Item("net:Home", PlanAction.Ask, PlanSection.Networks, ticked: true),
            Item("fw.vpnadapter:Tailscale", PlanAction.Ask, PlanSection.Firewall, ticked: true),
            Item("hardening.c", PlanAction.CannotPlan),
            Item("drive.map", PlanAction.Create, PlanSection.Drive),
            Item("shortcut.file", PlanAction.Create, PlanSection.Shortcut),
            Item("auto.task", PlanAction.RemoveIfOurs, PlanSection.AutoPrivate),
            Item("fw.extra:CyShare", PlanAction.RemoveIfOurs, PlanSection.Firewall),
            Item("computer.name", PlanAction.Change, PlanSection.Computer),
            Item("backup", PlanAction.Create, PlanSection.Backup),
            Item("data.folder", PlanAction.Change, PlanSection.DataFolder),
        });

        var entries = ApplyList.Setup(plan);
        Assert.Equal(new[] { "data.folder", "backup", "hardening.b", "net:Home", "drive.map", "shortcut.file", "auto.task", "fw.extra:CyShare", "computer.name" },
            entries.Select(e => e.Item.Key));
        Assert.DoesNotContain(entries, e => e.Deferred);

        var run = new Run();
        var result = run.Execute(entries, null, e => new LambdaStep { Do = () => run.Calls.Add(e.Item.Key) });
        Assert.True(result.Completed);
        Assert.Contains("drive.map", run.Calls);
        Assert.Contains("auto.task", run.Calls);
        Assert.Contains("computer.name", run.Calls);
        Assert.Equal(0, result.Count(StepOutcome.Deferred));
        Assert.Single(ApplyList.Blocking(plan));
    }

    [Fact]
    public void AStepAlreadyAtTargetIsKeptWithoutWriting()
    {
        var run = new Run();
        var entries = new[] { new ApplyEntry(Item("hardening.a", PlanAction.Change), false) };
        var result = run.Execute(entries, ConfigBook.ForSetup(Fixture()), _ => new LambdaStep { Read = () => true, Do = () => run.Calls.Add("apply") });

        Assert.Empty(run.Calls);
        Assert.Equal(StepOutcome.Kept, result.Reports.Single().Outcome);
    }

    [Fact]
    public void TheFirstFailureStopsTheRunAndWhatWasDoneStaysSaved()
    {
        var run = new Run();
        var book = ConfigBook.ForSetup(Fixture());
        var entries = new[]
        {
            new ApplyEntry(Item("net:Home", PlanAction.Ask, PlanSection.Networks, ticked: true), false),
            new ApplyEntry(Item("hardening.b", PlanAction.Change), false),
            new ApplyEntry(Item("hardening.c", PlanAction.Change), false),
        };
        var result = run.Execute(entries, book, e => e.Item.Key switch
        {
            "net:Home" => new LambdaStep { RecordWith = b => b!.TrackNetwork("Home", "{1}") ? BackupWrite.Recorded : BackupWrite.NotNeeded },
            "hardening.b" => new LambdaStep { Do = () => throw new InvalidOperationException("access denied") },
            _ => new LambdaStep { Do = () => run.Calls.Add("c") },
        });

        Assert.False(result.Completed);
        Assert.Equal("hardening.b", result.FailedKey);
        Assert.Contains("access denied", result.Error, StringComparison.Ordinal);
        Assert.Empty(run.Calls);
        Assert.Equal(StepOutcome.NotRun, result.Reports.Single(r => r.Key == "hardening.c").Outcome);

        var saved = ConfigReader.Parse(run.Saves[^1]);
        Assert.Contains("Home", saved.TrackedNetworks);
        Assert.Equal("{1}", saved.TrackedNetworkProfiles.Single().Id);
    }

    [Fact]
    public void AFailureShowsWindowsWordsAndTheLogKeepsTheTypeAndTheCall()
    {
        var run = new Run();
        var entries = new[] { new ApplyEntry(Item("user.password", PlanAction.Change, PlanSection.User), false) };
        var windows = new System.ComponentModel.Win32Exception(2221).Message;
        var result = run.Execute(entries, null, _ => new LambdaStep
        {
            Do = () => throw new System.ComponentModel.Win32Exception(2221, "NetUserSetInfo 1003: " + windows),
        });

        Assert.Equal(windows, result.Error);
        Assert.DoesNotContain("Win32Exception", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("NetUserSetInfo", result.Error, StringComparison.Ordinal);
        Assert.Contains("Win32Exception: NetUserSetInfo 1003", result.Detail, StringComparison.Ordinal);
        Assert.Contains("2221", result.Detail, StringComparison.Ordinal);
        Assert.Equal(result.Detail, result.Reports.Single().LogText);
    }

    [Fact]
    public void AChangeThatDoesNotTakeEffectIsAFailure()
    {
        var run = new Run();
        var entries = new[] { new ApplyEntry(Item("hardening.a", PlanAction.Change), false) };
        var result = run.Execute(entries, null, _ => new LambdaStep { Check = () => false });

        Assert.Equal("hardening.a", result.FailedKey);
    }

    [Fact]
    public void AnUnsupportedCallIsReportedAndTheRunGoesOn()
    {
        var run = new Run();
        var entries = new[]
        {
            new ApplyEntry(Item("server.reject", PlanAction.Change, PlanSection.Server), false),
            new ApplyEntry(Item("hardening.a", PlanAction.Change), false),
        };
        var result = run.Execute(entries, null, e => e.Item.Key == "server.reject"
            ? new LambdaStep { Do = () => throw new StepUnsupportedException("Err_Test") }
            : new LambdaStep { Do = () => run.Calls.Add("a") });

        Assert.True(result.Completed);
        Assert.Equal(StepOutcome.Unsupported, result.Reports[0].Outcome);
        Assert.Equal("Err_Test", result.Reports[0].Text?.Key);
        Assert.Equal(new[] { "a" }, run.Calls);
        Assert.Equal(0, result.Count(StepOutcome.Deferred));
        Assert.Equal(1, result.NotDone);
    }

    [Fact]
    public void OwnSentenceTravelsAsItsKeySoThePageCanTranslateIt()
    {
        var run = new Run();
        var entries = new[] { new ApplyEntry(Item("share.main", PlanAction.Change, PlanSection.Share), false) };
        var result = run.Execute(entries, null, _ => new LambdaStep { Do = () => throw new CpfsException("Err_ShareNotOurs", "Shared") });

        Assert.False(result.Completed);
        Assert.Equal("Err_ShareNotOurs", result.ErrorText?.Key);
        Assert.Equal("Err_ShareNotOurs", result.Reports[0].Text?.Key);
        Assert.Equal(new object?[] { "Shared" }, result.ErrorText!.Args);

        // Without the app's English strings (as here) the message names the key; the log keeps the exception's type.
        Assert.Equal("Err_ShareNotOurs(Shared)", result.Error);
        Assert.StartsWith("CpfsException: ", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void APartlyDoneStepIsReportedAsPartialAndOneThatDidNothingStopsTheRun()
    {
        var run = new Run();
        var entries = new[]
        {
            new ApplyEntry(Item("u.owner", PlanAction.Ask, PlanSection.Folder, ticked: true), false),
            new ApplyEntry(Item("hardening.a", PlanAction.Change), false),
        };
        var partial = run.Execute(entries, null, e => e.Item.Key == "u.owner"
            ? new LambdaStep { Do = () => throw new StepPartialException("Err_OwnerPartial", 2, 1) }
            : new LambdaStep { Do = () => run.Calls.Add("a") });

        Assert.True(partial.Completed);
        Assert.Equal(StepOutcome.Partial, partial.Reports[0].Outcome);
        Assert.Equal("Err_OwnerPartial", partial.Reports[0].Text?.Key);
        Assert.Equal(new object?[] { 2, 1 }, partial.Reports[0].Text!.Args);
        Assert.Equal(StepOutcome.Done, partial.Reports[1].Outcome);
        Assert.Equal(1, partial.NotDone);

        var none = run.Execute(entries, null, e => e.Item.Key == "u.owner"
            ? new LambdaStep { Do = () => throw new InvalidOperationException("0 changed, 3 failed") }
            : new LambdaStep { Do = () => run.Calls.Add("b") });

        Assert.False(none.Completed);
        Assert.Equal(StepOutcome.Failed, none.Reports[0].Outcome);
        Assert.Equal(StepOutcome.NotRun, none.Reports[1].Outcome);
        Assert.DoesNotContain("b", run.Calls);
    }

    [Fact]
    public void TheBulkBackupIsTakenOnlyOnTheFirstSetupAndNothingIsSavedBeforeIt()
    {
        var fresh = ConfigBook.ForSetup(null);
        Assert.True(fresh.FirstSetup);
        Assert.False(fresh.CanSave);
        Assert.True(fresh.TakeBulkBackup(Bulk(), Now));
        Assert.True(fresh.CanSave);
        Assert.False(fresh.TakeBulkBackup(Bulk() with { Smb2 = false }, Now));

        var doc = ConfigReader.Parse(fresh.Serialize("0.5.0-test", Now));
        Assert.Equal(BackupState.Present, doc.Backup);
        Assert.True(doc.BackupSmb2);
        Assert.Equal(0, doc.BackupLockout);
        Assert.Equal(1800, doc.BackupLockoutDuration);
        Assert.True(doc.Partial);

        // A later Setup on an install that has a config.json never takes the backup again.
        var existing = ConfigBook.ForSetup(Fixture());
        Assert.False(existing.FirstSetup);
        Assert.False(existing.TakeBulkBackup(Bulk(), Now));
        Assert.Equal(Fixture().BackupTaken, ConfigReader.Parse(existing.Serialize("0.5.0-test", Now)).BackupTaken);
    }

    [Fact]
    public void ARecordedBackupValueIsNeverOverwrittenButANullOneIsFilled()
    {
        var book = ConfigBook.ForSetup(Fixture());
        Assert.Equal(BackupWrite.AlreadyRecorded, book.Backup("Smb2", false));

        // The 0.2.3 fixture's Lockout is null: the scripts could not read it.
        Assert.Null(Fixture().BackupLockout);
        Assert.Equal(BackupWrite.Recorded, book.Backup("Lockout", 0));
        Assert.Equal(BackupWrite.AlreadyRecorded, book.Backup("Lockout", 5));

        var doc = ConfigReader.Parse(book.Serialize("0.5.0-test", Now));
        Assert.True(doc.BackupSmb2);
        Assert.Equal(0, doc.BackupLockout);
    }

    [Fact]
    public void ABackupNeededWhereThereIsNoBackupBlockStopsBeforeTheChange()
    {
        var v01 = ConfigReader.Parse(TestFiles.ConfigBytes("v0.1.json"));
        var book = ConfigBook.ForSetup(v01);
        var run = new Run();
        var entries = new[] { new ApplyEntry(Item("hardening.lockout", PlanAction.Change, backup: true), false) };
        var result = run.Execute(entries, book, _ => new LambdaStep { RecordWith = b => b!.Backup("Lockout", 0), Do = () => run.Calls.Add("apply") });

        Assert.False(result.Completed);
        Assert.Empty(run.Calls);
        Assert.False(book.HasBackupBlock);
    }

    [Fact]
    public void AFirstSetupWritesAFileThe020UninstallCanRead()
    {
        var book = ConfigBook.ForSetup(null);
        book.TakeBulkBackup(Bulk(), Now);
        book.TrackUser("smbtest", false, false, null);
        book.TrackUser("SMBTEST", true, true, null);
        book.TrackFirewallRule("FPS-SMB-In-TCP");
        book.TrackNetwork("Home \u0634\u0628\u06a9\u0647", "{9}");
        book.TrackCreated("AcePath", @"E:\Box");
        book.SetField("SmbUser", "smbtest");
        book.SetField("SharePath", @"E:\Box");
        book.SetField("ShareName", "Box");
        book.MarkFinished();
        var bytes = book.Serialize("0.5.0-test", Now);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        var doc = ConfigReader.Parse(bytes);
        Assert.Equal(TextEncoding.Utf8Bom, doc.Encoding);
        Assert.Equal(ConfigShape.V02, doc.Shape);
        Assert.False(doc.Partial);
        Assert.Single(doc.TrackedUsers);
        Assert.False(doc.TrackedUsers[0].Existed);
        Assert.Equal(new[] { "FPS-SMB-In-TCP" }, doc.TrackedFirewallRules);
        Assert.Equal(8, doc.BackupRegistry.Count);
        Assert.All(doc.BackupRegistry, r => Assert.Equal(RegistryRoot.LocalMachine, r.Location!.Root));

        // The fields and JSON types the 0.2.0 Uninstall reads.
        using var json = JsonDocument.Parse(bytes.AsMemory(3));
        var root = json.RootElement;
        foreach (var name in new[] { "HostName", "SmbUser", "SharePath", "ShareName", "Product", "Version", "Updated" })
        {
            Assert.Equal(JsonValueKind.String, root.GetProperty(name).ValueKind);
        }

        Assert.Equal(JsonValueKind.Number, root.GetProperty("Access").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("DriveLetter").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("Shortcut").ValueKind);
        var backup = root.GetProperty("Backup");
        Assert.Equal(JsonValueKind.String, backup.GetProperty("Taken").ValueKind);
        Assert.Equal(JsonValueKind.String, backup.GetProperty("ServerStart").ValueKind);
        Assert.Equal(JsonValueKind.True, backup.GetProperty("Smb2").ValueKind);
        Assert.Equal(JsonValueKind.False, backup.GetProperty("RejectUnencrypted").ValueKind);
        Assert.Equal(JsonValueKind.Number, backup.GetProperty("Lockout").ValueKind);
        foreach (var entry in backup.GetProperty("Registry").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, entry.GetProperty("Path").ValueKind);
            Assert.StartsWith(@"HKLM:\", entry.GetProperty("Path").GetString(), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.True, entry.GetProperty("Exists").ValueKind);
            Assert.Equal(JsonValueKind.Number, entry.GetProperty("Value").ValueKind);
        }

        foreach (var list in new[] { "Users", "Networks", "FirewallRules" })
        {
            Assert.Equal(JsonValueKind.Array, root.GetProperty("Tracked").GetProperty(list).ValueKind);
        }

        // Persian network names stay readable in the file.
        Assert.Contains("\u0634\u0628\u06a9\u0647", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void ALaterSetupKeepsEveryFieldOfTheScriptsConfigWithItsType()
    {
        var original = Fixture();
        var book = ConfigBook.ForSetup(original);
        var doc = ConfigReader.Parse(book.Serialize("0.5.0-test", Now));

        foreach (var (path, value) in ConfigJson.Flatten(original.Root))
        {
            if (path is "Version" or "Updated")
            {
                continue;
            }

            Assert.Contains(ConfigJson.Flatten(doc.Root), p => p.Key == path && p.Value == value);
        }

        Assert.Equal(original.DriveLabel, doc.DriveLabel);
        Assert.Contains(ConfigJson.Flatten(doc.Root), p => p.Key == "DriveLabel");
        Assert.Equal("0.5.0-test", doc.Version);
    }

    [Fact]
    public void ATakenOverAccountRecordsWhatItLookedLikeBefore()
    {
        var book = ConfigBook.ForSetup(Fixture());
        Assert.True(book.TrackUser("other", true, true, new UserBefore(false, false, new[] { UninstallPlanner.AdminsSid }, null)));
        var before = ConfigReader.Parse(book.Serialize("0.5.0-test", Now)).TrackedUserBefore("OTHER");

        Assert.NotNull(before);
        Assert.False(before!.Enabled);
        Assert.Equal(new[] { UninstallPlanner.AdminsSid }, before.Groups);
        Assert.Null(before.HiddenValue);
    }

    [Fact]
    public void SavingAfterEachStepGoesThroughTheBook()
    {
        var run = new Run();
        var book = ConfigBook.ForSetup(null);
        var entries = new[]
        {
            new ApplyEntry(Item("data.folder", PlanAction.Create, PlanSection.DataFolder), false),
            new ApplyEntry(Item("backup", PlanAction.Create, PlanSection.Backup), false),
            new ApplyEntry(Item("hardening.b", PlanAction.Change, backup: true), false),
        };
        var result = run.Execute(entries, book, e => e.Item.Key switch
        {
            "backup" => new LambdaStep { TrackWith = b => b!.TakeBulkBackup(Bulk(), Now) },
            "hardening.b" => new LambdaStep { RecordWith = b => b!.Backup("Smb2", false) },
            _ => new LambdaStep(),
        });

        Assert.True(result.Completed);

        // Nothing before the backup; one save after it, one after the hardening step, one at the end.
        Assert.Equal(3, run.Saves.Count);
        Assert.True(ConfigReader.Parse(run.Saves[0]).Partial);
        Assert.False(ConfigReader.Parse(run.Saves[^1]).Partial);
        Assert.True(ConfigReader.Parse(run.Saves[^1]).BackupSmb2);
    }

    [Fact]
    public void NoSaveEverHoldsTheWordsOfAPassword()
    {
        // The password never reaches the book: no API takes one. This guards the shape of what is saved.
        var book = ConfigBook.ForSetup(null);
        book.TakeBulkBackup(Bulk(), Now);
        var text = System.Text.Encoding.UTF8.GetString(book.Serialize("0.5.0-test", Now));
        Assert.DoesNotContain("assword", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(typeof(ConfigBook).GetMethods(), m => m.GetParameters().Any(p => p.ParameterType == typeof(System.Security.SecureString)));
    }
}
