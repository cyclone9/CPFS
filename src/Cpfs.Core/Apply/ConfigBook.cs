using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Cpfs.Core.Config;
using Cpfs.Core.Planning;

namespace Cpfs.Core.Apply;

public enum BackupWrite
{
    // The step had nothing to record.
    NotNeeded,
    Recorded,

    // The field already holds a value from an earlier run; it is never overwritten.
    AlreadyRecorded,

    // There is no Backup block to write into (an install from 0.1, or a later run with Backup null).
    NoBackup,
}

// The original Windows values the first Setup reads before it changes anything (the scripts' Backup block, plus the
// lockout duration and window and the logon audit setting the app adds).
public sealed record BulkBackup(
    string? HostName,
    IReadOnlyList<RegistryBackup> Registry,
    bool Smb2,
    bool RejectUnencrypted,
    string? ServerStart,
    LockoutInfo? Lockout,
    AuditSetting? Audit);

// config.json while Apply runs: the parsed file (or a new one on the first Setup) and the rules for changing it.
// Backup is written once, by the first Setup, and a recorded value is never overwritten; Tracked only grows.
public sealed class ConfigBook
{
    private ConfigBook(JsonObject root, bool firstSetup)
    {
        Root = root;
        FirstSetup = firstSetup;
    }

    public JsonObject Root { get; }

    // No config.json existed when Apply started: only then is the bulk backup taken.
    public bool FirstSetup { get; }

    public bool HasBackupBlock => ConfigJson.GetObject(Root, "Backup") is not null;

    // On the first Setup nothing is saved until the backup is in the file: a config.json without one would make the
    // next run think the backup had been taken.
    public bool CanSave => !FirstSetup || HasBackupBlock;

    public static ConfigBook ForSetup(ConfigDocument? existing)
    {
        if (existing is not null)
        {
            var root = (JsonObject)existing.Root.DeepClone();
            ConfigWriter.Normalize(root);
            return new ConfigBook(root, false);
        }

        // The scripts' field order; quick access stays empty until a version creates it.
        var fresh = new JsonObject
        {
            ["HostName"] = null,
            ["SmbUser"] = null,
            ["SharePath"] = null,
            ["ShareName"] = null,
            ["Strict"] = false,
            ["AutoPrivate"] = false,
            ["Access"] = 0,
            ["DriveLetter"] = null,
            ["Shortcut"] = null,
            ["DriveLabel"] = null,
            ["Product"] = Planner.ShareDescription,
            ["Version"] = null,
            ["Updated"] = null,
            ["Partial"] = true,
            ["Backup"] = null,
            ["Tracked"] = new JsonObject
            {
                ["Users"] = new JsonArray(),
                ["Networks"] = new JsonArray(),
                ["FirewallRules"] = new JsonArray(),
            },
        };
        return new ConfigBook(fresh, true);
    }

    public bool TakeBulkBackup(BulkBackup backup, DateTime localTime)
    {
        if (backup is null)
        {
            throw new ArgumentNullException(nameof(backup));
        }

        if (!FirstSetup || HasBackupBlock)
        {
            return false;
        }

        var registry = new JsonArray();
        foreach (var r in backup.Registry)
        {
            registry.Add(new JsonObject
            {
                ["Path"] = r.Path,
                ["Name"] = r.Name,
                ["Exists"] = r.Exists ?? false,
                ["Value"] = r.Value is { } v ? JsonValue.Create(v) : null,
            });
        }

        var block = new JsonObject
        {
            ["Taken"] = localTime.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            ["HostName"] = backup.HostName,
            ["Registry"] = registry,
            ["Smb2"] = backup.Smb2,
            ["RejectUnencrypted"] = backup.RejectUnencrypted,
            ["ServerStart"] = backup.ServerStart,
            ["Lockout"] = backup.Lockout is { } l ? JsonValue.Create((long)l.Threshold) : null,
        };
        if (backup.Lockout is { } lockout)
        {
            block["LockoutDuration"] = (long)lockout.DurationSeconds;
            block["LockoutWindow"] = (long)lockout.WindowSeconds;
        }

        if (backup.Audit is { } audit)
        {
            block["AuditLogonSuccess"] = audit.Success;
            block["AuditLogonFailure"] = audit.Failure;
        }

        ConfigWriter.Set(Root, "Backup", block);
        if (backup.HostName is not null && ConfigJson.GetString(Root, "HostName") is null)
        {
            ConfigWriter.Set(Root, "HostName", backup.HostName);
        }

        ConfigWriter.Set(Root, "Partial", true);
        return true;
    }

    // One field of Backup, written only when it is absent or null.
    public BackupWrite Backup(string name, JsonNode? value)
    {
        if (ConfigJson.GetObject(Root, "Backup") is not { } block)
        {
            return BackupWrite.NoBackup;
        }

        if (ConfigJson.Get(block, name) is not null)
        {
            return BackupWrite.AlreadyRecorded;
        }

        ConfigWriter.Set(block, name, value);
        return BackupWrite.Recorded;
    }

    // The drive name a letter had before CPFS named it (Backup.DriveLabels[]), recorded once per letter.
    public BackupWrite BackupDriveLabel(char letter, string label)
    {
        if (ConfigJson.GetObject(Root, "Backup") is not { } block)
        {
            return BackupWrite.NoBackup;
        }

        var key = char.ToUpperInvariant(letter).ToString();
        var list = new JsonArray();
        foreach (var node in ConfigJson.GetArray(block, "DriveLabels"))
        {
            if (node is JsonObject o && string.Equals(ConfigJson.GetString(o, "Letter"), key, StringComparison.OrdinalIgnoreCase))
            {
                return BackupWrite.AlreadyRecorded;
            }

            list.Add(node?.DeepClone());
        }

        list.Add(new JsonObject { ["Letter"] = key, ["Label"] = label });
        ConfigWriter.Set(block, "DriveLabels", list);
        return BackupWrite.Recorded;
    }

    public bool IsTrackedUser(string name)
    {
        foreach (var node in ConfigJson.GetArray(Tracked(), "Users"))
        {
            var n = node is JsonObject o ? ConfigJson.GetString(o, "Name") : ConfigJson.AsString(node);
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // The first time an account is used: whether it existed, whether it was an administrator, and for an account
    // CPFS did not create, what it looked like (restored by Uninstall when the account is kept).
    public bool TrackUser(string name, bool existed, bool wasAdmin, UserBefore? before)
    {
        if (IsTrackedUser(name))
        {
            return false;
        }

        var entry = new JsonObject { ["Name"] = name, ["Existed"] = existed, ["WasAdmin"] = wasAdmin };
        if (before is not null)
        {
            var groups = new JsonArray();
            foreach (var g in before.Groups)
            {
                groups.Add(g);
            }

            entry["Before"] = new JsonObject
            {
                ["Enabled"] = before.Enabled,
                ["PasswordNeverExpires"] = before.PasswordNeverExpires,
                ["Groups"] = groups,
                ["HiddenValue"] = before.HiddenValue,
            };
        }

        List("Users").Add(entry);
        return true;
    }

    public bool TrackNetwork(string name, string? id)
    {
        var added = AddString("Networks", name);
        var profiles = List("NetworkProfiles");
        foreach (var node in profiles)
        {
            if (node is JsonObject o && string.Equals(ConfigJson.GetString(o, "Name"), name, StringComparison.Ordinal)
                && string.Equals(ConfigJson.GetString(o, "Id"), id, StringComparison.OrdinalIgnoreCase))
            {
                return added;
            }
        }

        profiles.Add(new JsonObject { ["Id"] = id, ["Name"] = name });
        return true;
    }

    public bool TrackFirewallRule(string ruleId) => AddString("FirewallRules", ruleId);

    public void TrackReplacedShare(string name, string? path, string? description, string? securityDescriptor) =>
        List("ReplacedShares").Add(new JsonObject
        {
            ["Name"] = name, ["Path"] = path, ["Description"] = description, ["SecurityDescriptor"] = securityDescriptor,
        });

    // Objects Setup created whose identity the scripts' fields do not hold (Tracked.Created.<name>).
    public void TrackCreated(string name, JsonNode? value)
    {
        var tracked = Tracked();
        if (ConfigJson.GetObject(tracked, "Created") is not { } created)
        {
            created = new JsonObject();
            ConfigWriter.Set(tracked, "Created", created);
        }

        ConfigWriter.Set(created, name, value);
    }

    public void SetField(string name, JsonNode? value) => ConfigWriter.Set(Root, name, value);

    // The scripts drop Partial at the end of a run that went through.
    public void MarkFinished()
    {
        if (ConfigJson.KeyOf(Root, "Partial") is { } key)
        {
            Root.Remove(key);
        }
    }

    public byte[] Serialize(string appVersion, DateTime localTime)
    {
        ConfigWriter.Normalize(Root);
        ConfigWriter.Set(Root, "Product", Planner.ShareDescription);
        ConfigWriter.Stamp(Root, appVersion, localTime);
        return ConfigWriter.Serialize(Root);
    }

    private JsonObject Tracked()
    {
        if (ConfigJson.GetObject(Root, "Tracked") is { } tracked)
        {
            return tracked;
        }

        tracked = new JsonObject { ["Users"] = new JsonArray(), ["Networks"] = new JsonArray(), ["FirewallRules"] = new JsonArray() };
        ConfigWriter.Set(Root, "Tracked", tracked);
        return tracked;
    }

    private JsonArray List(string name)
    {
        var tracked = Tracked();
        if (ConfigJson.Get(tracked, name) is JsonArray array)
        {
            return array;
        }

        array = new JsonArray();
        foreach (var item in ConfigJson.AsArray(ConfigJson.Get(tracked, name)))
        {
            array.Add(item?.DeepClone());
        }

        ConfigWriter.Set(tracked, name, array);
        return array;
    }

    private bool AddString(string list, string value)
    {
        var array = List(list);
        foreach (var node in array)
        {
            if (string.Equals(ConfigJson.AsString(node), value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        array.Add(value);
        return true;
    }
}
