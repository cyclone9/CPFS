using System;
using Cpfs.Core.Apply;
using Cpfs.Core.Config;
using Cpfs.Core.Drives;
using Cpfs.Core.Planning;
using Xunit;

namespace Cpfs.Tests;

public class SetupDefaultsTests
{
    private static LetterScan Letters()
    {
        var letters = new LetterScan();
        letters.Add('C', LetterSource.SystemDrive);
        return letters;
    }

    [Fact]
    public void AFirstInstallHasTheExplorerEntryOnAndNoDriveOrShortcut()
    {
        var a = SetupDefaults.From(null, Letters());
        Assert.True(a.ExplorerEntry);
        Assert.False(a.Drive);
        Assert.False(a.Shortcut);

        // Switched on, they still have the defaults.
        Assert.Equal('X', a.DriveLetter);
        Assert.Equal("CPFS - " + SetupDefaults.ShareName, a.DriveName);
        Assert.Equal(SetupDefaults.ShareName + " (CPFS)", a.ShortcutName);
    }

    [Theory]
    [InlineData("v0.3.0-written.json", true, true)]
    [InlineData("v0.2.3-anonymised.json", false, true)]
    [InlineData("v0.2.1-upgraded-from-0.1.json", false, false)]
    [InlineData("v0.1.json", true, true)]
    public void AnExistingInstallKeepsWhatItRecorded(string fixture, bool drive, bool shortcut)
    {
        var config = ConfigReader.Parse(TestFiles.ConfigBytes(fixture));
        var a = SetupDefaults.From(config, Letters());
        Assert.Equal(drive, a.Drive);
        Assert.Equal(shortcut, a.Shortcut);
        Assert.True(a.ExplorerEntry);

        // A recorded "no File Explorer entry" stays off.
        var book = ConfigBook.ForSetup(config);
        book.SetField("ExplorerEntry", false);
        Assert.False(SetupDefaults.From(ConfigReader.Parse(book.Serialize("1.0.0-test", new DateTime(2026, 11, 19))), Letters()).ExplorerEntry);
    }
}
