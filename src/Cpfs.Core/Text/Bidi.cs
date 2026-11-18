using System.Globalization;

namespace Cpfs.Core.Text;

// Keeps a value in its own direction inside translated prose. Embeddings (LRE/RLE ... PDF) rather than the newer
// isolates (U+2066-2069), because WPF's bidi implementation predates the isolates; and where it can, LRM fencing
// rather than an embedding, because WPF did not hold "CPFS 1.0.0-dev7" together inside an LRE ... PDF (dev8).
public static class Bidi
{
    public const char Lre = '\u202A';
    public const char Rle = '\u202B';
    public const char Pdf = '\u202C';
    public const char Lrm = '\u200E';
    public const char Rlm = '\u200F';

    // In right-to-left prose a value whose first strong character is left-to-right or that has none (a path, an
    // address, a date, a version, "Türkçe") is fenced as KeepLeftToRight does. A value that also holds right-to-left
    // letters (D:\پوشه) keeps the LRE ... PDF embedding: marks alone can't lift those letters above the paragraph's
    // level, so fencing would break the value's order wherever the embedding is honoured. In left-to-right prose a
    // right-to-left value (a Persian network name) is embedded right-to-left. Anything else is returned unchanged.
    public static string Embed(string? value, bool uiIsRtl)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? "";
        }

        var direction = FirstStrong(value!);
        if (uiIsRtl && direction != StrongDirection.RightToLeft)
        {
            return HasStrongRtl(value!) ? Lre + value + Pdf : Fence(value!);
        }

        if (!uiIsRtl && direction == StrongDirection.RightToLeft)
        {
            return Rle + value + Pdf;
        }

        return value!;
    }

    // A Latin value whose digits and punctuation must not be reordered in a right-to-left line (the window title,
    // "CPFS 1.0.0-dev7"). In WPF an LRE ... PDF embedding did not hold such a value together there: the Persian title
    // bar showed "dev7-1.0.0 CPFS". An LRM on each side of every character that is not a letter puts each of them
    // between strong left-to-right characters, so the value reads left to right whatever class the digits get.
    public static string KeepLeftToRight(string? value, bool uiIsRtl)
    {
        if (!uiIsRtl || string.IsNullOrEmpty(value))
        {
            return value ?? "";
        }

        return Fence(value!);
    }

    private static string Fence(string value)
    {
        var chars = new System.Text.StringBuilder(value.Length * 3 + 2);
        chars.Append(Lrm);
        var previous = '\0';
        foreach (var c in value)
        {
            if (char.IsLetter(c) && !IsStrongRtl(c))
            {
                chars.Append(c);
                previous = c;
                continue;
            }

            // A Windows error text can carry CRLF: a mark between CR and LF would make two line breaks of one.
            if (c == '\n' && previous == '\r')
            {
                chars.Length--;
            }

            previous = c;
            if (chars[chars.Length - 1] != Lrm && chars[chars.Length - 1] != '\r')
            {
                chars.Append(Lrm);
            }

            chars.Append(c).Append(Lrm);
        }

        if (chars[chars.Length - 1] != Lrm)
        {
            chars.Append(Lrm);
        }

        return chars.ToString();
    }

    // Removes the embedding marks again, for a value that is copied to the clipboard.
    public static string Strip(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? "";
        }

        var chars = new System.Text.StringBuilder(value!.Length);
        foreach (var c in value)
        {
            if (c is not (Lre or Rle or Pdf or Lrm or Rlm))
            {
                chars.Append(c);
            }
        }

        return chars.ToString();
    }

    private static bool HasStrongRtl(string value)
    {
        foreach (var c in value)
        {
            if (IsStrongRtl(c))
            {
                return true;
            }
        }

        return false;
    }

    public static StrongDirection FirstStrong(string value)
    {
        foreach (var c in value)
        {
            if (IsStrongRtl(c))
            {
                return StrongDirection.RightToLeft;
            }

            if (char.IsLetter(c))
            {
                return StrongDirection.LeftToRight;
            }
        }

        return StrongDirection.None;
    }

    // Hebrew, Arabic, Syriac, Thaana, NKo and the Arabic presentation forms: the scripts the app's languages use
    // plus their neighbours. Digits are weak and never decide.
    private static bool IsStrongRtl(char c)
    {
        if (c is >= '֐' and <= 'ࣿ' || c is >= 'יִ' and <= '﷿' || c is >= 'ﹰ' and <= '﻿')
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            return category is UnicodeCategory.OtherLetter or UnicodeCategory.ModifierLetter
                or UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter;
        }

        return c is '‏';
    }
}

public enum StrongDirection
{
    None,
    LeftToRight,
    RightToLeft,
}
