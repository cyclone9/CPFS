using System.Collections.Generic;

namespace Cpfs.Core.Connect;

// The share address in pieces that each end after a backslash, so the page can wrap it only there and never inside
// a host name such as "My-Laptop.local": "\\host\share" gives "\\", "host\", "share".
public static class UncPieces
{
    public static IReadOnlyList<string> Split(string? unc)
    {
        var pieces = new List<string>();
        if (string.IsNullOrEmpty(unc))
        {
            return pieces;
        }

        var start = 0;
        if (unc!.StartsWith(@"\\", System.StringComparison.Ordinal))
        {
            pieces.Add(@"\\");
            start = 2;
        }

        for (var i = start; i < unc.Length; i++)
        {
            if (unc[i] == '\\')
            {
                pieces.Add(unc.Substring(start, i + 1 - start));
                start = i + 1;
            }
        }

        if (start < unc.Length)
        {
            pieces.Add(unc.Substring(start));
        }

        return pieces;
    }

    // A value shown in a box (an smb:// address, a UNC path, a host) in pieces that each end after a run of "/", "@" or
    // "\": "smb://me@pc.local/Share" gives "smb://", "me@", "pc.local/", "Share", so a line never breaks inside a name
    // such as "My-Laptop". A value with a space (a command) stays whole and wraps at its spaces.
    public static IReadOnlyList<string> Wrap(string? value)
    {
        var pieces = new List<string>();
        if (string.IsNullOrEmpty(value))
        {
            return pieces;
        }

        if (value!.IndexOf(' ') >= 0)
        {
            pieces.Add(value);
            return pieces;
        }

        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (IsBreak(value[i]) && (i + 1 == value.Length || !IsBreak(value[i + 1])))
            {
                pieces.Add(value.Substring(start, i + 1 - start));
                start = i + 1;
            }
        }

        if (start < value.Length)
        {
            pieces.Add(value.Substring(start));
        }

        return pieces;
    }

    private static bool IsBreak(char c) => c is '/' or '@' or '\\';
}
