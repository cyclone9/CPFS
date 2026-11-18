using System;
using System.Collections.Generic;
using System.Linq;

namespace Cpfs.Core.Planning;

public enum TrayState
{
    // Before the first reading came back.
    Reading,
    NotSetUp,

    // No share by the config's name, or the Server service is stopped.
    Off,
    OnPrivate,

    // Other devices can't connect here: CPFS's firewall rules open port 445 on Private networks only.
    OnPublic,
    OnNoNetwork,

    // Set up, but the share, the service or the networks could not be read.
    Unknown,
}

// What the tray icon says, from the reads an unelevated process can make. It reuses the Setup status card's decision
// and never claims more: the firewall rules are not read, so it never says the PC is reachable.
public static class TrayStatus
{
    // installed: as the Setup card has it, a config.json CPFS trusts, or one that exists but could not be read.
    // knownMissing: ConfigFile.KnownMissing; File.Exists is false on access denied too, which is not "not set up".
    public static bool Installed(bool configFound, bool configUnreadable, bool knownMissing, bool configIgnored) =>
        configFound ? !configIgnored : configUnreadable || !knownMissing;

    public static TrayState Decide(
        bool installed,
        string? shareName,
        Reading<IReadOnlyList<ShareInfo>> shares,
        bool? serverRunning,
        Reading<IReadOnlyList<NetworkInfo>> networks,
        IReadOnlyList<VpnAdapterInfo> vpnAdapters)
    {
        var view = SharingStatus.Decide(installed, shareName, null, shares, serverRunning, networks, vpnAdapters,
            Reading.Failed<IReadOnlyList<FirewallRuleInfo>>(null));
        return view.State switch
        {
            SharingState.NotSetUp => TrayState.NotSetUp,
            SharingState.NotRunning => TrayState.Off,
            SharingState.On => view.Facts.FirstOrDefault(f => f.Key.StartsWith("Status_Net_", StringComparison.Ordinal))?.Key switch
            {
                "Status_Net_Private" => TrayState.OnPrivate,
                "Status_Net_Public" => TrayState.OnPublic,
                "Status_Net_None" => TrayState.OnNoNetwork,
                _ => TrayState.Unknown,
            },
            _ => TrayState.Unknown,
        };
    }

    public static string MessageKey(TrayState state) => state switch
    {
        TrayState.Reading => "Tray_Status_Reading",
        TrayState.NotSetUp => "Tray_Status_NotSetUp",
        TrayState.Off => "Tray_Status_Off",
        TrayState.OnPrivate => "Tray_Status_OnPrivate",
        TrayState.OnPublic => "Tray_Status_OnPublic",
        TrayState.OnNoNetwork => "Tray_Status_OnNoNetwork",
        _ => "Tray_Status_Unknown",
    };

    // The share name is worth a menu line only when the share was found.
    public static bool ShowsShare(TrayState state) => state is TrayState.OnPrivate or TrayState.OnPublic or TrayState.OnNoNetwork;
}

// The exit codes of "Cpfs.exe --tray", clear of --auto-private's 10-17. CI expects Elevated on its elevated runner.
public static class TrayExit
{
    public const int Done = 0;
    public const int Elevated = 20;
    public const int Off = 21;
    public const int AlreadyRunning = 22;
    public const int Crashed = 23;
}
