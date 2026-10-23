using System;
using System.Collections.Generic;
using System.Net;

namespace Cpfs.Core.Monitor;

public static class MonitorMath
{
    // Byte counters (Bytes/sec in the performance data) are running totals; the rate is the change over the time
    // between two readings. A smaller second reading means the SMB server restarted: no rate for that interval.
    public static double? Rate(double? previous, double? current, double seconds)
    {
        if (previous is not { } p || current is not { } c || seconds <= 0 || c < p)
        {
            return null;
        }

        return (c - p) / seconds;
    }

    // The share name from a per-share counter instance (\\*\Share or Share), or null for the totals and the
    // administrative shares (IPC$, ADMIN$, C$), which the Monitor leaves out as 0.2.3 did.
    public static string? ShareFromInstance(string? instance)
    {
        if (string.IsNullOrWhiteSpace(instance) || string.Equals(instance, "_Total", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = instance!.Substring(instance.LastIndexOf('\\') + 1);
        if (name.Length == 0 || name.EndsWith("$", StringComparison.Ordinal))
        {
            return null;
        }

        return name;
    }

    // Adds one per-share counter instance to the totals by share name. Down takes the larger of Sent and Read bytes,
    // up the larger of Received and Write bytes (whichever of the counters this Windows fills); instances that name
    // the same share (one per SMB scope) are summed, never overwritten.
    public static void AddShareInstance(IDictionary<string, (double Down, double Up)> totals, string? instance,
        double? sent, double? read, double? received, double? written)
    {
        var share = ShareFromInstance(instance);
        if (share is null)
        {
            return;
        }

        var down = Math.Max(sent ?? 0, read ?? 0);
        var up = Math.Max(received ?? 0, written ?? 0);
        totals[share] = totals.TryGetValue(share, out var t) ? (t.Down + down, t.Up + up) : (down, up);
    }

    // The client address as SMB reports it, without IPv6 brackets and with an IPv4-mapped IPv6 address shown as IPv4.
    public static string ClientAddress(string? clientComputerName)
    {
        var text = (clientComputerName ?? "").Trim().TrimStart('[').TrimEnd(']');
        if (IPAddress.TryParse(text, out var ip) && ip.IsIPv4MappedToIPv6)
        {
            return ip.MapToIPv4().ToString();
        }

        return text;
    }

    public static bool IsVpnClient(string clientAddress) =>
        IPAddress.TryParse(clientAddress, out var ip) && Connect.ConnectAddresses.IsVpn(ip);

    // The account part of DOMAIN\user.
    public static string UserName(string? clientUserName)
    {
        var text = clientUserName ?? "";
        return text.Substring(text.LastIndexOf('\\') + 1);
    }

    // Scales a byte count to B, KB, MB, GB or TB (powers of 1024), as the 0.2.3 Monitor did; unit 0 is bytes.
    public static (double Value, int Unit) ScaleBytes(double bytes)
    {
        var value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < 4)
        {
            value /= 1024;
            unit++;
        }

        return (value, unit);
    }

    // A WMI method's ReturnValue (uint32 for the SMB classes, where 0 is success); null when it is missing or not a
    // number.
    public static uint? ReturnCode(object? returnValue)
    {
        switch (returnValue)
        {
            case uint u:
                return u;
            case int i:
                return unchecked((uint)i);
            case long l when l >= 0 && l <= uint.MaxValue:
                return (uint)l;
            case ulong ul when ul <= uint.MaxValue:
                return (uint)ul;
            case ushort us:
                return us;
            case byte b:
                return b;
            case string s when uint.TryParse(s.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            default:
                return null;
        }
    }

    // ForceClose on Windows 11 25H2 returns no ReturnValue although the session does close, so only a non-zero code
    // is taken as an answer; a missing or zero code is checked against the session list.
    public static DisconnectVerdict JudgeDisconnect(uint? returnCode, Func<bool> sessionGone)
    {
        if (returnCode is > 0)
        {
            return DisconnectVerdict.Failed;
        }

        return sessionGone() ? DisconnectVerdict.Done : DisconnectVerdict.StillConnected;
    }

    public static (long Hours, int Minutes, int Seconds) SplitDuration(long seconds)
    {
        var s = Math.Max(0, seconds);
        return (s / 3600, (int)(s / 60 % 60), (int)(s % 60));
    }
}

public enum DisconnectVerdict
{
    Done,
    Failed,
    StillConnected,
}
