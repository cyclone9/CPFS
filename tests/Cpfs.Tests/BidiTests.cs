using System;
using Cpfs.Core.Text;
using Xunit;

namespace Cpfs.Tests;

public class BidiTests
{
    [Theory]
    [InlineData("فارسی", true, "فارسی")]
    [InlineData(@"D:\Shared", false, @"D:\Shared")]
    [InlineData("Türkçe", false, "Türkçe")]
    [InlineData("شبکه خانه", false, "\u202Bشبکه خانه\u202C")]
    [InlineData("", true, "")]
    [InlineData("Türkçe", true, "\u200ETürkçe\u200E")]
    [InlineData(@"D:\Shared", true, "\u200ED\u200E:\u200E\\\u200EShared\u200E")]
    public void EmbedKeepsTheValueInItsOwnDirection(string value, bool uiIsRtl, string expected)
    {
        Assert.Equal(expected, Bidi.Embed(value, uiIsRtl));
    }

    // Values that FormatValues puts into Persian and Arabic sentences. Each comes out fenced like the title (no
    // embedding marks to depend on), and strips back to the plain value; English and Turkish get it unchanged.
    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData(@"\\HOST-01.local\Shared-Server")]
    [InlineData("2026/11/19 01:24:48")]
    [InlineData("1.0.1-dev1")]
    [InlineData(@"C:\Users\Public\My Shared Files")]
    [InlineData("CPFS: before Setup")]
    [InlineData("fd7a:115c:a1e0::1")]
    [InlineData("Shared-Server (CPFS).lnk")]
    public void EmbedFencesLeftToRightValuesInRightToLeftProse(string value)
    {
        var embedded = Bidi.Embed(value, true);

        Assert.Equal(value, Bidi.Strip(embedded));
        Assert.DoesNotContain(Bidi.Lre.ToString(), embedded, StringComparison.Ordinal);
        Assert.DoesNotContain(Bidi.Pdf.ToString(), embedded, StringComparison.Ordinal);
        Assert.Equal(Bidi.KeepLeftToRight(value, true), embedded);
        AssertFenced(embedded, value);
        Assert.Equal(value, Bidi.Embed(value, false));
    }

    // Marks alone can't hold right-to-left letters inside a left-to-right value, so such a value keeps its embedding.
    [Fact]
    public void EmbedKeepsTheEmbeddingForAValueWithRightToLeftLetters()
    {
        const string path = @"D:\پوشه من\Shared";

        Assert.Equal(Bidi.Lre + path + Bidi.Pdf, Bidi.Embed(path, true));
        Assert.Equal(path, Bidi.Strip(Bidi.Embed(path, true)));
        Assert.Equal(path, Bidi.Embed(path, false));
    }

    [Fact]
    public void EmbedKeepsALineBreakWhole()
    {
        var embedded = Bidi.Embed("Access is denied.\r\nCode 5", true);

        Assert.Contains("\r\n", embedded, StringComparison.Ordinal);
        Assert.Equal("Access is denied.\r\nCode 5", Bidi.Strip(embedded));
    }

    [Fact]
    public void StripRemovesTheMarksForCopying()
    {
        Assert.Equal(@"D:\Shared", Bidi.Strip(Bidi.Embed(@"D:\Shared", true)));
        Assert.Equal("شبکه خانه", Bidi.Strip(Bidi.Embed("شبکه خانه", false)));
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
        AssertFenced(kept, value);
        Assert.Equal(value, Bidi.KeepLeftToRight(value, false));
        Assert.Equal("", Bidi.KeepLeftToRight(null, true));
    }

    private static void AssertFenced(string kept, string value)
    {
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
