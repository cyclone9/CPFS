using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Cpfs.Core.Connect;

namespace Cpfs.Core.Connections;

// One USE_INFO_2 entry from NetUseEnum, as the Windows layer read it. The password member is never read.
public sealed record SmbUse(string? Local, string Remote, uint Status, uint AssignmentType, uint OpenCount, string? User, string? Domain)
{
    // USE_INFO_2 ui2_status and ui2_asg_type values (lmuse.h).
    public const uint StatusOk = 0;
    public const uint StatusPaused = 1;
    public const uint StatusSessionLost = 2;
    public const uint StatusNetworkError = 3;
    public const uint StatusConnecting = 4;
    public const uint StatusReconnecting = 5;
    public const uint TypeIpc = 3;
}

// One persistent connection from WNetOpenEnum(RESOURCE_REMEMBERED). Windows keeps no user name in it here.
public sealed record RememberedMapping(string? Local, string Remote, string? Provider);

public enum ClientConnectionState
{
    Connected,
    Reconnecting,
    Disconnected,
    SavedNotConnected,
}

public sealed record ClientConnection(
    string Server,
    string Share,
    string? DriveLetter,
    string? Account,
    ClientConnectionState State,
    bool ReconnectsAtSignIn,
    bool IsSignIn,
    uint OpenCount);

public sealed record ClientConnectionGroup(string Server, bool IsThisPc, IReadOnlyList<ClientConnection> Connections);

public static class ClientConnections
{
    // "\\server\share" (or deeper) into the server as written and the rest. Null for anything that isn't a UNC path.
    public static (string Server, string Share)? SplitUnc(string? remote)
    {
        if (remote is null || remote.Length < 3 || !remote.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = remote.Substring(2);
        var slash = rest.IndexOf('\\');
        var server = slash < 0 ? rest : rest.Substring(0, slash);
        var share = slash < 0 ? "" : rest.Substring(slash + 1).TrimEnd('\\');
        return server.Length == 0 ? null : (server, share);
    }

    public static ClientConnectionState StateOf(uint status) => status switch
    {
        SmbUse.StatusOk => ClientConnectionState.Connected,
        SmbUse.StatusConnecting or SmbUse.StatusReconnecting => ClientConnectionState.Reconnecting,
        _ => ClientConnectionState.Disconnected,
    };

    // A remembered mapping belongs on the page only when it is an SMB one: its provider is the LanMan provider (the
    // name Windows gives it is localized, so it comes from WNetGetProviderName, never a literal), or it is a drive
    // NetUseEnum lists, which knows only the SMB client. SSHFS-Win and WebDAV drives fail both.
    public static bool KeepRemembered(RememberedMapping mapping, string? lanmanProvider, IEnumerable<SmbUse> uses)
    {
        if (mapping is null || SplitUnc(mapping.Remote) is null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(lanmanProvider) && string.Equals(mapping.Provider, lanmanProvider, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return (uses ?? Array.Empty<SmbUse>()).Any(u => SameUse(u, mapping));
    }

    // Groups by the server string as Windows holds it: a name, its .local form and an IP address stay apart, because
    // Windows counts them as different servers. Case doesn't split a group.
    public static IReadOnlyList<ClientConnectionGroup> Build(
        IEnumerable<SmbUse> uses, IEnumerable<RememberedMapping> remembered, string? lanmanProvider, Func<string, bool> isThisPc)
    {
        if (isThisPc is null)
        {
            throw new ArgumentNullException(nameof(isThisPc));
        }

        var useList = (uses ?? Array.Empty<SmbUse>()).ToList();
        var saved = (remembered ?? Array.Empty<RememberedMapping>()).Where(m => KeepRemembered(m, lanmanProvider, useList)).ToList();
        var rows = new List<ClientConnection>();
        foreach (var use in useList)
        {
            if (SplitUnc(use.Remote) is not { } unc)
            {
                continue;
            }

            var persistent = saved.Any(m => SameUse(use, m));
            var signIn = use.AssignmentType == SmbUse.TypeIpc || string.Equals(unc.Share, "IPC$", StringComparison.OrdinalIgnoreCase);
            rows.Add(new ClientConnection(unc.Server, unc.Share, Letter(use.Local), Account(use.Domain, use.User), StateOf(use.Status),
                persistent, signIn, use.OpenCount));
        }

        foreach (var mapping in saved)
        {
            if (useList.Any(u => SameUse(u, mapping)) || SplitUnc(mapping.Remote) is not { } unc)
            {
                continue;
            }

            rows.Add(new ClientConnection(unc.Server, unc.Share, Letter(mapping.Local), null, ClientConnectionState.SavedNotConnected,
                true, false, 0));
        }

        var groups = new List<ClientConnectionGroup>();
        foreach (var byServer in rows.GroupBy(r => r.Server, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = byServer
                .OrderBy(r => r.IsSignIn)
                .ThenBy(r => r.DriveLetter is null)
                .ThenBy(r => r.DriveLetter ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Share, StringComparer.OrdinalIgnoreCase)
                .ToList();
            groups.Add(new ClientConnectionGroup(ordered[0].Server, isThisPc(ordered[0].Server), ordered));
        }

        return groups;
    }

    private static bool SameUse(SmbUse use, RememberedMapping mapping) =>
        !string.IsNullOrEmpty(use.Local)
        && string.Equals(Letter(use.Local), Letter(mapping.Local), StringComparison.OrdinalIgnoreCase)
        && string.Equals(use.Remote.TrimEnd('\\'), mapping.Remote.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // "x:" as "X:"; null for a deviceless connection.
    private static string? Letter(string? local) =>
        string.IsNullOrWhiteSpace(local) ? null : local!.Trim().ToUpperInvariant();

    private static string? Account(string? domain, string? user)
    {
        if (string.IsNullOrEmpty(user))
        {
            return null;
        }

        return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
    }
}

// The names and addresses this PC answers to, for the "this PC" tag. Built from what the Windows layer read; makes no
// name lookups of its own.
public sealed class ThisPcNames
{
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IPAddress> _addresses = new();

    public ThisPcNames(string hostName, string? netBiosName, string? domainName, IEnumerable<AdapterAddress> adapters)
    {
        var list = (adapters ?? Array.Empty<AdapterAddress>()).ToList();
        foreach (var name in new[] { hostName, netBiosName })
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            _names.Add(name!);
            _names.Add(name + ".local");
            if (!string.IsNullOrWhiteSpace(domainName))
            {
                _names.Add(name + "." + domainName!.Trim().TrimEnd('.'));
            }
        }

        _names.Add("localhost");
        if (!string.IsNullOrWhiteSpace(hostName))
        {
            // The .local name, the LAN and VPN addresses and the MagicDNS names the Connect page offers.
            foreach (var a in ConnectAddresses.Build(hostName, list))
            {
                _names.Add(a.Host);
            }
        }

        foreach (var a in list)
        {
            _addresses.Add(a.Address.IsIPv4MappedToIPv6 ? a.Address.MapToIPv4() : a.Address);
        }
    }

    public bool Matches(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return false;
        }

        var s = server!.Trim().TrimEnd('.');
        if (_names.Contains(s))
        {
            return true;
        }

        // Only text that looks like an address: IPAddress.TryParse would also take an all-digit server name.
        var bare = s.Trim('[', ']');
        if ((bare.IndexOf('.') >= 0 || bare.IndexOf(':') >= 0) && IPAddress.TryParse(bare, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }

            var bytes = ip.GetAddressBytes();
            return _addresses.Any(a => a.AddressFamily == ip.AddressFamily && a.GetAddressBytes().SequenceEqual(bytes));
        }

        return false;
    }
}
