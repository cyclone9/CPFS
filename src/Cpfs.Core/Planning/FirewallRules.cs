using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Cpfs.Core.Planning;

// The rule CPFS wants, in the firewall API's terms: Protocol 6 TCP / 17 UDP, Profiles bits Domain 1, Private 2,
// Public 4 (all 0x7FFFFFFF), Direction 1 inbound, Action 1 allow.
public sealed record RuleSpec(string Name, int Protocol, string LocalPorts, int Profiles, IReadOnlyList<string> RemoteAddresses, IReadOnlyList<string> Interfaces);

public static class FirewallRules
{
    public const int Tcp = 6;
    public const int Udp = 17;
    public const int ProfilePrivate = 2;
    public const int ProfilePublic = 4;
    public const int ProfileAll = 0x7FFFFFFF;
    public const int Inbound = 1;
    public const int Allow = 1;

    public const string LanRule = "CPFS SMB (LAN)";
    public const string MdnsRule = "CPFS mDNS (LAN)";
    public const string VpnRule = "CPFS SMB (VPN)";

    // File and Printer Sharing.
    public const string FileSharingGroup = "@FirewallAPI.dll,-28502";

    private static readonly string[] LocalSubnet = { "LocalSubnet" };

    public static readonly IReadOnlyList<string> VpnRanges = new[] { "100.64.0.0/10", "fd7a:115c:a1e0::/48" };

    public static bool IsOwnName(string? name) => name is not null
        && (name.StartsWith("CPFS", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CyShare", StringComparison.OrdinalIgnoreCase));

    public static bool IsAllProfiles(int profiles) => profiles == 0 || (profiles & 7) == 7;

    public static IReadOnlyList<RuleSpec> Wanted(IReadOnlyList<string> vpnInterfaces)
    {
        var list = new List<RuleSpec>
        {
            new(LanRule, Tcp, "445", ProfilePrivate, LocalSubnet, Array.Empty<string>()),
            new(MdnsRule, Udp, "5353", ProfilePrivate, Array.Empty<string>(), Array.Empty<string>()),
        };
        if (vpnInterfaces.Count > 0)
        {
            list.Add(new RuleSpec(VpnRule, Tcp, "445", ProfileAll, VpnRanges, vpnInterfaces));
        }

        return list;
    }

    // The fields of an existing rule that differ from the wanted one, as words for the plan; empty when it matches.
    public static List<string> Differences(RuleSpec wanted, FirewallRuleInfo rule)
    {
        var diff = new List<string>();
        if (!rule.Enabled)
        {
            diff.Add("Enabled");
        }

        if (rule.Direction != Inbound)
        {
            diff.Add("Direction");
        }

        if (rule.Action != Allow)
        {
            diff.Add("Action");
        }

        if (rule.Protocol != wanted.Protocol)
        {
            diff.Add("Protocol");
        }

        if (!SameList(Tokens(rule.LocalPorts, false), Tokens(wanted.LocalPorts, false)))
        {
            diff.Add("LocalPorts");
        }

        var profilesMatch = IsAllProfiles(wanted.Profiles) ? IsAllProfiles(rule.Profiles) : rule.Profiles == wanted.Profiles;
        if (!profilesMatch)
        {
            diff.Add("Profiles");
        }

        if (!SameList(NormalizeAddresses(rule.RemoteAddresses), NormalizeAddresses(string.Join(",", wanted.RemoteAddresses))))
        {
            diff.Add("RemoteAddresses");
        }

        if (!SameList(Sorted(rule.Interfaces), Sorted(wanted.Interfaces)))
        {
            diff.Add("Interfaces");
        }

        return diff;
    }

    // A CPFS rule is written disabled, read back and enabled only when it matches; then read back once more. The
    // rules of this name as read back, against the spec and the state the rule should be in at this stage. Empty
    // when it may go on; otherwise the rule must be removed, since a rule bound to the wrong interfaces or profiles
    // would open 445 where it must not.
    public static List<string> StagedProblems(RuleSpec wanted, IReadOnlyList<FirewallRuleInfo> named, bool enabled)
    {
        if (named.Count == 0)
        {
            return new List<string> { "Missing" };
        }

        if (named.Count > 1)
        {
            return new List<string> { "Count" };
        }

        var problems = Differences(wanted, named[0]);
        problems.Remove("Enabled");
        if (named[0].Enabled != enabled)
        {
            problems.Insert(0, "Enabled");
        }

        return problems;
    }

    public static bool HasPort(IReadOnlyList<string> ports, string port)
    {
        foreach (var p in ports)
        {
            foreach (var token in Tokens(p, false))
            {
                if (string.Equals(token, port, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The firewall API reports IPv4 subnets in mask form ("100.64.0.0/255.192.0.0"); compare them as prefixes. An
    // empty list means any address.
    public static List<string> NormalizeAddresses(string? text)
    {
        var result = new List<string>();
        foreach (var token in Tokens(text, true))
        {
            if (token == "*")
            {
                return new List<string>();
            }

            result.Add(NormalizeAddress(token));
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static string NormalizeAddress(string token)
    {
        if (string.Equals(token, "localsubnet", StringComparison.OrdinalIgnoreCase))
        {
            return "LocalSubnet";
        }

        var slash = token.IndexOf('/');
        var addressText = slash < 0 ? token : token.Substring(0, slash);
        if (!IPAddress.TryParse(addressText, out var address))
        {
            return token.ToUpperInvariant();
        }

        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = max;
        if (slash >= 0)
        {
            var maskText = token.Substring(slash + 1);
            if (int.TryParse(maskText, NumberStyles.None, CultureInfo.InvariantCulture, out var bits))
            {
                prefix = bits;
            }
            else if (IPAddress.TryParse(maskText, out var mask) && mask.AddressFamily == AddressFamily.InterNetwork)
            {
                prefix = PrefixOf(mask.GetAddressBytes());
            }
        }

        return address.ToString().ToLowerInvariant() + "/" + prefix.ToString(CultureInfo.InvariantCulture);
    }

    private static int PrefixOf(byte[] mask)
    {
        var bits = 0;
        foreach (var b in mask)
        {
            for (var i = 7; i >= 0; i--)
            {
                if ((b & (1 << i)) == 0)
                {
                    return bits;
                }

                bits++;
            }
        }

        return bits;
    }

    private static List<string> Tokens(string? text, bool keepCase)
    {
        var list = new List<string>();
        foreach (var part in (text ?? "").Split(','))
        {
            var t = part.Trim();
            if (t.Length > 0)
            {
                list.Add(keepCase ? t : t.ToUpperInvariant());
            }
        }

        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static List<string> Sorted(IEnumerable<string> items)
    {
        var list = new List<string>();
        foreach (var i in items)
        {
            if (!string.IsNullOrWhiteSpace(i))
            {
                list.Add(i.Trim().ToUpperInvariant());
            }
        }

        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static bool SameList(List<string> a, List<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
