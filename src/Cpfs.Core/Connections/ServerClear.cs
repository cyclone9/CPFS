using System;
using System.Collections.Generic;
using System.Linq;

namespace Cpfs.Core.Connections;

public enum ServerClearError
{
    None,
    Empty,

    // A colon: a drive letter or another device name, never a server.
    Device,
    Wildcard,

    // More than \\server\share, a forward slash, or a backslash anywhere but the leading \\ and the one separator.
    Path,
    Server,

    // A name SSHFS-Win answers to (\\sshfs\..., \\sshfs.r\... and so on): not an SMB server.
    NotSmb,
    Share,
}

// A server the user typed, and an optional share on it.
public sealed record ServerClearRequest(string Server, string? Share);

// One deviceless name NetUseDel is called with, and whether the user typed it (false: IPC$ or a listed deviceless share).
public sealed record ServerClearTarget(string Remote, bool Typed);

public enum ServerClearVerdict
{
    // Windows answered 0.
    Ended,

    // 2250: Windows holds no connection by that name that this session can end.
    NotHeld,

    // 2401 or 2404: kept; only that target's own "Disconnect anyway" may force it (B3, B10).
    OpenFiles,
    Failed,
}

// "End connections to this server" on the Connections page (design record 2026-11-19, section 3.1; B9, B10). A typed
// server becomes deviceless names only: \\server\share and \\server\IPC$, never a drive letter or a saved mapping,
// and they go only to NetUseDel, which reaches the SMB client alone.
public static class ServerClear
{
    // B11: the button stays hidden until CI (or the laptop) shows NetUseDel ending a connection NetUseEnum did not list.
    // The CI apply test prints "B11 proven" or "B11 not proven"; only a proven run may switch this on.
    public static bool Shown => false;

    public const int NerrUseNotFound = 2250;

    private const int MaxServer = 253;
    private const int MaxLabel = 63;
    private const int MaxShare = 80;

    private static readonly string[] SshfsPrefixes = { "sshfs", "sshfs.r", "sshfs.k", "sshfs.kr" };
    private static readonly char[] Wildcards = { '*', '?' };
    private static readonly char[] BadShareChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

    public static ServerClearError Parse(string? input, out ServerClearRequest? request)
    {
        request = null;
        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            return ServerClearError.Empty;
        }

        if (text.IndexOf(':') >= 0)
        {
            return ServerClearError.Device;
        }

        if (text.IndexOfAny(Wildcards) >= 0)
        {
            return ServerClearError.Wildcard;
        }

        if (text.IndexOf('/') >= 0)
        {
            return ServerClearError.Path;
        }

        var lead = text.StartsWith(@"\\", StringComparison.Ordinal);
        var body = lead ? text.Substring(2) : text;
        if (!lead && body.IndexOf('\\') >= 0)
        {
            return ServerClearError.Path;
        }

        var parts = body.TrimEnd('\\').Split('\\');
        if (parts.Length > 2 || parts.Any(p => p.Length == 0))
        {
            return ServerClearError.Path;
        }

        var server = parts[0];
        if (!IsServer(server))
        {
            return ServerClearError.Server;
        }

        if (SshfsPrefixes.Contains(server, StringComparer.OrdinalIgnoreCase))
        {
            return ServerClearError.NotSmb;
        }

        string? share = null;
        if (parts.Length == 2)
        {
            share = parts[1];
            if (share.Length > MaxShare || share.IndexOfAny(BadShareChars) >= 0 || share.Any(char.IsControl) || share.Trim('.').Length == 0)
            {
                return ServerClearError.Share;
            }
        }

        request = new ServerClearRequest(server, share);
        return ServerClearError.None;
    }

    // The typed share first, then any deviceless share the list shows on that server, IPC$ last so it isn't ended while
    // a share on it is still open. A share the list shows under a drive letter is left out: that drive has its own
    // Disconnect, and this path never removes a drive or a saved mapping.
    public static IReadOnlyList<ServerClearTarget> Targets(ServerClearRequest request, IEnumerable<ClientConnection>? listed)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var rows = (listed ?? Array.Empty<ClientConnection>())
            .Where(r => string.Equals(r.Server, request.Server, StringComparison.OrdinalIgnoreCase)).ToList();
        var onDrive = new HashSet<string>(rows.Where(r => r.DriveLetter is not null).Select(r => r.Share), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ServerClearTarget>();

        void Add(string share, bool typed)
        {
            if (!string.Equals(share, "IPC$", StringComparison.OrdinalIgnoreCase) && !onDrive.Contains(share) && seen.Add(share))
            {
                list.Add(new ServerClearTarget(@"\\" + request.Server + @"\" + share, typed));
            }
        }

        if (request.Share is { } typedShare)
        {
            Add(typedShare, true);
        }

        foreach (var r in rows.Where(r => r.DriveLetter is null && !r.IsSignIn && r.Share.Length > 0))
        {
            Add(r.Share, false);
        }

        list.Add(new ServerClearTarget(@"\\" + request.Server + @"\IPC$", request.Share is { } s && string.Equals(s, "IPC$", StringComparison.OrdinalIgnoreCase)));
        return list;
    }

    // Shares the user typed that the list holds under a drive letter: the page names them instead of ending them.
    public static IReadOnlyList<string> OnDrive(ServerClearRequest request, IEnumerable<ClientConnection>? listed)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return (listed ?? Array.Empty<ClientConnection>())
            .Where(r => r.DriveLetter is not null && string.Equals(r.Server, request.Server, StringComparison.OrdinalIgnoreCase)
                && request.Share is not null && string.Equals(r.Share, request.Share, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.DriveLetter!)
            .ToList();
    }

    public static ServerClearVerdict Judge(int code) => code switch
    {
        0 => ServerClearVerdict.Ended,
        NerrUseNotFound => ServerClearVerdict.NotHeld,
        _ when ClientDisconnect.IsOpenFiles(code) => ServerClearVerdict.OpenFiles,
        _ => ServerClearVerdict.Failed,
    };

    // A host name, a .local name, an ipv6-literal.net name or an IPv4 address. Raw IPv6 has colons and is refused above.
    private static bool IsServer(string server)
    {
        if (server.Length > MaxServer || server.StartsWith(".", StringComparison.Ordinal) || server.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var labels = server.Split('.');
        if (labels.Length == 4 && labels.All(l => l.Length > 0 && l.All(c => c is >= '0' and <= '9')))
        {
            return labels.All(l => l.Length <= 3 && int.Parse(l, System.Globalization.CultureInfo.InvariantCulture) <= 255);
        }

        // Letters and digits of any script (a PC may carry a Persian name, which can hold a ZWNJ), hyphen and underscore.
        return labels.All(l => l.Length > 0 && l.Length <= MaxLabel && l[0] != '-' && l[l.Length - 1] != '-'
            && l.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '\u200C'));
    }
}
