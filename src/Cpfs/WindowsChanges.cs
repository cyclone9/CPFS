using System.Threading;

namespace Cpfs;

// Moves after every Setup apply and Uninstall run, whatever the outcome. The pages are kept alive between visits; one
// that read Windows before the counter moved reads it again when it is shown.
internal static class WindowsChanges
{
    private static int _count;

    public static int Count => Volatile.Read(ref _count);

    public static void Bump(string by)
    {
        var now = Interlocked.Increment(ref _count);
        AppLog.Write("state", "Windows changed by " + by + " (" + now.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
    }
}
