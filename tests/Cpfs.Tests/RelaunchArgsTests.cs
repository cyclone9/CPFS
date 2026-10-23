using System.Collections.Generic;
using System.Text;
using Cpfs.Core.Elevation;
using Xunit;

namespace Cpfs.Tests;

public class RelaunchArgsTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    // CommandLineToArgvW's rules for the arguments after the program name (2n backslashes + quote -> n and a quote
    // toggle; 2n+1 backslashes + quote -> n and a literal quote; other backslashes literal).
    private static List<string> SplitLikeWindows(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasArg = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var n = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    n++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', n / 2);
                    if (n % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else
                {
                    current.Append('\\', n);
                    i--;
                }

                hasArg = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasArg = true;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (hasArg)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasArg = false;
                }
            }
            else
            {
                current.Append(c);
                hasArg = true;
            }
        }

        if (hasArg)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    [Fact]
    public void BuildQuoteAndParseRoundTrip()
    {
        var request = new RelaunchRequest
        {
            Page = Pages.Setup,
            DesktopSid = Sid,
            DesktopPath = @"C:\Users\Some One\Desktop\",
            Letters = "zxDd",
            Theme = "Dark",
            Language = "fa",
            Font = "Vazirmatn",
        };

        var line = RelaunchArgs.ToCommandLine(RelaunchArgs.Build(request));
        var parsed = RelaunchArgs.Parse(SplitLikeWindows(line));

        Assert.True(parsed.Elevated);
        Assert.Empty(parsed.Rejected);
        Assert.Equal(Pages.Setup, parsed.Page);
        Assert.Equal(Sid, parsed.DesktopSid);
        Assert.Equal(@"C:\Users\Some One\Desktop\", parsed.DesktopPath);
        Assert.Equal("DXZ", parsed.Letters);
        Assert.True(parsed.LettersPassed);
        Assert.Equal("fa", parsed.Language);
    }

    [Fact]
    public void EmptyLettersSurviveQuoting()
    {
        var line = RelaunchArgs.ToCommandLine(RelaunchArgs.Build(new RelaunchRequest()));
        var parsed = RelaunchArgs.Parse(SplitLikeWindows(line));

        Assert.True(parsed.LettersPassed);
        Assert.Equal("", parsed.Letters);
    }

    [Theory]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData(@"C:\dir\", "C:\\dir\\")]
    [InlineData(@"C:\my dir\", "\"C:\\my dir\\\\\"")]
    [InlineData("", "\"\"")]
    public void Quoting(string arg, string expected)
    {
        Assert.Equal(expected, RelaunchArgs.ToCommandLine(new[] { arg }));
        Assert.Equal(new List<string> { arg }, SplitLikeWindows(expected));
    }

    [Theory]
    [InlineData("--desktop-sid", "S-1-5-21-x")]
    [InlineData("--desktop-sid", "S-1-5-21-1 --page Setup")]
    [InlineData("--desktop", @"relative\path")]
    [InlineData("--desktop", "\\\\server\\share\\Desktop")]
    public void MalformedIdentityRejectsTheHandOver(string option, string value)
    {
        var parsed = RelaunchArgs.Parse(new[] { "--elevated", option, value });

        Assert.True(parsed.IdentityRejected);
        Assert.Equal(IdentityCheck.Mismatch, Identity.Compare(parsed, Sid, @"C:\Users\A\Desktop"));
    }

    [Theory]
    [InlineData("--letters", "x1")]
    [InlineData("--letters", "ABC;")]
    [InlineData("--page", "Format")]
    [InlineData("--lang", "fa-IR")]
    [InlineData("--theme", "Dark Mode")]
    public void OtherMalformedValuesAreDropped(string option, string value)
    {
        var parsed = RelaunchArgs.Parse(new[] { "--elevated", option, value });

        Assert.Contains(option, parsed.Rejected);
        Assert.False(parsed.IdentityRejected);
        Assert.Equal("", parsed.Letters);
        Assert.Null(parsed.Page);
        Assert.Null(parsed.Language);
    }

    [Fact]
    public void UnknownOptionsAndMissingValuesAreRejected()
    {
        var parsed = RelaunchArgs.Parse(new[] { "--password", "--letters" });

        Assert.Contains("--password", parsed.Rejected);
        Assert.Contains("--letters", parsed.Rejected);
        Assert.False(parsed.LettersPassed);
    }

    [Fact]
    public void IdentityIsComparedWithWhatTheElevatedInstanceDerived()
    {
        var parsed = RelaunchArgs.Parse(new[] { "--elevated", "--desktop-sid", Sid, "--desktop", @"C:\Users\A\Desktop" });

        Assert.Equal(IdentityCheck.Match, Identity.Compare(parsed, Sid.ToLowerInvariant(), @"c:\users\a\desktop\"));
        Assert.Equal(IdentityCheck.Mismatch, Identity.Compare(parsed, "S-1-5-21-1-2-3-500", @"C:\Users\A\Desktop"));
        Assert.Equal(IdentityCheck.Mismatch, Identity.Compare(parsed, Sid, @"C:\Users\B\Desktop"));
        Assert.Equal(IdentityCheck.Unconfirmed, Identity.Compare(parsed, null, null));
        Assert.Equal(IdentityCheck.NotPassed, Identity.Compare(RelaunchArgs.Parse(new[] { "--elevated" }), Sid, null));
    }
}
