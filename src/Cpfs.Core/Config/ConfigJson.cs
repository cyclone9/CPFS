using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cpfs.Core.Config;

// Tolerant access to a config written by Windows PowerShell 5.1: property names are matched case-insensitively
// (as ConvertFrom-Json does), and an array may arrive as a single value, null, or the {"value":[...],"Count":n} wrapper.
public static class ConfigJson
{
    public static JsonNode? Get(JsonObject? obj, string name)
    {
        if (obj is null)
        {
            return null;
        }

        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    public static bool Has(JsonObject? obj, string name)
    {
        if (obj is null)
        {
            return false;
        }

        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // The key as spelled in the file, so a write updates it in place instead of adding a second spelling.
    public static string? KeyOf(JsonObject obj, string name)
    {
        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }

        return null;
    }

    public static JsonObject? GetObject(JsonObject? obj, string name) => Get(obj, name) as JsonObject;

    public static string? GetString(JsonObject? obj, string name) => AsString(Get(obj, name));

    public static string? AsString(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => null,
        };
    }

    public static bool? GetBool(JsonObject? obj, string name) => AsBool(Get(obj, name));

    public static bool? AsBool(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        switch (value.GetValueKind())
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                var s = value.GetValue<string>();
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return null;
            default:
                return null;
        }
    }

    public static long? GetInt(JsonObject? obj, string name) => AsInt(Get(obj, name));

    public static long? AsInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        switch (value.GetValueKind())
        {
            case JsonValueKind.Number:
                if (value.TryGetValue<long>(out var l))
                {
                    return l;
                }

                return value.TryGetValue<double>(out var d) && d >= long.MinValue && d <= long.MaxValue && Math.Floor(d) == d ? (long)d : null;
            case JsonValueKind.String:
                return long.TryParse(value.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            default:
                return null;
        }
    }

    public static IReadOnlyList<JsonNode?> GetArray(JsonObject? obj, string name) => AsArray(Get(obj, name));

    public static IReadOnlyList<JsonNode?> AsArray(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return Array.Empty<JsonNode?>();
            case JsonArray array:
                return new List<JsonNode?>(array);
            case JsonObject wrapper when IsCountWrapper(wrapper):
                return AsArray(Get(wrapper, "value"));
            default:
                return new[] { node };
        }
    }

    public static ArrayShape ShapeOf(JsonNode? node) => node switch
    {
        null => ArrayShape.Null,
        JsonArray => ArrayShape.Array,
        JsonObject o when IsCountWrapper(o) => ArrayShape.CountWrapper,
        _ => ArrayShape.Scalar,
    };

    // Windows PowerShell 5.1 can serialize an array held in a PSObject as {"value": [...], "Count": n}.
    public static bool IsCountWrapper(JsonObject obj) =>
        obj.Count == 2 && Get(obj, "value") is JsonArray && Get(obj, "Count") is JsonValue;

    // Every leaf as "Path.To[0].Field" = text, in file order, for showing a config as found.
    public static IReadOnlyList<KeyValuePair<string, string>> Flatten(JsonNode? root)
    {
        var list = new List<KeyValuePair<string, string>>();
        Walk(root, "", list);
        return list;
    }

    private static void Walk(JsonNode? node, string path, List<KeyValuePair<string, string>> list)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.Count == 0)
                {
                    list.Add(new KeyValuePair<string, string>(path, "{}"));
                }

                foreach (var pair in obj)
                {
                    Walk(pair.Value, path.Length == 0 ? pair.Key : path + "." + pair.Key, list);
                }

                break;
            case JsonArray array:
                if (array.Count == 0)
                {
                    list.Add(new KeyValuePair<string, string>(path, "[]"));
                }

                for (var i = 0; i < array.Count; i++)
                {
                    Walk(array[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", list);
                }

                break;
            case null:
                list.Add(new KeyValuePair<string, string>(path, "null"));
                break;
            default:
                list.Add(new KeyValuePair<string, string>(path, AsString(node) ?? node.ToJsonString()));
                break;
        }
    }
}

public enum ArrayShape
{
    Null,
    Array,
    Scalar,
    CountWrapper,
}
