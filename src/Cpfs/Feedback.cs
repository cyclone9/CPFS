using System;
using System.Runtime.InteropServices;
using System.Windows;
using Wpf.Ui.Controls;

namespace Cpfs;

internal static class Feedback
{
    // Copies a value the user asked for (an address, user name, path or command; never a password) and says so.
    public static void Copy(string label, string value)
    {
        var loc = Loc.Instance;
        try
        {
            Clipboard.SetText(value);
            AppLog.Write("copy", "Copied " + label);
            Toast(loc["Common_Copied"], ControlAppearance.Success);
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            AppLog.Write("copy", "Copy failed: " + ex.Message);
            Toast(loc.Format("Common_CopyFailed", ex.Message), ControlAppearance.Danger);
        }
    }

    public static void Toast(string title, ControlAppearance appearance)
    {
        if (MainWindow.Current?.Snackbars is not { } presenter)
        {
            return;
        }

        new Snackbar(presenter)
        {
            Title = title,
            Appearance = appearance,
            Timeout = TimeSpan.FromSeconds(2),
            FlowDirection = MainWindow.Current.FlowDirection,
        }.Show();
    }
}
