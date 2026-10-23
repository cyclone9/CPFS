using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Cpfs.Tests;

// Checks the app's resource files and XAML from the repository: every language has every key with the same
// placeholders, every key the XAML binds exists, and no UI text is written into XAML.
public class StringsTests
{
    private static string StringsDir => Path.Combine(TestFiles.RepoRoot, "src", "Cpfs", "Strings");

    private static Dictionary<string, string> Load(string file) =>
        XDocument.Load(Path.Combine(StringsDir, file)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    private static SortedSet<string> Placeholders(string value) =>
        new(Regex.Matches(value, @"\{(\d+)(:[^}]*)?\}").Select(m => m.Groups[1].Value));

    [Theory]
    [InlineData("Strings.fa.resx")]
    [InlineData("Strings.ar.resx")]
    [InlineData("Strings.tr.resx")]
    public void EveryLanguageHasEveryKeyWithTheSamePlaceholders(string file)
    {
        var english = Load("Strings.resx");
        var other = Load(file);

        Assert.Empty(english.Keys.Except(other.Keys));
        Assert.Empty(other.Keys.Except(english.Keys));
        foreach (var (key, value) in english)
        {
            Assert.True(Placeholders(value).SetEquals(Placeholders(other[key])), file + ": placeholders differ in " + key);
        }
    }

    [Fact]
    public void EnglishIsMarkedTranslatedAndTheOthersNot()
    {
        Assert.Equal("true", Load("Strings.resx")["Meta_Translated"]);
        foreach (var file in new[] { "Strings.fa.resx", "Strings.ar.resx", "Strings.tr.resx" })
        {
            Assert.Equal("false", Load(file)["Meta_Translated"]);
        }
    }

    [Fact]
    public void LanguageNamesHaveNoBrackets()
    {
        foreach (var file in new[] { "Strings.resx", "Strings.fa.resx", "Strings.ar.resx", "Strings.tr.resx" })
        {
            foreach (var (key, value) in Load(file).Where(p => p.Key.StartsWith("Lang_", System.StringComparison.Ordinal) || p.Key.StartsWith("LangNative_", System.StringComparison.Ordinal)))
            {
                if (key.StartsWith("Lang_NotTranslated", System.StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.DoesNotContain("(", value, System.StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void EveryKeyTheXamlBindsExists()
    {
        var english = Load("Strings.resx");
        var xaml = Directory.EnumerateFiles(Path.Combine(TestFiles.RepoRoot, "src", "Cpfs"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, System.StringComparison.Ordinal));
        foreach (var file in xaml)
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{Binding \[([A-Za-z0-9_]+)\]"))
            {
                Assert.True(english.ContainsKey(m.Groups[1].Value), Path.GetFileName(file) + " binds a missing key " + m.Groups[1].Value);
            }
        }
    }

    [Fact]
    public void EveryKeyTheCodeNamesExists()
    {
        var english = Load("Strings.resx");
        var keyLike = new Regex("\"([A-Z][A-Za-z]+_[A-Za-z0-9_]*[A-Za-z0-9])\"");
        var code = Directory.EnumerateFiles(Path.Combine(TestFiles.RepoRoot, "src", "Cpfs"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, System.StringComparison.Ordinal));
        foreach (var file in code)
        {
            foreach (Match m in keyLike.Matches(File.ReadAllText(file)))
            {
                Assert.True(english.ContainsKey(m.Groups[1].Value), Path.GetFileName(file) + " names a missing key " + m.Groups[1].Value);
            }
        }

        // Keys the code builds from a stem and a value.
        var built = new List<string>();
        foreach (var id in new[] { "Config", "Computer", "User", "Share", "Firewall", "Tasks", "Hardening", "Networks", "Drive", "Shortcut", "DataFolder" })
        {
            built.Add("Setup_" + id + "_Title");
            built.Add("Setup_" + id + "_Desc");
        }

        built.AddRange(new[] { "Utf8", "Utf8Bom", "Utf16LeBom", "Utf16BeBom", "Fallback" }.Select(e => "Encoding_" + e));
        built.AddRange(new[] { "Match", "NotPassed", "Unconfirmed", "Mismatch" }.Select(e => "Identity_" + e));
        built.AddRange(new[] { "Absent", "Null", "Present" }.Select(e => "Setup_Config_Backup" + e));
        built.AddRange(new[] { "SystemDrive", "ThisProcess", "UserSessionReceived", "UserNetwork", "DosDevices", "MountedDevices" }.Select(e => "Source_" + e));
        built.AddRange(new[] { "0", "1", "2", "3", "4", "Unknown" }.Select(e => "StartType_" + e));
        built.AddRange(new[] { "0", "1", "2", "3", "4" }.Select(e => "TaskState_" + e));
        built.AddRange(new[] { "SegoeUiVariable", "SegoeUi", "Tahoma", "Vazirmatn" }.Select(e => "Font_" + e));
        built.AddRange(new[] { "en", "fa", "ar", "tr" }.SelectMany(l => new[] { "Lang_" + l, "LangNative_" + l }));
        built.AddRange(new[] { "Sync", "Uninstall" }.Select(p => "Placeholder_" + p + "_Desc"));
        built.AddRange(Enumerable.Range(0, 5).Select(u => "Size_" + u));
        built.AddRange(Cpfs.Core.Monitor.FailedLogon.KnownReasonKeys);
        Assert.DoesNotContain(built, k => !english.ContainsKey(k));
    }

    [Fact]
    public void XamlHoldsNoLiteralUiText()
    {
        var literal = new Regex("\\b(Text|Content|Header|Title|Message|PlaceholderText|ToolTip|AutomationProperties\\.Name)=\"(?!\\{)[^\"]+\"");
        var xaml = Directory.EnumerateFiles(Path.Combine(TestFiles.RepoRoot, "src", "Cpfs"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, System.StringComparison.Ordinal));
        foreach (var file in xaml)
        {
            var match = literal.Match(File.ReadAllText(file));
            Assert.False(match.Success, Path.GetFileName(file) + ": " + match.Value);
        }
    }
}
