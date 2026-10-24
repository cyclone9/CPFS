using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Cpfs.Core.Config;

// What kind of install wrote the file, decided by its shape, never by "Version" (the scripts wrote 0.2.0, 0.2.1 and
// 0.2.3 for the same format, and 0.1's string is unknown).
public enum ConfigShape
{
    // No "Tracked": written by 0.1.
    V01,

    // "Tracked" present: written by 0.2.x or the app.
    V02,
}

public enum BackupState
{
    Absent,
    Null,
    Present,
}

public sealed class ConfigDocument
{
    internal ConfigDocument(JsonObject root, TextEncoding encoding)
    {
        Root = root;
        Encoding = encoding;
    }

    public JsonObject Root { get; }

    public TextEncoding Encoding { get; }

    public ConfigShape Shape => ConfigJson.Has(Root, "Tracked") ? ConfigShape.V02 : ConfigShape.V01;

    public BackupState Backup => ConfigJson.Get(Root, "Backup") switch
    {
        JsonObject => BackupState.Present,
        null when ConfigJson.Has(Root, "Backup") => BackupState.Null,
        null => BackupState.Absent,
        _ => BackupState.Null,
    };

    // The first Setup run saved its backup and stopped before the final save.
    public bool Partial => ConfigJson.GetBool(Root, "Partial") == true;

    public string? Version => ConfigJson.GetString(Root, "Version");

    public string? HostName => ConfigJson.GetString(Root, "HostName");

    public string? SmbUser => ConfigJson.GetString(Root, "SmbUser");

    public string? SharePath => ConfigJson.GetString(Root, "SharePath");

    public string? ShareName => ConfigJson.GetString(Root, "ShareName");

    public bool? Strict => ConfigJson.GetBool(Root, "Strict");

    public bool? AutoPrivate => ConfigJson.GetBool(Root, "AutoPrivate");

    // Bit 1 drive, bit 2 shortcut.
    public long? Access => ConfigJson.GetInt(Root, "Access");

    public string? DriveLetter => ConfigJson.GetString(Root, "DriveLetter");

    public string? Shortcut => ConfigJson.GetString(Root, "Shortcut");

    // The drive's name in Explorer, written by the 0.2.3 scripts.
    public string? DriveLabel => ConfigJson.GetString(Root, "DriveLabel");

    // The folder CPFS's drive letter points to (Tracked.Created.DrivePath, added by the app). Installs the scripts made
    // have none: their drive points to SharePath.
    public string? TrackedDrivePath => ConfigJson.GetString(ConfigJson.GetObject(ConfigJson.GetObject(Root, "Tracked"), "Created"), "DrivePath");

    // The CPFS icon file in the data folder, used by the drive and the shortcut (Tracked.Created.IconFile, added by the app).
    public string? TrackedIconFile => ConfigJson.GetString(ConfigJson.GetObject(ConfigJson.GetObject(Root, "Tracked"), "Created"), "IconFile");

    // Whether Setup adds the File Explorer navigation-pane entry (added by the app); null in older files.
    public bool? ExplorerEntry => ConfigJson.GetBool(Root, "ExplorerEntry");

    // The navigation-pane entry CPFS made (Tracked.Created.ExplorerEntry, added by the app): whose hive, which class
    // key, the folder it opens and the name it shows. Null when CPFS made none.
    public TrackedExplorerEntry? TrackedExplorerEntry =>
        ConfigJson.GetObject(ConfigJson.GetObject(ConfigJson.GetObject(Root, "Tracked"), "Created"), "ExplorerEntry") is { } e
            ? new TrackedExplorerEntry(ConfigJson.GetString(e, "Sid"), ConfigJson.GetString(e, "Clsid"), ConfigJson.GetString(e, "Folder"), ConfigJson.GetString(e, "Name"))
            : null;

    // The path the recorded drive letter maps to: what CPFS created, never anything else.
    public string? OwnDrivePath => (TrackedDrivePath ?? SharePath)?.Trim().TrimEnd('\\');

    // The drive name CPFS gave its letter; the scripts before 0.2.3 recorded none and always wrote "CPFS - <share>".
    public string? OwnDriveLabel => DriveLabel is { Length: > 0 } label ? label : ShareName is { Length: > 0 } share ? "CPFS - " + share : null;

    // A drive name that was there before CPFS set its own (Backup.DriveLabels, added by the app), by letter.
    public string? BackupDriveLabel(char letter) => BackupByLetter("DriveLabels", "Label", letter);

    // A drive icon that was there before CPFS set its own (Backup.DriveIcons, added by the app), by letter.
    public string? BackupDriveIcon(char letter) => BackupByLetter("DriveIcons", "Icon", letter);

    private string? BackupByLetter(string list, string field, char letter)
    {
        foreach (var node in ConfigJson.GetArray(ConfigJson.GetObject(Root, "Backup"), list))
        {
            if (node is JsonObject entry && ConfigJson.GetString(entry, "Letter") is { Length: 1 } l
                && char.ToUpperInvariant(l[0]) == char.ToUpperInvariant(letter))
            {
                return ConfigJson.GetString(entry, field);
            }
        }

        return null;
    }

    public string? Updated => ConfigJson.GetString(Root, "Updated");

    public string? BackupTaken => ConfigJson.GetString(ConfigJson.GetObject(Root, "Backup"), "Taken");

    // Null both when the scripts could not read the threshold and when there is no backup.
    public long? BackupLockout => ConfigJson.GetInt(ConfigJson.GetObject(Root, "Backup"), "Lockout");

    public string? BackupHostName => ConfigJson.GetString(ConfigJson.GetObject(Root, "Backup"), "HostName");

    public bool? BackupSmb2 => ConfigJson.GetBool(ConfigJson.GetObject(Root, "Backup"), "Smb2");

    public bool? BackupRejectUnencrypted => ConfigJson.GetBool(ConfigJson.GetObject(Root, "Backup"), "RejectUnencrypted");

    // A ServiceStartMode name as PowerShell wrote it: Automatic, Manual, Disabled (Boot and System are never restored).
    public string? BackupServerStart => ConfigJson.GetString(ConfigJson.GetObject(Root, "Backup"), "ServerStart");

    // Added by the app; absent in the scripts' backups.
    public long? BackupLockoutDuration => ConfigJson.GetInt(ConfigJson.GetObject(Root, "Backup"), "LockoutDuration");

    public long? BackupLockoutWindow => ConfigJson.GetInt(ConfigJson.GetObject(Root, "Backup"), "LockoutWindow");

    // Shares a Setup removed when the user ticked it, recorded before the removal.
    public IReadOnlyList<TrackedShare> TrackedReplacedShares
    {
        get
        {
            var list = new List<TrackedShare>();
            foreach (var node in ConfigJson.GetArray(ConfigJson.GetObject(Root, "Tracked"), "ReplacedShares"))
            {
                if (node is JsonObject share && ConfigJson.GetString(share, "Name") is { } name)
                {
                    list.Add(new TrackedShare(name, ConfigJson.GetString(share, "Path")));
                }
            }

            return list;
        }
    }

    // Networks the app switched to Private, with the profile's id; Tracked.Networks keeps the names for 0.2.0.
    public IReadOnlyList<TrackedNetwork> TrackedNetworkProfiles
    {
        get
        {
            var list = new List<TrackedNetwork>();
            foreach (var node in ConfigJson.GetArray(ConfigJson.GetObject(Root, "Tracked"), "NetworkProfiles"))
            {
                if (node is JsonObject n && ConfigJson.GetString(n, "Name") is { } name)
                {
                    list.Add(new TrackedNetwork(ConfigJson.GetString(n, "Id"), name));
                }
            }

            return list;
        }
    }

    // What an account looked like before Setup first took it over; null when nothing was recorded.
    public UserBefore? TrackedUserBefore(string name)
    {
        foreach (var node in ConfigJson.GetArray(ConfigJson.GetObject(Root, "Tracked"), "Users"))
        {
            if (node is JsonObject user && string.Equals(ConfigJson.GetString(user, "Name"), name, StringComparison.OrdinalIgnoreCase)
                && ConfigJson.GetObject(user, "Before") is { } before)
            {
                var groups = new List<string>();
                foreach (var g in ConfigJson.GetArray(before, "Groups"))
                {
                    if (ConfigJson.AsString(g) is { Length: > 0 } sid)
                    {
                        groups.Add(sid);
                    }
                }

                var hidden = ConfigJson.GetInt(before, "HiddenValue");
                return new UserBefore(ConfigJson.GetBool(before, "Enabled"), ConfigJson.GetBool(before, "PasswordNeverExpires"), groups,
                    hidden is { } h ? (int)h : null);
            }
        }

        return null;
    }

    public IReadOnlyList<TrackedUser> TrackedUsers
    {
        get
        {
            var list = new List<TrackedUser>();
            foreach (var node in ConfigJson.GetArray(ConfigJson.GetObject(Root, "Tracked"), "Users"))
            {
                if (node is JsonObject user)
                {
                    list.Add(new TrackedUser(
                        ConfigJson.GetString(user, "Name"),
                        ConfigJson.GetBool(user, "Existed"),
                        ConfigJson.GetBool(user, "WasAdmin")));
                }
                else if (ConfigJson.AsString(node) is { } name)
                {
                    list.Add(new TrackedUser(name, null, null));
                }
            }

            return list;
        }
    }

    public IReadOnlyList<string> TrackedNetworks => Strings(ConfigJson.GetObject(Root, "Tracked"), "Networks");

    // Built-in rules (by their non-localized rule name) that Setup disabled; CPFS's own rules are found by name prefix.
    public IReadOnlyList<string> TrackedFirewallRules => Strings(ConfigJson.GetObject(Root, "Tracked"), "FirewallRules");

    public IReadOnlyList<RegistryBackup> BackupRegistry
    {
        get
        {
            var list = new List<RegistryBackup>();
            foreach (var node in ConfigJson.GetArray(ConfigJson.GetObject(Root, "Backup"), "Registry"))
            {
                if (node is not JsonObject entry)
                {
                    continue;
                }

                list.Add(new RegistryBackup(
                    ConfigJson.GetString(entry, "Path"),
                    ConfigJson.GetString(entry, "Name"),
                    ConfigJson.GetBool(entry, "Exists"),
                    ConfigJson.GetInt(entry, "Value")));
            }

            return list;
        }
    }

    private static List<string> Strings(JsonObject? obj, string name)
    {
        var list = new List<string>();
        foreach (var node in ConfigJson.GetArray(obj, name))
        {
            if (ConfigJson.AsString(node) is { Length: > 0 } s)
            {
                list.Add(s);
            }
        }

        return list;
    }
}

public sealed record TrackedUser(string? Name, bool? Existed, bool? WasAdmin);

public sealed record TrackedShare(string Name, string? Path);

public sealed record TrackedExplorerEntry(string? Sid, string? Clsid, string? Folder, string? Name);

public sealed record TrackedNetwork(string? Id, string Name);

// Groups holds the SIDs of Administrators and Users when the account was a member.
public sealed record UserBefore(bool? Enabled, bool? PasswordNeverExpires, IReadOnlyList<string> Groups, int? HiddenValue);

public sealed record RegistryBackup(string? Path, string? Name, bool? Exists, long? Value)
{
    public RegistryLocation? Location => Path is null ? null : RegistryLocation.FromProviderPath(Path);
}

// Backup.Registry[].Path is a PowerShell provider path ("HKLM:\SYSTEM\..."). The app opens it in the 64-bit view.
public sealed record RegistryLocation(RegistryRoot Root, string SubKey)
{
    public static RegistryLocation? FromProviderPath(string path)
    {
        if (path is null)
        {
            return null;
        }

        var trimmed = path.Trim();
        var known = new[]
        {
            ("HKLM:", RegistryRoot.LocalMachine),
            ("HKEY_LOCAL_MACHINE", RegistryRoot.LocalMachine),
            ("HKCU:", RegistryRoot.CurrentUser),
            ("HKEY_CURRENT_USER", RegistryRoot.CurrentUser),
            ("Microsoft.PowerShell.Core\\Registry::HKEY_LOCAL_MACHINE", RegistryRoot.LocalMachine),
        };

        foreach (var (prefix, root) in known)
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = trimmed.Substring(prefix.Length);
            if (rest.Length > 0 && rest[0] != '\\')
            {
                continue;
            }

            rest = rest.Trim('\\');
            return rest.Length == 0 ? null : new RegistryLocation(root, rest);
        }

        return null;
    }
}

public enum RegistryRoot
{
    LocalMachine,
    CurrentUser,
}
