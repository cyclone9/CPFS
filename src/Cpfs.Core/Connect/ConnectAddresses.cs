using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Cpfs.Core.Connect;

public enum AddressKind
{
    // <Computer>.local, resolved by mDNS on the same network.
    Mdns,

    // A Wi-Fi or LAN IPv4 address.
    Lan,

    // An address in a Tailscale/Headscale range.
    Vpn,

    // The MagicDNS name, resolved by the VPN client.
    MagicDns,
}

// One IP address of a network adapter, as the Windows layer read it.
public sealed record AdapterAddress(string Adapter, IPAddress Address, string? DnsSuffix = null);

public sealed record ConnectAddress(string Host, AddressKind Kind, string? Adapter)
{
    public bool ThroughVpn => Kind is AddressKind.Vpn or AddressKind.MagicDns;
}

public static class ConnectAddresses
{
    // 100.64.0.0/10 (CGNAT, used by Tailscale and Headscale for IPv4) and fd7a:115c:a1e0::/48 (their IPv6 range).
    public static bool IsVpn(IPAddress address)
    {
        if (address is null)
        {
            throw new ArgumentNullException(nameof(address));
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 100 && (b[1] & 0xC0) == 64;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && b[0] == 0xFD && b[1] == 0x7A && b[2] == 0x11 && b[3] == 0x5C && b[4] == 0xA1 && b[5] == 0xE0;
    }

    // The addresses another device can use, in the 0.2.3 order: the .local name first (works without knowing an
    // IP), then each IPv4 address in adapter order, then the MagicDNS names when a VPN address is present.
    // Loopback, link-local (169.254/16) and unspecified addresses can't be reached from another device.
    public static IReadOnlyList<ConnectAddress> Build(string hostName, IEnumerable<AdapterAddress> adapters)
    {
        if (string.IsNullOrWhiteSpace(hostName))
        {
            throw new ArgumentException("Host name is empty.", nameof(hostName));
        }

        var result = new List<ConnectAddress> { new(hostName + ".local", AddressKind.Mdns, null) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hostName + ".local" };
        var suffixes = new List<string>();
        var vpn = false;
        foreach (var a in adapters ?? Array.Empty<AdapterAddress>())
        {
            var ip = a.Address.IsIPv4MappedToIPv6 ? a.Address.MapToIPv4() : a.Address;
            var isVpn = IsVpn(ip);
            if (isVpn)
            {
                vpn = true;
                var suffix = (a.DnsSuffix ?? "").Trim().TrimEnd('.');
                if (suffix.Length > 0 && !suffixes.Contains(suffix, StringComparer.OrdinalIgnoreCase))
                {
                    suffixes.Add(suffix);
                }
            }

            // IPv6 literals need brackets in URLs and a special form in UNC paths; phones and the guide use IPv4.
            if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) || IsLinkLocal(ip)
                || ip.Equals(IPAddress.Any))
            {
                continue;
            }

            var text = ip.ToString();
            if (seen.Add(text))
            {
                result.Add(new ConnectAddress(text, isVpn ? AddressKind.Vpn : AddressKind.Lan, a.Adapter));
            }
        }

        if (vpn)
        {
            // MagicDNS names are lower case; the short name works through the tailnet's search domain.
            var shortName = hostName.ToLowerInvariant();
            if (seen.Add(shortName))
            {
                result.Add(new ConnectAddress(shortName, AddressKind.MagicDns, null));
            }

            foreach (var suffix in suffixes)
            {
                var full = shortName + "." + suffix.ToLowerInvariant();
                if (seen.Add(full))
                {
                    result.Add(new ConnectAddress(full, AddressKind.MagicDns, null));
                }
            }
        }

        return result;
    }

    private static bool IsLinkLocal(IPAddress ipv4)
    {
        var b = ipv4.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }
}
