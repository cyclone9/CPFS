using System;
using System.Windows;
using System.Windows.Threading;
using Cpfs.Core.Planning;

namespace Cpfs;

// Minimize to the tray: an unelevated window with the tray icon running hides instead of going to the taskbar, and
// comes back through the tray's "Open CPFS", which is an ordinary second start and SingleInstance's show event. No new
// channel. An elevated window always minimizes to the taskbar (design B2).
public partial class MainWindow
{
    private static readonly TimeSpan TrayCheck = TimeSpan.FromSeconds(2);

    private WindowState _beforeMinimize = WindowState.Normal;
    private DispatcherTimer? _trayWatch;

    internal bool InTray { get; private set; }

    private void InitMinimizeToTray() => StateChanged += OnStateChangedForTray;

    private void OnStateChangedForTray(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            _beforeMinimize = WindowState;
            return;
        }

        if (InTray || !TrayRules.HideOnMinimize(Startup.IsElevated, Startup.Preferences.TrayIcon, Windows.TrayControl.IsRunning()))
        {
            return;
        }

        InTray = true;
        Dispatcher.BeginInvoke(new Action(Hide));
        AppLog.Write("window", "Minimized to the tray icon");

        // If the tray goes away meanwhile (Exit in its menu), the window goes back to the taskbar, still minimized.
        _trayWatch ??= new DispatcherTimer(TrayCheck, DispatcherPriority.Background, OnTrayWatch, Dispatcher);
        _trayWatch.Start();
    }

    private void OnTrayWatch(object? sender, EventArgs e)
    {
        if (!InTray)
        {
            _trayWatch?.Stop();
            return;
        }

        if (!Windows.TrayControl.IsRunning())
        {
            _trayWatch?.Stop();
            InTray = false;
            ShowInTaskbar = true;
            Show();
            AppLog.Write("window", "The tray icon is gone: the minimized window is back on the taskbar");
        }
    }

    // Called by the show event's handler. False when the window was not in the tray.
    internal bool RestoreFromTray()
    {
        if (!InTray)
        {
            return false;
        }

        InTray = false;
        _trayWatch?.Stop();
        ShowInTaskbar = true;
        Show();
        WindowState = _beforeMinimize;
        AppLog.Write("window", "Restored from the tray icon");
        return true;
    }
}
