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
}
