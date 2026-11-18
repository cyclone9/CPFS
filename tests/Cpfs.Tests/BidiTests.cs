using System;
using Cpfs.Core.Text;
using Xunit;

namespace Cpfs.Tests;

public class BidiTests
{
    [Theory]
    [InlineData(@"D:\Shared", true, "\u202AD:\\Shared\u202C")]
    [InlineData("Türkçe", true, "\u202ATürkçe\u202C")]
    [InlineData("192.168.1.10", true, "\u202A192.168.1.10\u202C")]
    [InlineData("فارسی", true, "فارسی")]
    [InlineData(@"D:\Shared", false, @"D:\Shared")]
    [InlineData("شبکه خانه", false, "\u202Bشبکه خانه\u202C")]
    [InlineData("", true, "")]
    public void EmbedKeepsTheValueInItsOwnDirection(string value, bool uiIsRtl, string expected)
    {
        Assert.Equal(expected, Bidi.Embed(value, uiIsRtl));
    }

    [Fact]
    public void StripRemovesTheMarksForCopying()
    {
        Assert.Equal(@"D:\Shared", Bidi.Strip(Bidi.Embed(@"D:\Shared", true)));
        Assert.Equal("a", Bidi.Strip("\u200Ea\u200F"));
    }

    // Every digit, dot, hyphen and space of "CPFS 1.0.0-dev7" sits between left-to-right marks, so neither the
    // version nor its place after "CPFS" can be reordered in a right-to-left title bar.
    [Theory]
    [InlineData("1.0.0-dev7")]
    [InlineData("CPFS 0.9.0")]
    [InlineData("7")]
    public void KeepLeftToRightFencesEveryNonLetter(string value)
    {
        var kept = Bidi.KeepLeftToRight(value, true);

        Assert.Equal(value, Bidi.Strip(kept));
        Assert.Equal(Bidi.Lrm, kept[0]);
        Assert.Equal(Bidi.Lrm, kept[kept.Length - 1]);
        Assert.DoesNotContain(new string(Bidi.Lrm, 2), kept, StringComparison.Ordinal);
        for (var i = 0; i < kept.Length; i++)
        {
            if (kept[i] != Bidi.Lrm && !char.IsLetter(kept[i]))
            {
                Assert.True(kept[i - 1] == Bidi.Lrm && kept[i + 1] == Bidi.Lrm, "Not fenced at " + i + ": " + value);
            }
        }

        Assert.Equal(value, Bidi.KeepLeftToRight(value, false));
        Assert.Equal("", Bidi.KeepLeftToRight(null, true));
    }

    [Theory]
    [InlineData("smb://HOST-01.local/Shared", StrongDirection.LeftToRight)]
    [InlineData("12 شبکه", StrongDirection.RightToLeft)]
    [InlineData("العربية", StrongDirection.RightToLeft)]
    [InlineData("123 - 456", StrongDirection.None)]
    public void FirstStrong(string value, StrongDirection expected)
    {
        Assert.Equal(expected, Bidi.FirstStrong(value));
    }
}
