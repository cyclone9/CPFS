using System;
using System.Globalization;
using System.Net;
using System.Text;

namespace Cpfs.Core.Monitor;

// How the Monitor recognises a connected device again: by the MAC address Windows has in its neighbor (ARP/ND)
// table for the client's IP, or by the IP when there is none (a VPN client is not a link-layer neighbor).
public static class DeviceIdentity
{
    public const string MacPrefix = "mac:";
    public const string IpPrefix = "ip:";

    // MIB_IPNET_TABLE2 / MIB_IPNET_ROW2 on x64 (netioapi.h): NumEntries is padded to 8 bytes, then 88-byte rows.
    // Row layout: SOCKADDR_INET Address (28), NET_IFINDEX (4), NET_LUID (8), PhysicalAddress[32],
    // PhysicalAddressLength (4), NL_NEIGHBOR_STATE (4), Flags (1, padded), ReachabilityTime (4).
    public const int NeighborTableHeader = 8;
    public const int NeighborRowSize = 88;
    private const int PhysicalAddressOffset = 40;
    private const int PhysicalAddressLengthOffset = 72;
    private const int StateOffset = 76;
    private const ushort AfInet = 2;
    private const ushort AfInet6 = 23;

    // NlnsUnreachable = 0 and NlnsIncomplete = 1 have no usable link-layer address.
    private const int NlnsProbe = 2;

    public static string Key(string? mac, string clientAddress) =>
        mac is { Length: > 0 } ? MacPrefix + mac : IpPrefix + NormalizeAddress(clientAddress);

    public static bool IsMacKey(string key) => key.StartsWith(MacPrefix, StringComparison.Ordinal);

    // One spelling per address, so SMB's client text and the neighbor table match: IPv4-mapped IPv6 as IPv4, and an
    // IPv6 address without its %scope.
    public static string NormalizeAddress(string? address)
    {
        var text = (address ?? "").Trim().TrimStart('[').TrimEnd(']');
        if (!IPAddress.TryParse(text, out var ip))
        {
            return text;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            return ip.MapToIPv4().ToString();
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            ip.ScopeId = 0;
        }

        return ip.ToString();
    }

    // A reverse lookup's answer, or null when it gives nothing better than the address itself.
    public static string? CleanHostName(string? hostName, string clientAddress)
    {
        var name = (hostName ?? "").Trim().TrimEnd('.');
        if (name.Length == 0 || IPAddress.TryParse(name, out _)
            || string.Equals(name, NormalizeAddress(clientAddress), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return name;
    }

    // One MIB_IPNET_ROW2 copied out of the table. Only a unicast 6-byte MAC on an entry that has one counts.
    public static bool TryParseNeighborRow(byte[] row, out string address, out string mac)
    {
        address = "";
        mac = "";
        if (row is null || row.Length < NeighborRowSize)
        {
            return false;
        }

        IPAddress ip;
        switch (BitConverter.ToUInt16(row, 0))
        {
            case AfInet:
                ip = new IPAddress(Slice(row, 4, 4));
                break;
            case AfInet6:
                ip = new IPAddress(Slice(row, 8, 16));
                break;
            default:
                return false;
        }

        if (BitConverter.ToUInt32(row, PhysicalAddressLengthOffset) != 6 || BitConverter.ToInt32(row, StateOffset) < NlnsProbe)
        {
            return false;
        }

        var physical = Slice(row, PhysicalAddressOffset, 6);
        var allZero = true;
        foreach (var b in physical)
        {
            allZero &= b == 0;
        }

        // The group bit marks multicast and broadcast entries (224.x.x.x, ff02::, x.x.x.255).
        if (allZero || (physical[0] & 1) != 0)
        {
            return false;
        }

        var text = new StringBuilder(17);
        for (var i = 0; i < physical.Length; i++)
        {
            if (i > 0)
            {
                text.Append('-');
            }

            text.Append(physical[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        address = NormalizeAddress(ip.ToString());
        mac = text.ToString();
        return true;
    }

    private static byte[] Slice(byte[] source, int offset, int count)
    {
        var result = new byte[count];
        Array.Copy(source, offset, result, 0, count);
        return result;
    }
}
