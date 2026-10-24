using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Cpfs.Tests;

// The app-wide ui:Button style in App.xaml must extend WPF-UI's own style, or every button loses the library's
// template and draws as a classic grey Button (0.6.0 dev3). WPF can't load here, so this checks the XAML and the
// library it names.
public class ButtonStyleTests
{
    private const string BaseKey = "DefaultUiButtonStyle";
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AppButtonStyleExtendsWpfUiKeyedStyleAfterItsDictionary()
    {
        var app = XDocument.Load(Path.Combine(TestFiles.RepoRoot, "src", "Cpfs", "App.xaml"));
        var root = app.Root!.Element(Wpf + "Application.Resources")!.Element(Wpf + "ResourceDictionary")!;

        var merged = root.Element(Wpf + "ResourceDictionary.MergedDictionaries")!.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.True(merged.IndexOf("ThemesDictionary") is >= 0 and var t && merged.IndexOf("ControlsDictionary") > t,
            "WPF-UI's ThemesDictionary must come before its ControlsDictionary: " + string.Join(", ", merged));

        // A direct entry of the application dictionary outranks every merged one, WPF-UI's implicit style included.
        var buttonStyles = root.Elements(Wpf + "Style").Where(s => (string?)s.Attribute("TargetType") == "{x:Type ui:Button}").ToList();
        var style = Assert.Single(buttonStyles);
        Assert.Null(style.Attribute(X + "Key"));
        Assert.Equal("{StaticResource " + BaseKey + "}", (string?)style.Attribute("BasedOn"));
    }

    [Fact]
    public void NoXamlBasesAButtonStyleOnItsOwnTypeKey()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(TestFiles.RepoRoot, "src", "Cpfs"), "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotContain("BasedOn=\"{StaticResource {x:Type ui:Button}}\"", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    // The key is WPF-UI's, not part of its documented API; a WPF-UI update must still define it.
    [Fact]
    public void ReferencedWpfUiDefinesTheBaseKey()
    {
        var assets = Path.Combine(TestFiles.RepoRoot, "src", "Cpfs", "obj", "project.assets.json");
        Assert.True(File.Exists(assets), "Restore Cpfs.sln first: " + assets + " is missing.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(assets));
        var packages = json.RootElement.GetProperty("packageFolders").EnumerateObject().First().Name;
        var library = json.RootElement.GetProperty("libraries").EnumerateObject().Single(l => l.Name.StartsWith("WPF-UI/", StringComparison.Ordinal));
        var target = json.RootElement.GetProperty("targets").EnumerateObject().First().Value.GetProperty(library.Name);
        var dll = target.GetProperty("runtime").EnumerateObject().Single(r => r.Name.EndsWith("Wpf.Ui.dll", StringComparison.Ordinal)).Name;
        var path = Path.Combine(packages, library.Value.GetProperty("path").GetString()!, dll);

        // Compiled XAML (BAML) keeps resource keys as UTF-8 strings.
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(BaseKey)) >= 0, library.Name + " has no " + BaseKey + " in " + path);
    }
}
