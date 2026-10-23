using System.Globalization;

namespace Cpfs.Core.Text;

// Keeps a value in its own direction inside translated prose. LRE/RLE ... PDF rather than the newer isolates
// (U+2066-2069), because WPF's bidi implementation predates the isolates.
public static class Bidi
{
    public const char Lre = '‪';
    public const char Rle = '‫';
    public const char Pdf = '‬';

    // In right-to-left prose a value whose first strong character is left-to-right (a path, an address, a share name,
    // "Türkçe") is embedded left-to-right; in left-to-right prose a right-to-left value (a Persian network name) is
    // embedded right-to-left. Anything else is returned unchanged.
    public static string Embed(string? value, bool uiIsRtl)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? "";
        }

        var direction = FirstStrong(value!);
        if (uiIsRtl && direction != StrongDirection.RightToLeft)
        {
            return Lre + value + Pdf;
        }

        if (!uiIsRtl && direction == StrongDirection.RightToLeft)
        {
            return Rle + value + Pdf;
        }

        return value!;
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
            if (c is not (Lre or Rle or Pdf or '‎' or '‏'))
            {
                chars.Append(c);
            }
        }

        return chars.ToString();
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
