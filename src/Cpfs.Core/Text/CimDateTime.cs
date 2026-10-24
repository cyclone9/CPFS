using System;
using System.Globalization;

namespace Cpfs.Core.Text;

// WMI's CIM DATETIME, yyyymmddHHMMSS.mmmmmmsUUU, where sUUU is the zone's offset from UTC in minutes
// [Learn: CIM_DATETIME]. Kept as a DateTimeOffset so the offset is never lost: the 0.6.0 dev2 log wrote a local
// restore-point time with a "Z" because "u" formatting does not convert to UTC.
public static class CimDateTime
{
    public static bool TryParse(string? value, out DateTimeOffset result)
    {
        result = default;
        if (value is null || value.Length != 25 || value[14] != '.' || (value[21] != '+' && value[21] != '-'))
        {
            return false;
        }

        // Asterisks mark unused fields (a range or an unzoned time), which is no point in time.
        if (!DateTime.TryParseExact(value.Substring(0, 21), "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            || !int.TryParse(value.Substring(22, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || minutes > 14 * 60)
        {
            return false;
        }

        var offset = TimeSpan.FromMinutes(value[21] == '-' ? -minutes : minutes);
        result = new DateTimeOffset(local, offset);
        return true;
    }

    // ISO 8601 with the real offset, for the log.
    public static string Iso(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}
