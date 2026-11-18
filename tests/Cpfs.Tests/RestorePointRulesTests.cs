using System;
using Cpfs.Core.Apply;
using Cpfs.Core.Text;
using Xunit;

namespace Cpfs.Tests;

public class RestorePointRulesTests
{
    // The owner's laptop, 0.9.0 dev4: the call began at 00:40:42 local (UTC+3:30), Windows created point 745 at
    // 21:10:42.335 UTC, and the list read after the call still showed 744 (created 15:41:02 UTC) as the newest.
    private static readonly DateTimeOffset CallStart = new(2026, 11, 19, 0, 40, 42, TimeSpan.FromMinutes(210));

    private static DateTimeOffset Cim(string value)
    {
        Assert.True(CimDateTime.TryParse(value, out var t));
        return t;
    }

    [Fact]
    public void ANewerSequenceIsANewPoint()
    {
        Assert.True(RestorePointRules.ShowsNewPoint(744, 745, Cim("20261118211042.335207-000"), CallStart));
        Assert.True(RestorePointRules.ShowsNewPoint(null, 1, null, CallStart));
    }

    [Fact]
    public void TheListBeforeWindowsCaughtUpIsNotYetANewPoint()
    {
        Assert.False(RestorePointRules.ShowsNewPoint(744, 744, Cim("20261118154102.681107-000"), CallStart));
        Assert.False(RestorePointRules.ShowsNewPoint(null, null, null, CallStart));
    }

    [Fact]
    public void APointCreatedAtOrAfterTheCallCountsEvenWithTheSameSequence()
    {
        Assert.True(RestorePointRules.ShowsNewPoint(745, 745, Cim("20261118211042.335207-000"), CallStart));
        Assert.True(RestorePointRules.ShowsNewPoint(745, 745, CallStart - TimeSpan.FromSeconds(2), CallStart));
        Assert.False(RestorePointRules.ShowsNewPoint(745, 745, CallStart - TimeSpan.FromSeconds(3), CallStart));
    }

    [Fact]
    public void TheDev5SkipStaysASkip()
    {
        // 01:11:03 local, newest point 745 thirty minutes old.
        var start = new DateTimeOffset(2026, 11, 19, 1, 11, 3, TimeSpan.FromMinutes(210));
        Assert.False(RestorePointRules.ShowsNewPoint(745, 745, Cim("20261118211042.335207-000"), start));
    }
}
