using System;
using System.Collections.Generic;

namespace Cpfs.Core.Connect;

// The Connect page's device tabs, phones first. The id is what preferences.json keeps.
public static class GuideTabs
{
    public static readonly IReadOnlyList<string> Ids = new[] { "ios", "android", "windows", "mac", "linux" };

    // The tab to open: the saved one when it is known, otherwise the first.
    public static int IndexOf(string? saved)
    {
        for (var i = 0; i < Ids.Count; i++)
        {
            if (string.Equals(Ids[i], saved?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }
}
