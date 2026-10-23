namespace Cpfs.Core.Text;

public static class VersionText
{
    // "0.5.0-dev1+3d20e71..." (the SDK appends the full commit) becomes "0.5.0-dev1 (3d20e71)"; without build
    // metadata the version stays as it is.
    public static string Display(string? informational)
    {
        var text = (informational ?? "").Trim();
        var plus = text.IndexOf('+');
        if (plus < 0)
        {
            return text;
        }

        var version = text.Substring(0, plus);
        var meta = text.Substring(plus + 1);
        if (meta.Length == 0)
        {
            return version;
        }

        return version + " (" + (IsHex(meta) && meta.Length > 7 ? meta.Substring(0, 7) : meta) + ")";
    }

    // "0.5.0-dev3+3d20e71..." becomes "0.5.0-dev3": the window title and config.json's Version.
    public static string Number(string? informational)
    {
        var text = (informational ?? "").Trim();
        var plus = text.IndexOf('+');
        return plus < 0 ? text : text.Substring(0, plus);
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }
}
