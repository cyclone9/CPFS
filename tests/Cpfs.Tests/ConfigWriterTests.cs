using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cpfs.Core.Config;
using Xunit;

namespace Cpfs.Tests;

public class ConfigWriterTests
{
    private static readonly DateTime StampTime = new(2000, 1, 8, 12, 0, 0);

    // The fields and JSON types the 0.2.0 Uninstall script reads (legacy inventory, section 6).
    private static readonly (string Path, JsonValueKind[] Kinds)[] V020Fields =
    {
        ("HostName", new[] { JsonValueKind.String }),
        ("SmbUser", new[] { JsonValueKind.String }),
        ("SharePath", new[] { JsonValueKind.String }),
        ("ShareName", new[] { JsonValueKind.String }),
        ("Strict", new[] { JsonValueKind.True, JsonValueKind.False }),
        ("AutoPrivate", new[] { JsonValueKind.True, JsonValueKind.False }),
        ("Access", new[] { JsonValueKind.Number }),
        ("DriveLetter", new[] { JsonValueKind.String, JsonValueKind.Null }),
        ("Shortcut", new[] { JsonValueKind.String, JsonValueKind.Null }),
        ("Product", new[] { JsonValueKind.String }),
        ("Version", new[] { JsonValueKind.String }),
        ("Updated", new[] { JsonValueKind.String }),
        ("Backup", new[] { JsonValueKind.Object, JsonValueKind.Null }),
        ("Backup.Taken", new[] { JsonValueKind.String }),
        ("Backup.HostName", new[] { JsonValueKind.String }),
        ("Backup.Registry", new[] { JsonValueKind.Array }),
        ("Backup.Smb2", new[] { JsonValueKind.True, JsonValueKind.False }),
        ("Backup.RejectUnencrypted", new[] { JsonValueKind.True, JsonValueKind.False }),
        ("Backup.ServerStart", new[] { JsonValueKind.String }),
        ("Backup.Lockout", new[] { JsonValueKind.Number, JsonValueKind.Null }),
        ("Tracked", new[] { JsonValueKind.Object }),
        ("Tracked.Users", new[] { JsonValueKind.Array }),
        ("Tracked.Networks", new[] { JsonValueKind.Array }),
        ("Tracked.FirewallRules", new[] { JsonValueKind.Array }),
    };

    private static byte[] WriteFrom(string fixture)
    {
        var doc = ConfigReader.Parse(TestFiles.ConfigBytes(fixture));
        ConfigWriter.Normalize(doc.Root);
        ConfigWriter.Stamp(doc.Root, "0.3.0", StampTime);
        return ConfigWriter.Serialize(doc.Root);
    }

    [Fact]
    public void OutputMatchesTheGoldenFile()
    {
        Assert.Equal(TestFiles.ConfigBytes("v0.3.0-written.json"), WriteFrom("v0.2.0-first-run-bom.json"));
    }

    [Fact]
    public void OutputIsUtf8WithBomAndCrlf()
    {
        var bytes = WriteFrom("v0.2.x-no-bom-persian.json");

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("شبکه خانه", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v0.2.0-first-run-bom.json")]
    [InlineData("v0.2.x-scalar-arrays.json")]
    [InlineData("v0.2.x-count-shape.json")]
    [InlineData("v0.2.3-anonymised.json")]
    public void OutputKeepsEvery020FieldWithIts020Type(string fixture)
    {
        var root = JsonNode.Parse(Encoding.UTF8.GetString(WriteFrom(fixture)).TrimStart('﻿'),
            new JsonNodeOptions { PropertyNameCaseInsensitive = true })!.AsObject();

        foreach (var (path, kinds) in V020Fields)
        {
            JsonNode? node = root;
            foreach (var part in path.Split('.'))
            {
                Assert.True(ConfigJson.Has(node as JsonObject, part), fixture + ": missing " + path);
                node = ConfigJson.Get(node as JsonObject, part);
            }

            var kind = node?.GetValueKind() ?? JsonValueKind.Null;
            Assert.True(kinds.Contains(kind), fixture + ": " + path + " is " + kind);
        }

        foreach (var entry in ConfigJson.GetArray(ConfigJson.GetObject(root, "Backup"), "Registry"))
        {
            Assert.Equal(JsonValueKind.String, ConfigJson.Get(entry!.AsObject(), "Path")!.GetValueKind());
            Assert.Contains(ConfigJson.Get(entry.AsObject(), "Exists")!.GetValueKind(), new[] { JsonValueKind.True, JsonValueKind.False });
        }
    }

    [Theory]
    [InlineData("v0.2.0-first-run-bom.json")]
    [InlineData("v0.2.x-no-bom-persian.json")]
    [InlineData("v0.2.3-anonymised.json")]
    [InlineData("v0.2.1-partial.json")]
    public void RoundTripKeepsEveryOtherLeaf(string fixture)
    {
        var before = ConfigJson.Flatten(ConfigReader.Parse(TestFiles.ConfigBytes(fixture)).Root)
            .Where(p => p.Key is not ("Version" or "Updated"));
        var after = ConfigJson.Flatten(ConfigReader.Parse(WriteFrom(fixture)).Root).ToList();

        foreach (var leaf in before)
        {
            Assert.Contains(leaf, after);
        }

        // Unknown fields stay where they were; the App block is added at the end.
        Assert.Equal("App.Updated", after[^1].Key);
    }

    [Fact]
    public void UnknownFieldsArePreserved()
    {
        var doc = ConfigReader.Parse(WriteFrom("v0.2.x-no-bom-persian.json"));
        Assert.Equal(true, ConfigJson.GetBool(ConfigJson.GetObject(doc.Root, "FutureField"), "Kept"));
    }

    [Fact]
    public void SetReusesTheExistingKeySpelling()
    {
        var doc = ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.x-count-shape.json"));
        ConfigWriter.Normalize(doc.Root);

        var keys = doc.Root.Select(p => p.Key).ToList();
        Assert.Contains("tracked", keys);
        Assert.DoesNotContain("Tracked", keys);
        var users = ConfigJson.Get(ConfigJson.GetObject(doc.Root, "Tracked"), "Users");
        Assert.IsType<JsonArray>(users);
    }

    [Fact]
    public void StampSetsVersionUpdatedAndApp()
    {
        var root = new JsonObject { ["version"] = "0.2.1" };
        ConfigWriter.Stamp(root, "0.3.0", StampTime);

        Assert.Equal("0.3.0", ConfigJson.GetString(root, "Version"));
        Assert.Equal(new List<string> { "version", "Updated", "App" }, root.Select(p => p.Key).ToList());
        Assert.Equal("2000-01-08T12:00:00", ConfigJson.GetString(ConfigJson.GetObject(root, "App"), "Updated"));
    }
}
