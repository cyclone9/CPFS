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
