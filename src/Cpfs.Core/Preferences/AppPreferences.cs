using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cpfs.Core.Preferences;

// The user's own UI choices, kept in %LOCALAPPDATA%\CPFS\preferences.json. Never in ProgramData: they are per user
// and must not need administrator rights.
public sealed class AppPreferences
{
    public const string DefaultTheme = "System";

    public string Theme { get; set; } = DefaultTheme;

    // Null: follow the Windows display language when it is one of the app's languages.
    public string? Language { get; set; }

    // Per two-letter language name; a language without an entry uses its default font.
    public Dictionary<string, string> Fonts { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Asks GitHub for a newer release once when the app starts. Off unless the user switches it on.
    public bool CheckUpdatesAtStart { get; set; }

    // Unknown or malformed content gives the defaults rather than an error: preferences are never worth a failed start.
    // Fields a newer or older version wrote (0.6.0 dev4's AlwaysShowDetails) are ignored.
    public static AppPreferences Parse(string? json)
    {
        var prefs = new AppPreferences();
        if (string.IsNullOrWhiteSpace(json))
        {
            return prefs;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json!, new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return prefs;
        }

        if (root is null)
        {
            return prefs;
        }

        if (Text(Config.ConfigJson.Get(root, "Theme")) is { Length: > 0 } theme)
        {
            prefs.Theme = theme;
        }

        if (Text(Config.ConfigJson.Get(root, "Language")) is { Length: > 0 } language)
        {
            prefs.Language = language;
        }

        if (Config.ConfigJson.Get(root, "CheckUpdatesAtStart") is JsonValue check && check.GetValueKind() == JsonValueKind.True)
        {
            prefs.CheckUpdatesAtStart = true;
        }

        if (Config.ConfigJson.GetObject(root, "Fonts") is { } fonts)
        {
            foreach (var pair in fonts)
            {
                if (Text(pair.Value) is { Length: > 0 } font)
                {
                    prefs.Fonts[pair.Key] = font;
                }
            }
        }

        return prefs;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public string Serialize()
    {
        var fonts = new JsonObject();
        foreach (var pair in Fonts)
        {
            fonts[pair.Key] = pair.Value;
        }

        var root = new JsonObject
        {
            [nameof(Theme)] = Theme,
            [nameof(Language)] = Language,
            [nameof(Fonts)] = fonts,
            [nameof(CheckUpdatesAtStart)] = CheckUpdatesAtStart,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
