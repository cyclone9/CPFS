using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Cpfs.Core.Planning;
using Cpfs.Core.Preferences;
using Cpfs.Windows;
using Wpf.Ui.Controls;

namespace Cpfs.Pages;

// Start with Windows: the Run value named CPFS in this user's HKCU, the only state. Read each time the page opens,
// written only from an unelevated window and only while the tray icon is on (B2). StartupApproved is read for a
// hint and never written.
public partial class PreferencesPage
{
    private static readonly TimeSpan TrayExitWait = TimeSpan.FromSeconds(3);

    private StartReading? _start;
    private byte[]? _approved;
    private string? _startError;

    // What the last change said went wrong; shown until the next change or the next visit.
    private string? _actionMessage;
    private bool _startBusy;

    private static string ThisExe => typeof(PreferencesPage).Assembly.Location;

    // The tray switch's handler calls this after saving TrayIcon. Off also switches this off, in an elevated window too.
    private async Task OnTraySwitched(bool on)
    {
        if (!on)
        {
            await RunStart(() =>
            {
                RemoveOurs("the tray icon was switched off");
                return null;
            });
        }

        RefreshStart();
    }

    private void InitStartWithWindows()
    {
        Loaded += (_, _) =>
        {
            _actionMessage = null;
            RefreshStart();
        };
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Loc.Culture))
            {
                Dispatcher.BeginInvoke(new Action(ShowStart));
            }
        };
    }

    private async void RefreshStart()
    {
        if (Startup.IsElevated)
        {
            // An elevated window may hold another account's HKCU: nothing is read or written from here.
            _start = null;
            ShowStart();
            return;
        }

        try
        {
            var (reading, approved) = await Task.Run(() =>
            {
                var value = StartWithWindowsKeys.ReadCurrentUser();
                return (StartWithWindows.Decide(value, ThisExe, File.Exists), StartWithWindowsKeys.ApprovedCurrentUser());
            });
            _start = reading;
            _approved = approved;
            _startError = null;
            AppLog.Write("prefs", "Start with Windows: " + reading.State + (reading.Data is null ? "" : ", value " + reading.Data)
                + ", StartupApproved " + StartWithWindows.Hex(approved));
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            AppLog.Exception("prefs", ex);
            _start = null;
            _startError = UiErrors.ForUser(ex);
        }

        ShowStart();
    }

    private void ShowStart()
    {
        var loc = Loc.Instance;
        var trayOn = Startup.Preferences.TrayIcon;
        var state = _start?.State ?? StartState.Off;
        _syncing = true;
        StartSwitch.IsChecked = !Startup.IsElevated && state is StartState.On or StartState.OtherCopy or StartState.TargetMissing;
        StartSwitch.IsEnabled = TrayRules.StartSwitchEnabled(Startup.IsElevated, trayOn, _start is not null, state == StartState.Foreign) && !_startBusy;
        _syncing = false;

        // An elevated window: the tray card's own note above says what can be changed here.
        var note = !Startup.IsElevated && !trayOn ? loc["Pref_Start_NeedsTray"] : null;
        StartNote.Text = note ?? "";
        StartNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;

        string? message = null;
        var severity = InfoBarSeverity.Warning;
        if (Startup.IsElevated)
        {
            message = null;
        }
        else if (_actionMessage is not null)
        {
            message = _actionMessage;
            severity = InfoBarSeverity.Error;
        }
        else if (_startError is not null)
        {
            message = loc.FormatValues("Pref_Start_ReadFailed", _startError);
            severity = InfoBarSeverity.Error;
        }
        else if (state == StartState.OtherCopy)
        {
            message = loc.FormatValues("Pref_Start_OtherCopy", _start!.Path);
            severity = InfoBarSeverity.Informational;
        }
        else if (state == StartState.TargetMissing)
        {
            message = loc.FormatValues("Pref_Start_Missing", _start!.Path);
        }
        else if (state == StartState.Foreign)
        {
            message = loc.FormatValues("Pref_Start_Foreign", _start!.Data ?? "?");
        }

        SetInfo(StartInfo, severity, message);

        var hint = !Startup.IsElevated && state is StartState.On or StartState.OtherCopy or StartState.TargetMissing
            && StartWithWindows.DisabledInSettings(_approved);
        SetInfo(StartHint, InfoBarSeverity.Informational, hint ? loc["Pref_Start_Disabled"] : null);

        var useThis = !Startup.IsElevated && state is StartState.OtherCopy or StartState.TargetMissing or StartState.Foreign;
        UseThisCopyButton.Visibility = useThis ? Visibility.Visible : Visibility.Collapsed;
        UseThisCopyButton.IsEnabled = trayOn && !_startBusy;
        StartupAppsButton.Visibility = hint ? Visibility.Visible : Visibility.Collapsed;
        StartActions.Visibility = useThis || hint ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnStartSwitch(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var on = StartSwitch.IsChecked == true;
        if (Startup.IsElevated || !Startup.Preferences.TrayIcon)
        {
            ShowStart();
            return;
        }

        await RunStart(() =>
        {
            // Read again before writing: the value may have changed since the page opened.
            var now = StartWithWindows.Decide(StartWithWindowsKeys.ReadCurrentUser(), ThisExe, File.Exists);
            if (!on)
            {
                RemoveOurs("switched off");
                return null;
            }

            if (now.State != StartState.Off)
            {
                return null;
            }

            if (StartWithWindows.Command(ThisExe, out var refusal) is not { } command)
            {
                return Refused(refusal);
            }

            StartWithWindowsKeys.WriteCurrentUser(command);
            AppLog.Write("prefs", "Start with Windows on: " + command);
            return null;
        });
        RefreshStart();
    }

    // Points the value at this exe (also over a foreign value named CPFS: the user pressed the button), stops the tray
    // that runs now and starts this copy's.
    private async void OnUseThisCopy(object sender, RoutedEventArgs e)
    {
        if (Startup.IsElevated || !Startup.Preferences.TrayIcon)
        {
            return;
        }

        await RunStart(() =>
        {
            if (StartWithWindows.Command(ThisExe, out var refusal) is not { } command)
            {
                return Refused(refusal);
            }

            var before = StartWithWindowsKeys.ReadCurrentUser();
            StartWithWindowsKeys.WriteCurrentUser(command);
            AppLog.Write("prefs", "Start with Windows: use this copy, " + (before?.Text ?? "(not a string)") + " -> " + command);
            TrayControl.Stop("use this copy");
            if (!TrayControl.WaitUntilGone(TrayExitWait))
            {
                AppLog.Write("prefs", "The running tray did not exit within " + TrayExitWait.TotalSeconds + " s");
                return Loc.Instance["Pref_Start_OldTrayRunning"];
            }

            return TrayControl.StartIfMissing("use this copy", TimeSpan.Zero) is { } error
                ? Loc.Instance.FormatValues("Pref_Start_TrayNotStarted", error)
                : null;
        });
        RefreshStart();
    }

    private void OnOpenStartupApps(object sender, RoutedEventArgs e)
    {
        try
        {
            using (Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }))
            {
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppLog.Exception("prefs", ex);
        }
    }

    // Runs a registry change off the UI thread; work returns a message for the user, or null.
    private async Task RunStart(Func<string?> work)
    {
        _startBusy = true;
        _actionMessage = null;
        ShowStart();
        string? message;
        try
        {
            message = await Task.Run(work);
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            AppLog.Exception("prefs", ex);
            message = Loc.Instance.FormatValues("Pref_Start_Failed", UiErrors.ForUser(ex));
        }
        finally
        {
            _startBusy = false;
        }

        _actionMessage = message;
        ShowStart();
    }

    // A value in CPFS's format goes; any other value named CPFS stays.
    private static void RemoveOurs(string why)
    {
        var value = StartWithWindowsKeys.ReadCurrentUser();
        if (value is null)
        {
            return;
        }

        if (StartWithWindows.TargetOf(value.Text) is null)
        {
            AppLog.Write("prefs", "Start with Windows off (" + why + "): a value named CPFS that isn't CPFS's is left: " + (value.Text ?? "(not a string)"));
            return;
        }

        StartWithWindowsKeys.DeleteCurrentUser();
        AppLog.Write("prefs", "Start with Windows off (" + why + "): removed " + value.Text);
    }

    private static string Refused(CommandRefusal refusal)
    {
        AppLog.Write("prefs", "Start with Windows refused for " + ThisExe + ": " + refusal);
        return refusal == CommandRefusal.TooLong
            ? Loc.Instance["Pref_Start_PathTooLong"]
            : Loc.Instance.FormatValues("Pref_Start_PathRefused", ThisExe);
    }

    private static void SetInfo(InfoBar bar, InfoBarSeverity severity, string? message)
    {
        bar.Title = message is null ? "" : Loc.Instance["Pref_Start_Title"];
        bar.Message = message ?? "";
        bar.Severity = severity;
        bar.IsOpen = message is not null;
    }
}
