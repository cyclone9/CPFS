namespace Cpfs.Core.Text;

// NotifyIcon.Text throws above 63 characters on .NET Framework (Learn, NotifyIcon.Text). Share names go to 80
// characters and Turkish runs long, so the tooltip is fitted: the title and the status always, the share name as
// far as it fits.
public static class TrayTooltip
{
    public const int Max = 63;

    public static string Fit(string title, string status, string? share)
    {
        var head = (title ?? "").Trim() + "\n" + (status ?? "").Trim();
        if (head.Length > Max)
        {
            return Cut(head, Max);
        }

        var name = share?.Trim();
        var room = Max - head.Length - 1;

        // Fewer than four characters of a name tell the user nothing.
        if (string.IsNullOrEmpty(name) || room < 4)
        {
            return head;
        }

        return head + "\n" + (name!.Length <= room ? name : Cut(name, room));
    }

    // Ends in an ellipsis and never splits a surrogate pair.
    private static string Cut(string text, int max)
    {
        var n = max - 1;
        if (char.IsHighSurrogate(text[n - 1]))
        {
            n--;
        }

        return text.Substring(0, n).TrimEnd() + "…";
    }
}
