using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Threading;

namespace Cpfs.Windows;

// The window's side of the tray icon: start "Cpfs.exe --tray" and ask it to exit. Both run only in an unelevated
// window (design B2): a child of an elevated window would be elevated, and the tray refuses that anyway.
internal static class TrayControl
{
    public const string Verb = "--tray";

    // Named after the product, as the window's own mutex is: a renamed copy is the same tray.
    public const string MutexName = @"Local\CPFS-CyClonePowerFileShare-Tray";

    // Carries no data, and its receiver is always the unelevated tray; the worst a process of the user can do with it
    // is make the icon go away.
    public const string ExitEventName = @"Local\CPFS-CyClonePowerFileShare-TrayExit";

    public static bool IsRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName, MutexRights.Synchronize, out var mutex))
            {
                mutex.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // It exists; only its owner may open it.
            return true;
        }
    }

    // Starts the tray unless one runs. wait: how long a tray that was just told to exit may take to let go of its
    // mutex (switched off and on again quickly). Returns the error text for the user, or null.
    public static string? StartIfMissing(string reason, TimeSpan wait)
    {
        if (Startup.IsElevated)
        {
            AppLog.Write("tray", "Not started from an elevated window (" + reason + ")");
            return null;
        }

        var until = DateTime.UtcNow + wait;
        while (IsRunning())
        {
            if (DateTime.UtcNow >= until)
            {
                AppLog.Write("tray", "Already running (" + reason + ")");
                return null;
            }

            Thread.Sleep(100);
        }

        try
        {
            var exe = typeof(TrayControl).Assembly.Location;
            using var process = Process.Start(new ProcessStartInfo(exe, Verb) { UseShellExecute = false });
            AppLog.Write("tray", "Started the tray icon (" + reason + "), process " + (process?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"));
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Write("tray", "Could not start the tray icon (" + reason + "): " + ex.GetType().Name + ": " + ex.Message);
            return ex.Message;
        }
    }

    public static void Stop(string reason)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ExitEventName, EventWaitHandleRights.Modify, out var signal))
            {
                AppLog.Write("tray", "No tray icon to stop (" + reason + ")");
                return;
            }

            using (signal)
            {
                signal.Set();
            }

            AppLog.Write("tray", "Asked the tray icon to exit (" + reason + ")");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            AppLog.Write("tray", "Could not ask the tray icon to exit (" + reason + "): " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
