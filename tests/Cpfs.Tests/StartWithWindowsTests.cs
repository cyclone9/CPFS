using System.Collections.Generic;
using Cpfs.Core.Apply;
using Cpfs.Core.Config;
using Cpfs.Core.Planning;
using Cpfs.Core.Preferences;
using Xunit;

namespace Cpfs.Tests;

public class StartWithWindowsTests
{
    private const string Exe = @"C:\Tools\CPFS\Cpfs.exe";

    [Fact]
    public void TheCommandIsQuotedAndParsesBack()
    {
        var line = StartWithWindows.Command(Exe, out var refusal);
        Assert.Equal(CommandRefusal.None, refusal);
        Assert.Equal("\"" + Exe + "\" --tray", line);
        Assert.Equal(Exe, StartWithWindows.TargetOf(line));

        var unc = StartWithWindows.Command(@"\\server\share\Cpfs.exe", out _);
        Assert.Equal(@"\\server\share\Cpfs.exe", StartWithWindows.TargetOf(unc));
    }

    [Theory]
    [InlineData(null, CommandRefusal.NotFullPath)]
    [InlineData(@"Cpfs.exe", CommandRefusal.NotFullPath)]
    [InlineData(@"\\?\C:\Cpfs.exe", CommandRefusal.NotFullPath)]
    [InlineData(@"C:\A""B\Cpfs.exe", CommandRefusal.Quote)]
    [InlineData(@"%LOCALAPPDATA%\Cpfs.exe", CommandRefusal.NotFullPath)]
    [InlineData(@"C:\100%\Cpfs.exe", CommandRefusal.Percent)]
    public void ABadPathIsRefused(string? exe, CommandRefusal expected)
    {
        Assert.Null(StartWithWindows.Command(exe, out var refusal));
        Assert.Equal(expected, refusal);
    }

    // 260 characters is the limit for the whole line, quotes and verb included.
    [Fact]
    public void ALineOver260CharactersIsRefused()
    {
        var fits = @"C:\" + new string('a', 260 - 3 - 2 - 7 - 4) + ".exe";
        Assert.Equal(260, StartWithWindows.Command(fits, out _)!.Length);
        Assert.Null(StartWithWindows.Command("C:\\a" + fits[3..], out var refusal));
        Assert.Equal(CommandRefusal.TooLong, refusal);
    }

    [Theory]
    [InlineData(@"C:\Tools\CPFS\Cpfs.exe --tray")]
    [InlineData(@"""C:\Tools\CPFS\Cpfs.exe"" --tray ")]
    [InlineData(@"""C:\Tools\CPFS\Cpfs.exe"" --TRAY")]
    [InlineData(@"""C:\Tools\CPFS\Cpfs.exe""")]
    [InlineData(@"""%ProgramFiles%\CPFS\Cpfs.exe"" --tray")]
    [InlineData(@"""C:\Tools\CPFS\Cpfs.exe"" --tray"" --tray")]
    [InlineData(@"""Cpfs.exe"" --tray")]
    [InlineData(@"notepad.exe")]
    [InlineData("")]
    public void AnythingElseIsNotCpfsFormat(string data) => Assert.Null(StartWithWindows.TargetOf(data));

    [Fact]
    public void TheFiveStates()
    {
        var mine = new RunValue("\"" + Exe + "\" --tray");
        var other = new RunValue(@"""D:\Old\Cpfs.exe"" --tray");
        Assert.Equal(StartState.Off, StartWithWindows.Decide(null, Exe, _ => true).State);
        Assert.Equal(StartState.On, StartWithWindows.Decide(mine, @"c:\tools\cpfs\CPFS.EXE", _ => false).State);

        var copy = StartWithWindows.Decide(other, Exe, p => p == @"D:\Old\Cpfs.exe");
        Assert.Equal(StartState.OtherCopy, copy.State);
        Assert.Equal(@"D:\Old\Cpfs.exe", copy.Path);
        Assert.Equal(StartState.TargetMissing, StartWithWindows.Decide(other, Exe, _ => false).State);

        var foreign = StartWithWindows.Decide(new RunValue("notepad.exe"), Exe, _ => true);
        Assert.Equal(StartState.Foreign, foreign.State);
        Assert.Equal("notepad.exe", foreign.Data);
        Assert.Equal(StartState.Foreign, StartWithWindows.Decide(new RunValue(null), Exe, _ => true).State);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(new byte[0], false)]
    [InlineData(new byte[] { 2, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 3, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 6 }, false)]
    [InlineData(new byte[] { 7, 1 }, true)]
    public void StartupApprovedHintsOffOnlyOnAnOddFirstByte(byte[]? bytes, bool off) => Assert.Equal(off, StartWithWindows.DisabledInSettings(bytes));

    [Fact]
    public void BytesAreLoggedAsHex()
    {
        Assert.Equal("absent", StartWithWindows.Hex(null));
        Assert.Equal("empty", StartWithWindows.Hex(System.Array.Empty<byte>()));
        Assert.Equal("03 00 AB", StartWithWindows.Hex(new byte[] { 3, 0, 0xAB }));
    }

    // Uninstall removes a value in CPFS's format, with or without config.json, leaves any other value named CPFS and
    // says so, and never blocks on a value it could not read.
    [Fact]
    public void UninstallRemovesOnlyCpfsFormat()
    {
        var config = ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.3-anonymised.json"));
        var ours = Reading.Ok<RunValue?>(new RunValue("\"" + Exe + "\" --tray"));
        foreach (var c in new[] { config, null })
        {
            var plan = PlanFor(UninstallPlannerTests.Installed() with { StartWithWindows = ours }, c);
            var item = plan.Find("u.run")!;
            Assert.Equal(PlanAction.RemoveIfOurs, item.Action);
            Assert.Equal(PlanSection.StartWithWindows, item.Section);
            Assert.Contains(ApplyList.Uninstall(plan), e => e.Item.Key == "u.run");
            Assert.False(UninstallPlanner.NothingInstalled(plan));
        }

        var foreignPlan = PlanFor(UninstallPlannerTests.Installed() with { StartWithWindows = Reading.Ok<RunValue?>(new RunValue("notepad.exe")) }, config);
        var foreign = foreignPlan.Find("u.run")!;
        Assert.Equal(PlanAction.Keep, foreign.Action);
        Assert.Equal(UninstallPlanner.R.RunForeign, foreign.ReasonKey);
        Assert.DoesNotContain(ApplyList.Uninstall(foreignPlan), e => e.Item.Key == "u.run");
        Assert.Contains(PlanSummary.Uninstall(foreignPlan).Lines, l => l.Key == "Summary_U_RunForeignLeft");

        Assert.Null(PlanFor(UninstallPlannerTests.Installed() with { StartWithWindows = Reading.Ok<RunValue?>(null) }, config).Find("u.run"));

        var unread = PlanFor(UninstallPlannerTests.Installed() with { StartWithWindows = Reading.Failed<RunValue?>("hive not loaded") }, config);
        Assert.Equal(PlanAction.Keep, unread.Find("u.run")!.Action);
        Assert.Empty(ApplyList.Blocking(unread));
    }

    private static Plan PlanFor(SystemSnapshot s, ConfigDocument? c) =>
        UninstallPlanner.Uninstall(new UninstallInput { Snapshot = s, Config = c, Choices = new Dictionary<string, bool>() });
}

public class TrayRulesTests
{
    private static Reading<DataFolderInfo> Folder(bool exists, OwnerKind? config, OwnerKind owner = OwnerKind.Administrators) =>
        Reading.Ok(new DataFolderInfo(exists, owner == OwnerKind.Administrators, true, false) { Owner = owner, ConfigOwner = config });

    // After Uninstall (no ProgramData\CPFS) or with an untrusted config.json, the window doesn't bring the icon back.
    [Fact]
    public void TheWindowStartsTheTrayOnlyWhileCpfsIsSetUp()
    {
        var setUp = Folder(true, OwnerKind.Administrators);
        Assert.True(TrayRules.AtWindowStart(false, true, setUp));
        Assert.False(TrayRules.AtWindowStart(true, true, setUp));
        Assert.False(TrayRules.AtWindowStart(false, false, setUp));
        Assert.False(TrayRules.AtWindowStart(false, true, Folder(false, null)));
        Assert.False(TrayRules.AtWindowStart(false, true, Folder(true, null)));
        Assert.False(TrayRules.AtWindowStart(false, true, Folder(true, OwnerKind.Other)));
        Assert.False(TrayRules.AtWindowStart(false, true, Reading.Failed<DataFolderInfo>("denied")));
    }

    [Fact]
    public void OnlyAnUnelevatedWindowWithARunningTrayHidesOnMinimize()
    {
        Assert.True(TrayRules.HideOnMinimize(false, true, true));
        Assert.False(TrayRules.HideOnMinimize(true, true, true));
        Assert.False(TrayRules.HideOnMinimize(false, false, true));
        Assert.False(TrayRules.HideOnMinimize(false, true, false));
    }

    // An elevated window may turn the tray off but not on; Start with Windows is never changed there.
    [Fact]
    public void WhenTheSwitchesAreEnabled()
    {
        Assert.True(TrayRules.TraySwitchEnabled(false, false));
        Assert.True(TrayRules.TraySwitchEnabled(true, true));
        Assert.False(TrayRules.TraySwitchEnabled(true, false));

        Assert.True(TrayRules.StartSwitchEnabled(false, true, true, false));
        Assert.False(TrayRules.StartSwitchEnabled(true, true, true, false));
        Assert.False(TrayRules.StartSwitchEnabled(false, false, true, false));
        Assert.False(TrayRules.StartSwitchEnabled(false, true, false, false));
        Assert.False(TrayRules.StartSwitchEnabled(false, true, true, true));
    }
}
