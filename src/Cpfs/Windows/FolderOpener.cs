using System;
using System.Diagnostics;
using System.IO;

namespace Cpfs.Windows;

// Opens a folder in File Explorer, for Setup's Open folder and the tray. Callers pass only a folder from a config
// CPFS trusts and check Directory.Exists first: Explorer given a file would run it.
internal static class FolderOpener
{
    // Throws Win32Exception, InvalidOperationException or FileNotFoundException when Explorer can't be started.
    public static void Open(string folder)
    {
        // A quoted path must not end in a backslash (it would escape the quote); a drive root has no spaces to quote.
        var argument = folder.TrimEnd('\\').EndsWith(":", StringComparison.Ordinal) ? folder.TrimEnd('\\') + "\\" : "\"" + folder.TrimEnd('\\') + "\"";
        using (Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), argument)
        {
            UseShellExecute = false,
        }))
        {
        }
    }
}
