using System;
using System.Linq;
using Cpfs.Core.Monitor;
using Xunit;

namespace Cpfs.Tests;

public class FolderActivityTests
{
    private const string Root = @"D:\Server\Shared-Server";
    private static readonly DateTimeOffset Noon = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static FolderActivity New(int max = 200)
    {
        var activity = new FolderActivity(TimeSpan.FromSeconds(2), max, TimeSpan.FromHours(24));
        activity.Watch(Root);
        return activity;
    }

    private static FolderChange C(ActivityKind kind, string relative, double seconds, string? oldRelative = null) =>
        new(kind, Root + @"\" + relative, oldRelative is null ? null : Root + @"\" + oldRelative, TimeSpan.FromSeconds(seconds), Noon.AddSeconds(seconds));

    private static string[] Shown(FolderActivity a) => a.Rows().Select(r => r.Kind + " " + (r.OldPath.Length > 0 ? r.OldPath + " > " : "") + r.Path).ToArray();

    [Theory]
    [InlineData(Root, Root + @"\Photos\a.jpg", @"Photos\a.jpg")]
    [InlineData(Root + @"\", Root + @"\a.jpg", "a.jpg")]
    [InlineData(@"d:\server\shared-server", Root + @"\a.jpg", "a.jpg")]
    [InlineData(Root, Root, "")]
    [InlineData(Root, Root + @"\", "")]
    [InlineData(Root, Root + @"-Other\a.jpg", Root + @"-Other\a.jpg")]
    [InlineData(@"D:\", @"D:\a.jpg", "a.jpg")]
    public void PathsAreRelativeToTheSharedFolder(string root, string full, string expected) =>
        Assert.Equal(expected, FolderActivity.RelativePath(root, full));

    // One small copy from a phone: Created, a burst of Changed, and the folder's own Changed, in either order.
    [Fact]
    public void ACopyIsOneAddedRow()
    {
        var a = New();
        Assert.True(a.Add(C(ActivityKind.Changed, "Photos", 0.0)));
        Assert.True(a.Add(C(ActivityKind.Added, @"Photos\a.jpg", 0.1)));
        a.Add(C(ActivityKind.Changed, @"Photos\a.jpg", 0.2));
        a.Add(C(ActivityKind.Changed, @"Photos\a.jpg", 0.3));
        a.Add(C(ActivityKind.Changed, "Photos", 0.4));
        Assert.Equal(new[] { @"Added Photos\a.jpg" }, Shown(a));

        // A long copy keeps writing more often than the window: still one row.
        for (var s = 1.0; s < 10; s += 1.5)
        {
            a.Add(C(ActivityKind.Changed, @"Photos\a.jpg", s));
        }

        Assert.Single(a.Rows());

        // A later edit, after a pause, is a row of its own; its burst is one row.
        a.Add(C(ActivityKind.Changed, @"Photos\a.jpg", 20));
        a.Add(C(ActivityKind.Changed, @"Photos\a.jpg", 21));
        Assert.Equal(new[] { @"Changed Photos\a.jpg", @"Added Photos\a.jpg" }, Shown(a));
    }

    [Fact]
    public void RenamesAndDeletes()
    {
        var a = New();
        a.Add(C(ActivityKind.Renamed, "b.jpg", 0, "a.jpg"));
        a.Add(C(ActivityKind.Deleted, "c.jpg", 5));

        // Moved in from outside the folder (no old name) and out of it (no new name).
        a.Add(new FolderChange(ActivityKind.Renamed, Root + @"\in.jpg", Root + @"\", TimeSpan.FromSeconds(10), Noon));
        a.Add(new FolderChange(ActivityKind.Renamed, Root + @"\", Root + @"\out.jpg", TimeSpan.FromSeconds(15), Noon));
        Assert.Equal(new[] { "Deleted out.jpg", "Added in.jpg", "Deleted c.jpg", "Renamed a.jpg > b.jpg" }, Shown(a));

        // Changes to the shared folder itself are not rows.
        Assert.False(a.Add(C(ActivityKind.Changed, "", 20)));
    }

    [Fact]
    public void OverflowsInARowAreOneRow()
    {
        var a = New();
        var missed = new FolderChange(ActivityKind.Missed, "", null, TimeSpan.FromSeconds(1), Noon);
        Assert.True(a.Add(missed));
        Assert.False(a.Add(missed with { At = TimeSpan.FromSeconds(2) }));
        a.Add(C(ActivityKind.Added, "a.jpg", 3));
        Assert.True(a.Add(missed with { At = TimeSpan.FromSeconds(4) }));
        Assert.Equal(new[] { ActivityKind.Missed, ActivityKind.Added, ActivityKind.Missed }, a.Rows().Select(r => r.Kind).ToArray());
    }

    [Fact]
    public void KeepsAtMostMaxRowsAndOnly24Hours()
    {
        var a = New(max: 3);
        for (var i = 0; i < 5; i++)
        {
            a.Add(C(ActivityKind.Added, "f" + i + ".jpg", i * 10));
        }

        Assert.Equal(new[] { "Added f4.jpg", "Added f3.jpg", "Added f2.jpg" }, Shown(a));

        Assert.False(a.Expire(TimeSpan.FromHours(24)));
        Assert.True(a.Expire(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(25)));
        Assert.Equal(new[] { "Added f4.jpg", "Added f3.jpg" }, Shown(a));
    }

    [Fact]
    public void AnotherSharedFolderStartsAnEmptyList()
    {
        var a = New();
        a.Add(C(ActivityKind.Added, "a.jpg", 0));
        a.Watch(Root.ToUpperInvariant() + @"\");
        Assert.Single(a.Rows());
        a.Watch(@"E:\Other");
        Assert.Empty(a.Rows());
    }
}
