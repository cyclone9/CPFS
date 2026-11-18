using System;
using System.Collections.Generic;
using Cpfs.Core.Planning;
using Cpfs.Core.Preferences;
using Cpfs.Core.Text;
using Xunit;

namespace Cpfs.Tests;

public class TrayTests
{
    private static readonly IReadOnlyList<VpnAdapterInfo> NoVpn = Array.Empty<VpnAdapterInfo>();

    private static Reading<IReadOnlyList<ShareInfo>> Shares(params string[] names)
    {
        var list = new List<ShareInfo>();
        foreach (var n in names)
        {
            list.Add(new ShareInfo(n, @"D:\" + n, null, true, 1, 0, false));
        }

        return Reading.Ok<IReadOnlyList<ShareInfo>>(list);
    }

    private static Reading<IReadOnlyList<NetworkInfo>> Nets(params NetworkInfo[] nets) => Reading.Ok<IReadOnlyList<NetworkInfo>>(nets);

    [Fact]
    public void EveryStateComesFromWhatWasRead()
    {
        var privateNet = Nets(new NetworkInfo("Home", "Wi-Fi", 1));
        Assert.Equal(TrayState.NotSetUp, TrayStatus.Decide(false, null, Shares(), true, privateNet, NoVpn));
        Assert.Equal(TrayState.OnPrivate, TrayStatus.Decide(true, "Shared", Shares("shared"), true, privateNet, NoVpn));
        Assert.Equal(TrayState.Off, TrayStatus.Decide(true, "Shared", Shares("other"), true, privateNet, NoVpn));
        Assert.Equal(TrayState.Off, TrayStatus.Decide(true, "Shared", Shares("Shared"), false, privateNet, NoVpn));
        Assert.Equal(TrayState.OnPublic, TrayStatus.Decide(true, "Shared", Shares("Shared"), true,
            Nets(new NetworkInfo("Home", "Wi-Fi", 1), new NetworkInfo("Cafe", "Ethernet", 0)), NoVpn));

        // The VPN adapter's profile is not "a network" here, as on Setup's card.
        Assert.Equal(TrayState.OnNoNetwork, TrayStatus.Decide(true, "Shared", Shares("Shared"), true,
            Nets(new NetworkInfo("tailnet", "Tailscale", 0)), new[] { new VpnAdapterInfo("Tailscale", "100.64.0.1") }));

        // Nothing read is never "on": a share or network that could not be read, or no share name.
        Assert.Equal(TrayState.Unknown, TrayStatus.Decide(true, "Shared", Reading.Failed<IReadOnlyList<ShareInfo>>("x"), true, privateNet, NoVpn));
        Assert.Equal(TrayState.Unknown, TrayStatus.Decide(true, "Shared", Shares("Shared"), null, privateNet, NoVpn));
        Assert.Equal(TrayState.Unknown, TrayStatus.Decide(true, "Shared", Shares("Shared"), true, Reading.Failed<IReadOnlyList<NetworkInfo>>("x"), NoVpn));
        Assert.Equal(TrayState.Unknown, TrayStatus.Decide(true, null, Shares("Shared"), true, privateNet, NoVpn));
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, false, true)]
    public void InstalledFollowsSetupsCard(bool found, bool unreadable, bool knownMissing, bool ignored, bool installed)
    {
        Assert.Equal(installed, TrayStatus.Installed(found, unreadable, knownMissing, ignored));
    }

    [Fact]
    public void TheFolderIsOpenedOnlyFromATrustedConfig()
    {
        Reading<DataFolderInfo> Folder(OwnerKind config, bool link = false) =>
            Reading.Ok(new DataFolderInfo(true, true, true, false) { Owner = OwnerKind.Administrators, ConfigOwner = config, IsLink = link });

        Assert.True(ConfigTrust.Trusted(Folder(OwnerKind.Administrators)));
        Assert.False(ConfigTrust.Trusted(Folder(OwnerKind.Other)));
        Assert.False(ConfigTrust.Trusted(Folder(OwnerKind.Administrators, link: true)));
        Assert.False(ConfigTrust.Trusted(Reading.Failed<DataFolderInfo>("x")));
        Assert.False(ConfigTrust.Trusted(Reading.NeedsAdmin<DataFolderInfo>()));
        Assert.False(ConfigTrust.Trusted(Reading.Ok(new DataFolderInfo(false, false, false, false))));
        Assert.False(ConfigTrust.Trusted(Reading.Ok(new DataFolderInfo(true, true, true, false) { Owner = OwnerKind.Administrators })));
    }

    [Theory]
    [InlineData("CPFS", "Sharing is on, Private network", "Shared-Server")]
    [InlineData("CPFS", "Paylaşım açık, ancak bu ağ Genel olarak ayarlı; diğer cihazlar buraya bağlanamaz", "Shared")]
    [InlineData("CPFS", "Paylaşım açık, Özel ağ", "Bir-cok-uzun-paylasim-adi-Ilkbahar-Istanbul-Izmir-Ankara-Bursa-Antalya-Konya-Ad")]
    [InlineData("CPFS", "\u0627\u0644\u0645\u0634\u0627\u0631\u0643\u0629 \u0645\u0641\u0639\u0651\u0644\u0629", "\U0001F4C1\U0001F4C1\U0001F4C1\U0001F4C1\U0001F4C1\U0001F4C1\U0001F4C1\U0001F4C1\U0001F4C1\U0001F4C1\U0001F4C1\U0001F4C1\U0001F4C1\U0001F4C1\U0001F4C1\U0001F4C1\U0001F4C1\U0001F4C1")]
    public void TheTooltipNeverExceedsWhatNotifyIconTakes(string title, string status, string share)
    {
        var text = TrayTooltip.Fit(title, status, share);

        Assert.True(text.Length <= TrayTooltip.Max, text.Length + ": " + text);
        Assert.StartsWith(title + "\n", text, StringComparison.Ordinal);
        for (var i = 0; i < text.Length; i++)
        {
            Assert.False(char.IsHighSurrogate(text[i]) && (i + 1 == text.Length || !char.IsLowSurrogate(text[i + 1])), "split pair at " + i);
        }
    }

    [Fact]
    public void AShortTooltipKeepsTheWholeShareName()
    {
        Assert.Equal("CPFS\nSharing is off\nShared", TrayTooltip.Fit("CPFS", "Sharing is off", "Shared"));
        Assert.Equal("CPFS\nSharing is off", TrayTooltip.Fit("CPFS", "Sharing is off", null));

        var cut = TrayTooltip.Fit("CPFS", "Sharing is on, Private network", new string('S', 80));
        Assert.Equal(TrayTooltip.Max, cut.Length);
        Assert.EndsWith("\u2026", cut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrayPreferenceIsOffUnlessSaved()
    {
        Assert.False(AppPreferences.Parse("{\"Theme\": \"Dark\"}").TrayIcon);
        Assert.False(AppPreferences.Parse("{\"TrayIcon\": \"true\"}").TrayIcon);
        Assert.True(AppPreferences.Parse(new AppPreferences { TrayIcon = true }.Serialize()).TrayIcon);
    }
}
