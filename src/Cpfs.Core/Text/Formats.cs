using System;
using System.Globalization;

namespace Cpfs.Core.Text;

// Numbers, dates and times on screen follow the app's language, never the Windows regional format: the owner's
// Windows can be English with a Persian region. Values the user copies or compares (log stamps, config dates)
// stay invariant.
public static class Formats
{
    public static string Count(long value, CultureInfo uiCulture) => value.ToString("N0", uiCulture);

    public static string DateTime(DateTime value, CultureInfo uiCulture) => value.ToString("g", uiCulture);

    public static string Stamp(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
