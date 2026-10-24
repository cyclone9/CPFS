using System;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cpfs.Core.Config;

// Writes config.json so that the 0.2.0 Uninstall script can still read it: UTF-8 with a BOM (without one, Get-Content
// in Windows PowerShell 5.1 reads a non-ASCII network name as ANSI), every field the scripts wrote kept with its type,
// unknown fields and their order preserved. Fields are added or set, never removed or retyped.
public static class ConfigWriter
{
    // The arrays the scripts read with foreach; a single value or the Count wrapper becomes a real array again.
    private static readonly string[][] ArrayPaths =
    {
        new[] { "Tracked", "Users" },
        new[] { "Tracked", "Networks" },
        new[] { "Tracked", "FirewallRules" },
        new[] { "Backup", "Registry" },
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentSize = 4,
        NewLine = "\r\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] Serialize(JsonObject root)
    {
        if (root is null)
        {
            throw new ArgumentNullException(nameof(root));
        }

        var text = root.ToJsonString(WriteOptions) + "\r\n";
        var encoding = new UTF8Encoding(true);
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);
        var bytes = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
        return bytes;
    }

    // Sets a property, reusing the key's spelling and position when it exists.
    public static void Set(JsonObject obj, string name, JsonNode? value)
    {
        if (obj is null)
        {
            throw new ArgumentNullException(nameof(obj));
        }

        var key = ConfigJson.KeyOf(obj, name);
        if (key is null)
        {
            obj.Add(name, value);
        }
        else
        {
            obj[key] = value;
        }
    }

    public static void Normalize(JsonObject root)
    {
        foreach (var path in ArrayPaths)
        {
            if (ConfigJson.GetObject(root, path[0]) is not { } parent || !ConfigJson.Has(parent, path[1]))
            {
                continue;
            }

            var node = ConfigJson.Get(parent, path[1]);
            if (ConfigJson.ShapeOf(node) == ArrayShape.Array)
            {
                continue;
            }

            var array = new JsonArray();
            foreach (var item in ConfigJson.AsArray(node))
            {
                array.Add(item?.DeepClone());
            }

            Set(parent, path[1], array);
        }
    }

    // What every app save records: the scripts' Version and Updated (yyyy-MM-ddTHH:mm:ss, local time, no offset),
    // plus an App block that tells the app's saves apart from the scripts'.
    public static void Stamp(JsonObject root, string appVersion, DateTime localTime)
    {
        if (root is null)
        {
            throw new ArgumentNullException(nameof(root));
        }

        var updated = localTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        Set(root, "Version", appVersion);
        Set(root, "Updated", updated);
        var app = ConfigJson.GetObject(root, "App");
        if (app is null)
        {
            app = new JsonObject();
            Set(root, "App", app);
        }

        Set(app, "Version", appVersion);
        Set(app, "Updated", updated);
    }
}
