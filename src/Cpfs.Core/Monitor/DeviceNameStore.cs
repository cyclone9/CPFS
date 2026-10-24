using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cpfs.Core.Config;
using Cpfs.Core.Validation;

namespace Cpfs.Core.Monitor;

// Names the owner gave connected devices, kept in %LOCALAPPDATA%\CPFS\devices.json and keyed by DeviceIdentity.Key.
// Per user and never in ProgramData: it is the app's own data, not a Windows setting.
public sealed class DeviceNameStore
{
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _names.Count;

    public string? Get(string key) => _names.TryGetValue(key, out var name) ? name : null;

    public bool Set(string key, string name)
    {
        var valid = Validators.DeviceName(name);
        if (!valid.IsValid || key.Length == 0)
        {
            return false;
        }

        _names[key] = valid.Value!;
        return true;
    }

    public bool Remove(string key) => _names.Remove(key);

    // Malformed content or invalid entries are dropped: a lost name is never worth an error in the Monitor.
    public static DeviceNameStore Parse(string? json)
    {
        var store = new DeviceNameStore();
        if (string.IsNullOrWhiteSpace(json))
        {
            return store;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json!, new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return store;
        }

        foreach (var node in ConfigJson.GetArray(root, "Devices"))
        {
            if (node is JsonObject entry
                && ConfigJson.GetString(entry, "Key") is { Length: > 0 } key
                && ConfigJson.GetString(entry, "Name") is { } name)
            {
                store.Set(key, name);
            }
        }

        return store;
    }

    public string Serialize()
    {
        var keys = new List<string>(_names.Keys);
        keys.Sort(StringComparer.Ordinal);
        var devices = new JsonArray();
        foreach (var key in keys)
        {
            devices.Add(new JsonObject { ["Key"] = key, ["Name"] = _names[key] });
        }

        return new JsonObject { ["Devices"] = devices }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
