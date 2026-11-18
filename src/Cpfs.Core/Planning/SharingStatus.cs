using System;
using System.Collections.Generic;
using System.Linq;

namespace Cpfs.Core.Planning;

public enum SharingState
{
    // No config.json: the card is the first-run welcome.
    NotSetUp,

    // The share exists and the Server service runs, both as read.
    On,

    // The share is missing or the Server service is not running, as read.
    NotRunning,

    // Set up, but the share or the service could not be read: the card claims neither.
    Unknown,
}

public enum StatusTone
{
    Neutral,
    Success,
    Caution,
}

public sealed record StatusFact(string Key, StatusTone Tone);

public sealed record SharingStatusView(SharingState State, string? Address, IReadOnlyList<StatusFact> Facts);

// The status card at the top of Setup. It states only what was read: a fact whose reading failed or needs
// administrator rights is left out, never guessed.
public static class SharingStatus
{
    public static SharingStatusView Decide(
        bool installed,
        string? shareName,
        string? hostName,
        Reading<IReadOnlyList<ShareInfo>> shares,
        bool? serverRunning,
        Reading<IReadOnlyList<NetworkInfo>> networks,
        IReadOnlyList<VpnAdapterInfo> vpnAdapters,
        Reading<IReadOnlyList<FirewallRuleInfo>> ownRules)
    {
        if (!installed)
        {
            return new SharingStatusView(SharingState.NotSetUp, null, Array.Empty<StatusFact>());
        }

        ShareInfo? share = null;
        bool? shareFound = null;
        if (shares is { IsOk: true } && !string.IsNullOrWhiteSpace(shareName))
        {
            share = shares.Value!.FirstOrDefault(s => string.Equals(s.Name, shareName, StringComparison.OrdinalIgnoreCase));
            shareFound = share is not null;
        }

        var state = shareFound == false || serverRunning == false ? SharingState.NotRunning
            : shareFound == true && serverRunning == true ? SharingState.On
            : SharingState.Unknown;

        var facts = new List<StatusFact>();
        if (networks is { IsOk: true })
        {
            var vpn = new HashSet<string>((vpnAdapters ?? Array.Empty<VpnAdapterInfo>()).Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
            var local = networks.Value!.Where(n => n.InterfaceAlias is null || !vpn.Contains(n.InterfaceAlias)).ToList();
            facts.Add(local.Count == 0 ? new StatusFact("Status_Net_None", StatusTone.Neutral)
                : local.Any(n => n.Category == 0) ? new StatusFact("Status_Net_Public", StatusTone.Caution)
                : new StatusFact("Status_Net_Private", StatusTone.Success));
        }

        if (share?.EncryptData is { } encrypt)
        {
            facts.Add(encrypt ? new StatusFact("Status_Encrypt_On", StatusTone.Success) : new StatusFact("Status_Encrypt_Off", StatusTone.Neutral));
        }

        if (ownRules is { IsOk: true })
        {
            var wanted = FirewallRules.Wanted(Array.Empty<string>())[0];
            var lan = ownRules.Value!.Where(r => string.Equals(r.Name, FirewallRules.LanRule, StringComparison.OrdinalIgnoreCase)).ToList();
            facts.Add(lan.Count == 1 && FirewallRules.Differences(wanted, lan[0]).Count == 0
                ? new StatusFact("Status_Firewall_Private", StatusTone.Success)
                : new StatusFact("Status_Firewall_Changed", StatusTone.Caution));
        }

        var address = state == SharingState.On && !string.IsNullOrWhiteSpace(hostName) && !string.IsNullOrWhiteSpace(shareName)
            ? @"\\" + hostName!.Trim() + @".local\" + shareName!.Trim()
            : null;
        return new SharingStatusView(state, address, facts);
    }
}
