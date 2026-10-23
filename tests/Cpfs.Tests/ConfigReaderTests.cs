using System.IO;
using System.Linq;
using System.Text;
using Cpfs.Core.Config;
using Xunit;

namespace Cpfs.Tests;

public class ConfigReaderTests
{
    [Theory]
    [InlineData("v0.1.json", ConfigShape.V01, BackupState.Absent, false)]
    [InlineData("v0.2.0-first-run-bom.json", ConfigShape.V02, BackupState.Present, false)]
    [InlineData("v0.2.1-upgraded-from-0.1.json", ConfigShape.V02, BackupState.Null, false)]
    [InlineData("v0.2.1-partial.json", ConfigShape.V02, BackupState.Present, true)]
    [InlineData("v0.2.x-scalar-arrays.json", ConfigShape.V02, BackupState.Present, false)]
    [InlineData("v0.2.x-count-shape.json", ConfigShape.V02, BackupState.Present, false)]
    [InlineData("v0.2.x-no-bom-persian.json", ConfigShape.V02, BackupState.Present, false)]
    [InlineData("v0.2.3-anonymised.json", ConfigShape.V02, BackupState.Present, false)]
    [InlineData("v0.3.0-written.json", ConfigShape.V02, BackupState.Present, false)]
    public void EveryFixtureParsesWithItsShape(string file, ConfigShape shape, BackupState backup, bool partial)
    {
        var doc = ConfigReader.Parse(TestFiles.ConfigBytes(file));

        Assert.Equal(shape, doc.Shape);
        Assert.Equal(backup, doc.Backup);
        Assert.Equal(partial, doc.Partial);
    }

    [Fact]
    public void BomAndNoBomAreBothRead()
    {
        Assert.Equal(TextEncoding.Utf8Bom, ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.0-first-run-bom.json")).Encoding);
        var noBom = ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.x-no-bom-persian.json"));
        Assert.Equal(TextEncoding.Utf8, noBom.Encoding);
        Assert.Contains("شبکه خانه", noBom.TrackedNetworks);
    }

    [Fact]
    public void PowerShellEscapesAreDecoded()
    {
        var doc = ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.0-first-run-bom.json"));
        Assert.Equal(new[] { "Home <5G> & Guest's" }, doc.TrackedNetworks);
    }

    [Fact]
    public void FieldsOfA023InstallAreRead()
    {
        var doc = ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.3-anonymised.json"));

        Assert.Equal("0.2.3", doc.Version);
        Assert.Equal("HOST-01", doc.HostName);
        Assert.Equal("ShareUser", doc.SmbUser);
        Assert.Equal(@"D:\Shared", doc.SharePath);
        Assert.Equal(2, doc.Access);
        Assert.Null(doc.DriveLetter);
        Assert.Null(doc.DriveLabel);
        Assert.Equal(@"C:\Users\Public\Desktop\Shared (CPFS).lnk", doc.Shortcut);
        Assert.Equal(8, doc.BackupRegistry.Count);
        var user = Assert.Single(doc.TrackedUsers);
        Assert.Equal(new TrackedUser("ShareUser", false, false), user);
        Assert.Empty(doc.TrackedNetworks);
        Assert.Empty(doc.TrackedFirewallRules);
    }

    [Fact]
    public void ScalarArraysAreReadAsArrays()
    {
        var doc = ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.x-scalar-arrays.json"));

        Assert.Equal("ShareUser", Assert.Single(doc.TrackedUsers).Name);
        Assert.Equal(new[] { "Network 2" }, doc.TrackedNetworks);
        Assert.Empty(doc.TrackedFirewallRules);
    }

    [Fact]
    public void CountWrapperAndKeyCaseAreAccepted()
    {
        var doc = ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.x-count-shape.json"));

        Assert.Equal(ConfigShape.V02, doc.Shape);
        Assert.Equal(new TrackedUser("ShareUser", false, false), Assert.Single(doc.TrackedUsers));
        Assert.Equal(new[] { "Network", "Network 3" }, doc.TrackedNetworks);
        Assert.Equal(2, doc.TrackedFirewallRules.Count);
        Assert.Equal(8, doc.BackupRegistry.Count);
    }

    [Fact]
    public void ProviderPathsMapTo64BitRegistryLocations()
    {
        var doc = ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.0-first-run-bom.json"));
        var first = doc.BackupRegistry[0];

        Assert.Equal(new RegistryLocation(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa"), first.Location);
        Assert.True(first.Exists);
        Assert.Equal(0, first.Value);
        var missing = doc.BackupRegistry.Single(r => r.Name == "AutoShareWks");
        Assert.False(missing.Exists);
        Assert.Null(missing.Value);

        Assert.Equal(new RegistryLocation(RegistryRoot.CurrentUser, @"Software\X"), RegistryLocation.FromProviderPath(@"HKCU:\Software\X\"));
        Assert.Null(RegistryLocation.FromProviderPath(@"HKLMX:\Software"));
        Assert.Null(RegistryLocation.FromProviderPath(@"C:\Windows"));
    }

    [Fact]
    public void Utf16WithBomIsRead()
    {
        var text = File.ReadAllText(TestFiles.Config("v0.1.json"));
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();

        var doc = ConfigReader.Parse(bytes);

        Assert.Equal(TextEncoding.Utf16LeBom, doc.Encoding);
        Assert.Equal("ShareUser", doc.SmbUser);
    }

    [Fact]
    public void InvalidUtf8WithoutBomUsesTheFallbackEncoding()
    {
        var bytes = new byte[] { (byte)'{', (byte)'"', (byte)'A', (byte)'"', (byte)':', (byte)'"', 0xE9, (byte)'"', (byte)'}' };

        Assert.Throws<DecoderFallbackException>(() => ConfigReader.Parse(bytes));
        var doc = ConfigReader.Parse(bytes, Encoding.Latin1);
        Assert.Equal(TextEncoding.Fallback, doc.Encoding);
        Assert.Equal("é", ConfigJson.GetString(doc.Root, "a"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[1, 2]")]
    [InlineData("{\"HostName\": ")]
    [InlineData("{\"SmbUser\": \"a\", \"smbuser\": \"b\"}")]
    public void BrokenFilesGiveAFormatError(string text)
    {
        Assert.Throws<ConfigFormatException>(() => ConfigReader.Parse(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void FlattenListsEveryLeafInFileOrder()
    {
        var doc = ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.3-anonymised.json"));
        var flat = ConfigJson.Flatten(doc.Root);

        Assert.Equal("HostName", flat[0].Key);
        Assert.Contains(flat, p => p.Key == "Backup.Registry[7].Name" && p.Value == "AutoShareWks");
        Assert.Contains(flat, p => p.Key == "Tracked.Networks" && p.Value == "[]");
        Assert.Contains(flat, p => p.Key == "DriveLetter" && p.Value == "null");
    }
}
