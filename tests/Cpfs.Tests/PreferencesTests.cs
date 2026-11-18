using Cpfs.Core.Preferences;
using Xunit;

namespace Cpfs.Tests;

public class PreferencesTests
{
    [Fact]
    public void RoundTrip()
    {
        var prefs = new AppPreferences { Theme = "Light", Language = "fa", CheckUpdatesAtStart = true, SaveActivityLog = true };
        prefs.Fonts["fa"] = "Tahoma";

        var back = AppPreferences.Parse(prefs.Serialize());

        Assert.Equal("Light", back.Theme);
        Assert.Equal("fa", back.Language);
        Assert.Equal("Tahoma", back.Fonts["FA"]);
        Assert.True(back.CheckUpdatesAtStart);
        Assert.True(back.SaveActivityLog);
    }

    // dev4 saved "AlwaysShowDetails"; the switch is gone and the field is ignored.
    [Fact]
    public void AnOldFieldIsIgnored()
    {
        var prefs = AppPreferences.Parse("{\"Theme\": \"Dark\", \"AlwaysShowDetails\": true}");

        Assert.Equal("Dark", prefs.Theme);
        Assert.DoesNotContain("AlwaysShowDetails", prefs.Serialize(), System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("{\"Theme\": 5, \"Fonts\": []}")]
    public void BrokenFilesGiveTheDefaults(string? json)
    {
        var prefs = AppPreferences.Parse(json);

        Assert.Equal(AppPreferences.DefaultTheme, prefs.Theme);
        Assert.Null(prefs.Language);
        Assert.Empty(prefs.Fonts);
        Assert.False(prefs.CheckUpdatesAtStart);
        Assert.False(prefs.SaveActivityLog);
    }

    [Fact]
    public void ConnectDeviceIsKeptAndItsAbsenceIsTolerated()
    {
        var prefs = new AppPreferences { ConnectDevice = "android" };
        Assert.Equal("android", AppPreferences.Parse(prefs.Serialize()).ConnectDevice);
        Assert.Null(AppPreferences.Parse("{\"Theme\":\"Dark\"}").ConnectDevice);
        Assert.Null(AppPreferences.Parse("{\"ConnectDevice\":3}").ConnectDevice);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("android", 1)]
    [InlineData(" LINUX ", 4)]
    [InlineData("symbian", 0)]
    public void ConnectTabFallsBackToThePhones(string? saved, int index) =>
        Assert.Equal(index, Cpfs.Core.Connect.GuideTabs.IndexOf(saved));
}
