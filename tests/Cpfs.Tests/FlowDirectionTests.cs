using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Cpfs.Tests;

// Pages, cards and panels follow the window's direction (MainWindow passes it to every page). Only a value that must
// stay left to right in Persian and Arabic (a path, a name, an address, a QR code, the brand symbol) sets its own
// FlowDirection, and only to LeftToRight. A container that sets it turns a whole page or card the wrong way round,
// as the 1.0.0 dev7 Setup page looked, so this keeps the XAML to that rule. WPF can't load here; it reads the files.
public class FlowDirectionTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // Elements that show one value.
    private static readonly HashSet<string> ValueElements = new(StringComparer.Ordinal)
    {
        "TextBlock", "TextBox", "ComboBox", "Image", "Run",
    };

    // Containers that hold the pieces of one address, each laid out left to right so it breaks only after a separator.
    private static readonly HashSet<string> AddressContainers = new(StringComparer.Ordinal)
    {
        "ConnectPage.xaml WrapPanel x:Name=AddressPanel",
        "ConnectPage.xaml ItemsControl ItemsSource={Binding Pieces}",
    };

    [Fact]
    public void OnlyValuesSetTheirOwnFlowDirection()
    {
        var found = 0;
        var wrong = new List<string>();
        foreach (var file in XamlFiles())
        {
            var name = Path.GetFileName(file);
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var element in doc.Descendants())
            {
                var where = name + ":" + ((System.Xml.IXmlLineInfo)element).LineNumber + " " + element.Name.LocalName;
                if (element.Attribute("FlowDirection") is { } attribute)
                {
                    found++;
                    if (!AllowedOnElement(name, element, attribute.Value))
                    {
                        wrong.Add(where + " FlowDirection=\"" + attribute.Value + "\"");
                    }
                }

                if (element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == "FlowDirection")
                {
                    found++;
                    var style = element.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Style");
                    var target = TypeName((string?)style?.Attribute("TargetType"));
                    if ((string?)element.Attribute("Value") != "LeftToRight" || target is not ("TextBlock" or "TextBox"))
                    {
                        wrong.Add(where + " in a Style for " + (target ?? "?") + ", Value=\"" + (string?)element.Attribute("Value") + "\"");
                    }
                }
            }
        }

        Assert.True(found > 0, "No FlowDirection found at all: the scan is reading the wrong files.");
        Assert.True(wrong.Count == 0, "FlowDirection set outside a value:" + Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void NoPageOrWindowSetsItsDirection()
    {
        foreach (var file in XamlFiles())
        {
            var root = XDocument.Load(file).Root!;
            Assert.True(root.Attribute("FlowDirection") is null, Path.GetFileName(file) + " sets FlowDirection on its root " + root.Name.LocalName);
        }
    }

    private static bool AllowedOnElement(string file, XElement element, string value)
    {
        var tag = element.Name.LocalName;

        // Each language's own name in the Preferences list keeps that language's direction.
        if (file == "PreferencesPage.xaml" && tag == "TextBlock" && value == "{Binding NativeFlow}")
        {
            return true;
        }

        if (value != "LeftToRight")
        {
            return false;
        }

        if (ValueElements.Contains(tag))
        {
            return true;
        }

        var key = file + " " + tag + " "
            + (element.Attribute(X + "Name") is { } n ? "x:Name=" + n.Value : "ItemsSource=" + (string?)element.Attribute("ItemsSource"));
        return AddressContainers.Contains(key);
    }

    private static string? TypeName(string? targetType)
    {
        if (targetType is null)
        {
            return null;
        }

        var name = targetType.StartsWith("{x:Type ", StringComparison.Ordinal) ? targetType.Substring(8).TrimEnd('}') : targetType;
        return name.Contains(':', StringComparison.Ordinal) ? name.Substring(name.IndexOf(':', StringComparison.Ordinal) + 1) : name;
    }

    private static IEnumerable<string> XamlFiles()
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(TestFiles.RepoRoot, "src", "Cpfs"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(sep + "obj" + sep, StringComparison.Ordinal) && !f.Contains(sep + "bin" + sep, StringComparison.Ordinal));
    }
}
