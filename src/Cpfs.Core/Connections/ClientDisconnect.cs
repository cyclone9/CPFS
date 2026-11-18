using System;
using System.Collections.Generic;
using System.Linq;

namespace Cpfs.Core.Connections;

// What WNetCancelConnection2W is called with: a drive by its device name, a deviceless connection by its remote name.
public sealed record DisconnectTarget(string Name, bool UpdateProfile);

public enum DisconnectVerdict
{
    // The row is gone from the list read again afterwards.
    Done,

    // Windows refused because files or folders on it are open: only a second, explicit confirmation may force it.
    OpenFiles,

    // Windows took the call but the row is still listed.
    StillListed,

    Failed,
}

// The Disconnect decisions of the Connections page, without Windows calls. Success is never taken from the return code
// alone: the list is read again and the row must be gone.
public static class ClientDisconnect
{
    // System Error Codes (Learn): ERROR_OPEN_FILES 2401, ERROR_DEVICE_IN_USE 2404, ERROR_NOT_CONNECTED 2250,
    // ERROR_SESSION_CREDENTIAL_CONFLICT 1219.
    public const int ErrorOpenFiles = 2401;
    public const int ErrorDeviceInUse = 2404;
    public const int ErrorNotConnected = 2250;
    public const int ErrorSessionCredentialConflict = 1219;

    // A drive is cancelled with the profile flag, so it is also forgotten (as Explorer's Disconnect and
    // "net use /delete" do; owner decision). The flag has no effect on a remote name (Learn), so it is not passed there.
    public static DisconnectTarget TargetFor(ClientConnection row)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        return row.DriveLetter is { Length: > 0 } letter
            ? new DisconnectTarget(letter, true)
            : new DisconnectTarget(@"\\" + row.Server + @"\" + row.Share, false);
    }

    public static bool IsOpenFiles(int code) => code is ErrorOpenFiles or ErrorDeviceInUse;

    public static DisconnectVerdict Judge(int code, bool goneAfter)
    {
        if (goneAfter)
        {
            return DisconnectVerdict.Done;
        }

        if (IsOpenFiles(code))
        {
            return DisconnectVerdict.OpenFiles;
        }

        return code == 0 ? DisconnectVerdict.StillListed : DisconnectVerdict.Failed;
    }

    // Whether the row is still in a list read again: a drive by its letter and remote, a deviceless connection by its
    // server and share. Case doesn't matter.
    public static bool IsGone(ClientConnection row, IEnumerable<ClientConnectionGroup> after)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        foreach (var c in (after ?? Array.Empty<ClientConnectionGroup>()).SelectMany(g => g.Connections))
        {
            if (string.Equals(c.Server, row.Server, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Share, row.Share, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.DriveLetter ?? "", row.DriveLetter ?? "", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // "Disconnect all connections to this server": every row once, drives first, the sign-in connection (IPC$) last,
    // so it isn't cancelled while a share on it is still open. Never forced.
    public static IReadOnlyList<(ClientConnection Row, DisconnectTarget Target)> AllFor(ClientConnectionGroup group)
    {
        if (group is null)
        {
            throw new ArgumentNullException(nameof(group));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(ClientConnection, DisconnectTarget)>();
        foreach (var row in group.Connections.OrderBy(r => r.IsSignIn).ThenBy(r => r.DriveLetter is null))
        {
            var target = TargetFor(row);
            if (seen.Add(target.Name))
            {
                list.Add((row, target));
            }
        }

        return list;
    }

    // The count line under the list: the resource key and its numbers.
    public static (string Key, int Connections, int Servers) CountLine(IReadOnlyList<ClientConnectionGroup> groups)
    {
        var servers = groups?.Count ?? 0;
        var connections = groups?.Sum(g => g.Connections.Count) ?? 0;
        var key = connections == 1 ? "Connections_Count_One"
            : servers == 1 ? "Connections_Count_OneServer"
            : "Connections_Count_Many";
        return (key, connections, servers);
    }
}
