using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cpfs.Core.Text;

namespace Cpfs.Core.Config;

// Its message is the English text; the screen shows Text in the app's language.
public sealed class ConfigFormatException : Exception, IHasText
{
    public ConfigFormatException(Exception? inner, string key, params object?[] args)
        : base(new TextKey(key, args).ToEnglish(), inner)
    {
        Text = new TextKey(key, args);
    }

    public TextKey? Text { get; }
}

// Reads C:\ProgramData\CPFS\config.json as the scripts wrote it (ConvertTo-Json | Set-Content -Encoding UTF8 on
// Windows PowerShell 5.1: UTF-8 with a BOM) and as the app writes it.
public static class ConfigReader
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    // Case-insensitive like ConvertFrom-Json; ConfigJson.Get still does every lookup.
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    // fallback decodes a BOM-less file that is not valid UTF-8 (the app passes the ANSI code page, which is what
    // Get-Content in Windows PowerShell 5.1 would use for it).
    public static ConfigDocument Parse(byte[] bytes, Encoding? fallback = null)
    {
        if (bytes is null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        string text;
        TextEncoding encoding;
        if (StartsWith(bytes, 0xEF, 0xBB, 0xBF))
        {
            text = Decode(new UTF8Encoding(false, true), bytes, 3);
            encoding = TextEncoding.Utf8Bom;
        }
        else if (StartsWith(bytes, 0xFF, 0xFE))
        {
            text = Decode(new UnicodeEncoding(false, false, true), bytes, 2);
            encoding = TextEncoding.Utf16LeBom;
        }
        else if (StartsWith(bytes, 0xFE, 0xFF))
        {
            text = Decode(new UnicodeEncoding(true, false, true), bytes, 2);
            encoding = TextEncoding.Utf16BeBom;
        }
        else
        {
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
                encoding = TextEncoding.Utf8;
            }
            catch (DecoderFallbackException) when (fallback is not null)
            {
                text = fallback.GetString(bytes);
                encoding = TextEncoding.Fallback;
            }
        }

        return Parse(text, encoding);
    }

    public static ConfigDocument Parse(string text, TextEncoding encoding = TextEncoding.Utf8)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, NodeOptions, DocumentOptions);

            // JsonNode builds its objects lazily; walking them now makes duplicate keys fail here, not later.
            Materialize(node);
        }
        catch (JsonException ex)
        {
            throw new ConfigFormatException(ex, "Err_ConfigNotJson", ex.Message);
        }
        catch (ArgumentException ex)
        {
            // Duplicate property names (also when they differ only in case, as ConvertFrom-Json refuses them).
            throw new ConfigFormatException(ex, "Err_ConfigDuplicate", ex.Message);
        }

        if (node is not JsonObject root)
        {
            throw new ConfigFormatException(null, "Err_ConfigNotObject");
        }

        return new ConfigDocument(root, encoding);
    }

    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    Materialize(pair.Value);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Materialize(item);
                }

                break;
        }
    }

    private static string Decode(Encoding encoding, byte[] bytes, int skip)
    {
        try
        {
            return encoding.GetString(bytes, skip, bytes.Length - skip);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ConfigFormatException(ex, "Err_ConfigBadBom");
        }
    }

    private static bool StartsWith(byte[] bytes, params byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }
}

public enum TextEncoding
{
    Utf8,
    Utf8Bom,
    Utf16LeBom,
    Utf16BeBom,
    Fallback,
}
