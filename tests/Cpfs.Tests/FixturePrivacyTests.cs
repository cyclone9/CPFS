using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Cpfs.Core.Config;
using Xunit;

namespace Cpfs.Tests;

// The repository is public. A config taken from a real machine may only enter it anonymised, so this test compares
// every fixture with a private config kept outside the repository. CPFS_PRIVATE_CONFIG names that file; without it
// (as in CI) the check has nothing to compare with and passes.
public class FixturePrivacyTests
{
    private const string Variable = "CPFS_PRIVATE_CONFIG";
    private const string ProductName = "CPFS - CyClone Power File Share";

    // Values that every install has and that identify no one.
    private static readonly HashSet<string> SharedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Product", "Version", "ServerStart", "Path",
    };

    private static string? PrivateConfigPath()
    {
        var path = Environment.GetEnvironmentVariable(Variable);
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
    }

    [Fact]
    public void NoFixtureContainsAValueFromThePrivateConfig()
    {
        var path = PrivateConfigPath();
        if (path is null)
        {
            return;
        }

        var secrets = IdentifyingValues(File.ReadAllBytes(path));
        Assert.NotEmpty(secrets);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(TestFiles.RepoRoot, "tests"), "*", SearchOption.AllDirectories)
                     .Where(f => !IsBuildOutput(f)))
        {
            // The product name contains the scripts' default user name; it is the same on every install.
            var text = File.ReadAllText(file).Replace(ProductName, "", StringComparison.OrdinalIgnoreCase);
            var escaped = text.Replace(@"\\", @"\", StringComparison.Ordinal);
            foreach (var secret in secrets)
            {
                Assert.False(
                    text.Contains(secret, StringComparison.OrdinalIgnoreCase) || escaped.Contains(secret, StringComparison.OrdinalIgnoreCase),
                    Path.GetFileName(file) + " contains a value from the private config (" + secret.Length + " characters)");
            }
        }
    }

    [Fact]
    public void PrivateConfigIsReadAsA02xInstall()
    {
        var path = PrivateConfigPath();
        if (path is null)
        {
            return;
        }

        var doc = ConfigReader.Parse(File.ReadAllBytes(path));
        Assert.Equal(ConfigShape.V02, doc.Shape);
        Assert.NotNull(doc.SmbUser);
        Assert.NotNull(doc.SharePath);
    }

    // Every string in the private config except the ones every install shares, plus the parts of paths and names
    // that could identify a machine on their own (a share name inside a shortcut path, a host name inside a label).
    private static List<string> IdentifyingValues(byte[] bytes)
    {
        var doc = ConfigReader.Parse(bytes);
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(doc.Root.ToJsonString());
        Collect(json.RootElement, "", "", values);

        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in values)
        {
            foreach (var part in v.Split('\\', '/', ' ', '(', ')', '.'))
            {
                if (part.Length >= 6 && !IsCommonWord(part))
                {
                    parts.Add(part);
                }
            }
        }

        values.UnionWith(parts);
        return values.Where(v => v.Length >= 4).ToList();
    }

    private static void Collect(JsonElement element, string key, string parentKey, HashSet<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                {
                    Collect(p.Value, p.Name, key, values);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, key, key, values);
                }

                break;
            case JsonValueKind.String:
                var registryName = string.Equals(key, "Name", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(parentKey, "Registry", StringComparison.OrdinalIgnoreCase);
                if (!SharedKeys.Contains(key) && !registryName)
                {
                    values.Add(element.GetString()!);
                }

                break;
        }
    }

    // Words of the fixed paths every install has (C:\Users\Public\Desktop\..., "CPFS").
    private static bool IsCommonWord(string part) => part is "Public" or "Desktop" or "Users" or "Shared" or "Server";

    private static bool IsBuildOutput(string file) =>
        file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
