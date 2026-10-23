using System.Globalization;
using System.Threading;
using Cpfs.Core.Validation;
using Xunit;

namespace Cpfs.Tests;

public class ValidatorsTests
{
    private static ProtectedFolders Folders()
    {
        var f = new ProtectedFolders();
        f.Trees.Add(@"C:\Windows");
        f.Trees.Add(@"C:\Program Files");
        f.Trees.Add(@"C:\ProgramData\");
        f.Exact.Add(@"C:\Users");
        f.Exact.Add(@"C:\Users\Public");
        f.Exact.Add(@"C:\Users\Owner");
        return f;
    }

    [Theory]
    [InlineData("HOST-01", ValidationError.None)]
    [InlineData("a", ValidationError.None)]
    [InlineData("", ValidationError.Empty)]
    [InlineData("1234", ValidationError.AllDigits)]
    [InlineData("-HOST", ValidationError.InvalidCharacters)]
    [InlineData("HOST-", ValidationError.InvalidCharacters)]
    [InlineData("HOST_01", ValidationError.InvalidCharacters)]
    [InlineData("ABCDEFGHIJKLMNOP", ValidationError.TooLong)]
    public void ComputerName(string input, ValidationError expected)
    {
        Assert.Equal(expected, Validators.ComputerName(input).Error);
    }

    [Fact]
    public void SmbUserMustDifferFromWindowsUsersAndComputer()
    {
        var clash = Validators.SmbUserName("owner", new[] { @"HOST-01\Owner", null }, "HOST-01");
        Assert.Equal(ValidationError.SameAsWindowsUser, clash.Error);
        Assert.Equal("owner-SMB", clash.Suggestion);

        Assert.Equal(ValidationError.SameAsComputerName, Validators.SmbUserName("host-01", new[] { "Owner" }, "HOST-01").Error);
        Assert.Equal(ValidationError.None, Validators.SmbUserName(" ShareUser ", new[] { "Owner" }, "HOST-01").Error);
        Assert.Equal("ShareUser", Validators.SmbUserName(" ShareUser ", new[] { "Owner" }, "HOST-01").Value);
        Assert.Equal(ValidationError.InvalidCharacters, Validators.SmbUserName("Share User", System.Array.Empty<string>(), null).Error);
        Assert.Equal(ValidationError.TooLong, Validators.SmbUserName(new string('a', 21), System.Array.Empty<string>(), null).Error);
    }

    [Theory]
    [InlineData(@"D:\Shared\", ValidationError.None, @"D:\Shared")]
    [InlineData(@"D:\Shared", ValidationError.None, @"D:\Shared")]
    [InlineData(@"C:\Users\Owner\Shared", ValidationError.None, @"C:\Users\Owner\Shared")]
    [InlineData(@"D:\", ValidationError.DriveRoot, null)]
    [InlineData(@"D:", ValidationError.NotFullPath, null)]
    [InlineData(@"Shared", ValidationError.NotFullPath, null)]
    [InlineData(@"\\server\share", ValidationError.NotFullPath, null)]
    [InlineData(@"C:\Windows", ValidationError.ProtectedFolder, null)]
    [InlineData(@"c:\windows\temp", ValidationError.ProtectedFolder, null)]
    [InlineData(@"C:\ProgramData\CPFS", ValidationError.ProtectedFolder, null)]
    [InlineData(@"C:\Program Files (x86)", ValidationError.None, @"C:\Program Files (x86)")]
    [InlineData(@"C:\Users", ValidationError.ProtectedFolder, null)]
    [InlineData(@"C:\Users\Owner\", ValidationError.ProtectedFolder, null)]
    [InlineData(@"D:\a|b", ValidationError.InvalidCharacters, null)]
    [InlineData(@"D:\a:b", ValidationError.InvalidCharacters, null)]
    public void SharePath(string input, ValidationError expected, string? value)
    {
        var result = Validators.SharePath(input, Folders());
        Assert.Equal(expected, result.Error);
        Assert.Equal(value, result.Value);
    }

    [Theory]
    [InlineData("Shared", ValidationError.None)]
    [InlineData("My.Share_1-2", ValidationError.None)]
    [InlineData("My Share", ValidationError.InvalidCharacters)]
    [InlineData("C$", ValidationError.InvalidCharacters)]
    [InlineData("", ValidationError.Empty)]
    public void ShareName(string input, ValidationError expected)
    {
        Assert.Equal(expected, Validators.ShareName(input).Error);
    }

    [Fact]
    public void Password()
    {
        Assert.Equal(ValidationError.Empty, Validators.Password(0, true));
        Assert.Equal(ValidationError.TooShort, Validators.Password(7, true));
        Assert.Equal(ValidationError.PasswordsDiffer, Validators.Password(8, false));
        Assert.Equal(ValidationError.None, Validators.Password(8, true));
    }

    [Theory]
    [InlineData("CPFS - Shared", ValidationError.None)]
    [InlineData("  ", ValidationError.Empty)]
    [InlineData("a\tb", ValidationError.ControlCharacters)]
    [InlineData("0123456789012345678901234567890123", ValidationError.TooLong)]
    public void DriveName(string input, ValidationError expected)
    {
        Assert.Equal(expected, Validators.DriveName(input).Error);
    }

    [Theory]
    [InlineData("Shared (CPFS)", ValidationError.None, "Shared (CPFS)")]
    [InlineData("Shared (CPFS).lnk", ValidationError.None, "Shared (CPFS)")]
    [InlineData("a/b", ValidationError.InvalidCharacters, null)]
    [InlineData("what?", ValidationError.InvalidCharacters, null)]
    [InlineData("name.", ValidationError.TrailingDotOrSpace, null)]
    [InlineData("CON", ValidationError.ReservedName, null)]
    [InlineData("nul.txt", ValidationError.ReservedName, null)]
    [InlineData("com1", ValidationError.ReservedName, null)]
    [InlineData("Console", ValidationError.None, "Console")]
    [InlineData("", ValidationError.Empty, null)]
    public void ShortcutName(string input, ValidationError expected, string? value)
    {
        var result = Validators.ShortcutName(input);
        Assert.Equal(expected, result.Error);
        Assert.Equal(value, result.Value);
    }

    [Theory]
    [InlineData("x", ValidationError.None, "X")]
    [InlineData("X:", ValidationError.None, "X")]
    [InlineData(@"X:\", ValidationError.None, "X")]
    [InlineData("A", ValidationError.LetterReserved, null)]
    [InlineData("Y", ValidationError.LetterNotFree, null)]
    [InlineData("XY", ValidationError.InvalidCharacters, null)]
    [InlineData("1", ValidationError.InvalidCharacters, null)]
    public void DriveLetter(string input, ValidationError expected, string? value)
    {
        var result = Validators.DriveLetter(input, new[] { 'X', 'Z' });
        Assert.Equal(expected, result.Error);
        Assert.Equal(value, result.Value);
    }

    // Under a Turkish culture "i".ToUpper() is "İ"; the rules must not depend on the UI culture.
    [Fact]
    public void RulesHoldUnderTurkishCulture()
    {
        var saved = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            var tr = CultureInfo.GetCultureInfo("tr-TR");
            Thread.CurrentThread.CurrentCulture = tr;
            Thread.CurrentThread.CurrentUICulture = tr;

            Assert.Equal(ValidationError.SameAsWindowsUser, Validators.SmbUserName("ADMIN", new[] { "admin" }, null).Error);
            Assert.Equal(ValidationError.ProtectedFolder, Validators.SharePath(@"c:\windows\system32", Folders()).Error);
            Assert.Equal("I", Validators.DriveLetter("i", new[] { 'I' }).Value);
            Assert.Equal(ValidationError.ReservedName, Validators.ShortcutName("lpt1").Error);
            Assert.Equal(ValidationError.None, Validators.ComputerName("istanbul-pc").Error);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved.CurrentCulture;
            Thread.CurrentThread.CurrentUICulture = saved.CurrentUICulture;
        }
    }
}
