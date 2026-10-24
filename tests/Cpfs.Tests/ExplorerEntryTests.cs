using System;
using Cpfs.Core.Apply;
using Cpfs.Core.Config;
using Cpfs.Core.Planning;
using Xunit;

namespace Cpfs.Tests;

public class ExplorerEntryTests
{
    public const string DesktopSid = "S-1-5-21-1-2-3-1001";

    public const string CpfsIcon = @"C:\ProgramData\CPFS\cpfs.ico";

    // An entry as Setup writes it.
    internal static ExplorerEntryInfo Entry(string name, string folder, string icon = CpfsIcon) => new()
    {
        Name = name,
        NameSpaceName = name,
        Icon = icon,
        TargetFolderPath = folder,
        Pinned = ExplorerEntry.Pinned,
        SortOrder = ExplorerEntry.SortOrderIndex,
        InProcServer = ExplorerEntry.InProcServer,
        InstanceClsid = ExplorerEntry.InstanceClsid,
        BagAttributes = ExplorerEntry.BagAttributes,
        FolderValueFlags = ExplorerEntry.FolderValueFlags,
        ShellAttributes = ExplorerEntry.ShellAttributes,
        HiddenOnDesktop = ExplorerEntry.HiddenOnDesktop,
    };

    internal static Reading<ExplorerHive> Hive(ExplorerEntryInfo? entry, string sid = DesktopSid, bool loaded = true) =>
        Reading.Ok(new ExplorerHive(sid, loaded, entry));

    // The config as Setup leaves it after recording the entry.
    internal static ConfigDocument Recorded(ConfigDocument config, string folder, string name, string sid = DesktopSid)
    {
        var book = ConfigBook.ForSetup(config);
        book.TrackExplorerEntry(sid, folder, name);
        return ConfigReader.Parse(book.Serialize("0.6.0-test", new DateTime(2026, 10, 24, 12, 0, 0)));
    }

    [Fact]
    public void AnEntryIsAtTargetOnlyWithEveryDocumentedValue()
    {
        var entry = Entry("CPFS - Shared", @"D:\Shared");
        Assert.True(ExplorerEntry.IsAtTarget(entry, "CPFS - Shared", @"D:\Shared\", CpfsIcon));
        Assert.False(ExplorerEntry.IsAtTarget(entry, "CPFS - Other", @"D:\Shared", CpfsIcon));
        Assert.False(ExplorerEntry.IsAtTarget(entry, "CPFS - Shared", @"D:\Other", CpfsIcon));
        Assert.False(ExplorerEntry.IsAtTarget(entry with { Icon = @"C:\Icons\other.ico" }, "CPFS - Shared", @"D:\Shared", CpfsIcon));
        Assert.False(ExplorerEntry.IsComplete(entry with { ShellAttributes = null }));
        Assert.False(ExplorerEntry.IsComplete(entry with { HiddenOnDesktop = 0 }));
        Assert.Equal(unchecked((int)0xF080004D), ExplorerEntry.ShellAttributes);
        Assert.False(ExplorerEntry.CanHold(@"D:\100%Shared"));
        Assert.True(ExplorerEntry.CanHold(@"D:\Shared"));
    }

    [Fact]
    public void TheRecordKeepsSidClassFolderAndNameAndTheFormDefaultsFollowIt()
    {
        var config = ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.3-anonymised.json"));
        Assert.Null(config.TrackedExplorerEntry);
        Assert.True(SetupDefaults.From(config, new Core.Drives.LetterScan()).ExplorerEntry);

        var recorded = Recorded(config, @"D:\Shared", "My files");
        var t = recorded.TrackedExplorerEntry!;
        Assert.Equal(DesktopSid, t.Sid);
        Assert.Equal(ExplorerEntry.Clsid, t.Clsid);
        Assert.Equal(@"D:\Shared", t.Folder);
        Assert.Equal("My files", t.Name);

        // Without a drive letter there is no DriveLabel: the entry's name is the form's name.
        Assert.Equal("My files", SetupDefaults.From(recorded, new Core.Drives.LetterScan()).DriveName);

        var book = ConfigBook.ForSetup(recorded);
        book.SetField("ExplorerEntry", false);
        Assert.False(SetupDefaults.From(ConfigReader.Parse(book.Serialize("0.6.0-test", new DateTime(2026, 10, 24))), new Core.Drives.LetterScan()).ExplorerEntry);
    }
}
