using System;
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

    // Windows counts no file data per device, so a device's state comes from its session: active while it has a file
    // open or did something within the last few seconds, idle otherwise.
    public const long ActiveIdleSeconds = 5;

    public static bool IsActive(long secondsIdle, ulong openFiles) => openFiles > 0 || secondsIdle < ActiveIdleSeconds;

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
