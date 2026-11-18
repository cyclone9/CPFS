using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Cpfs.Core.Monitor;
using Cpfs.Core.Updates;
using Cpfs.Windows.Update;
using Wpf.Ui.Controls;

namespace Cpfs.Pages;

// What the About page shows after an update that did not restart CPFS: a sentence, and the saved file for "Show in folder".
internal sealed record UpdateUiResult(InfoBarSeverity Severity, string Message, string? SavedPath = null);

// "Update now" (About only, unelevated only): confirm, download with progress and Cancel, then either the swap and the
// restart or Save as…. The progress dialog stays up from the download to the restart, so after the swap nothing else
// in this window can be used (U6); the process then only starts the new version and exits.
internal static class UpdateUi
{
    public static bool IsRunning { get; private set; }

    public static async Task<UpdateUiResult?> RunAsync(FrameworkElement page, UpdateOffer offer)
    {
        // U2: the page disables the button too; this is the second lock.
        if (IsRunning || Startup.IsElevated)
        {
            return null;
        }

        IsRunning = true;
        try
        {
            return await RunCoreAsync(page, offer);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private static async Task<UpdateUiResult?> RunCoreAsync(FrameworkElement page, UpdateOffer offer)
    {
        var loc = Loc.Instance;
        var exe = UpdateEngine.RunningExe;
        var oldVersion = AppLog.VersionNumber();
        var place = UpdateEngine.PlaceOf(exe, Startup.IsElevated);
        switch (place)
        {
            case UpdatePlace.Elevated:
                return null;
            case UpdatePlace.BackgroundCopy:
                return new UpdateUiResult(InfoBarSeverity.Warning, loc["Update_Err_BackgroundCopy"]);
        }

        if (!await ConfirmAsync(page, offer, exe, place))
        {
            AppLog.Write("update", "Update to " + offer.Version + " not confirmed");
            return null;
        }

        using var cancel = new CancellationTokenSource();
        var status = Text(loc.Format("Update_Step_Download", offer.Version));
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 4, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(bar, loc["Update_ProgressTitle"]);
        var content = new StackPanel { MaxWidth = 520, MinWidth = 360 };
        content.Children.Add(status);
        content.Children.Add(bar);
        var box = ApplyUi.NewChoice(page, loc["Update_ProgressTitle"], content);
        var cancelButton = box.AddButton(loc["Common_Cancel"], Wpf.Ui.Controls.MessageBoxButton.Close, ControlAppearance.Secondary);
        // 0 running, 1 committed to the swap, 2 cancelled: whichever comes first wins.
        var state = 0;
        var fromCode = false;
        bool Commit() => Interlocked.CompareExchange(ref state, 1, 0) == 0;
        box.Closing += (_, e) =>
        {
            if (fromCode)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            {
                cancel.Cancel();
                return;
            }

            // From the swap on, the update runs to its end.
            e.Cancel = state == 1;
        };

        void OnProgress(UpdateStep step, long done, long total) => box.Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (step)
            {
                case UpdateStep.Download when total > 0 && done > 0:
                    bar.IsIndeterminate = false;
                    bar.Value = Math.Min(100, done * 100.0 / total);
                    status.Text = loc.Format("Update_Step_DownloadBytes", offer.Version, Size(done), Size(total));
                    break;
                case UpdateStep.Stage:
                    bar.IsIndeterminate = true;
                    status.Text = loc["Update_Step_Check"];
                    break;
                case UpdateStep.Probe:
                    status.Text = loc["Update_Step_Probe"];
                    break;
                case UpdateStep.Swap:
                    cancelButton.IsEnabled = false;
                    status.Text = loc["Update_Step_Swap"];
                    break;
            }
        }));

        var work = Task.Run(() => UpdateEngine.DownloadAndSwap(offer, exe, place, OnProgress, Commit, cancel.Token));
        _ = work.ContinueWith(t => box.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result.SaveAsBytes is null)
            {
                status.Text = loc["Update_Step_Restart"];
                Restart(box, exe, oldVersion, offer.Version);
                return;
            }

            fromCode = true;
            box.CloseFromCode();
        }), System.Windows.Threading.DispatcherPriority.Background), TaskScheduler.Default);

        await box.ShowLoggedAsync("update", "Update_ProgressTitle");

        UpdateDone done;
        try
        {
            done = await work;
        }
        catch (UpdateException ex)
        {
            AppLog.Write("update", "Update to " + offer.Version + " stopped: " + ex.Error
                + (ex.Error == UpdateError.RollbackFailed ? "; the previous version is NOT back in place" : "; nothing was replaced"));
            return new UpdateUiResult(ex.Error == UpdateError.Cancelled ? InfoBarSeverity.Informational : InfoBarSeverity.Error, Failure(ex, offer));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Exception("update", ex);
            return new UpdateUiResult(InfoBarSeverity.Error, loc.FormatValues("Update_Err_Unexpected", ex.GetType().Name));
        }

        return done.SaveAsBytes is { } bytes ? await SaveAsAsync(page, offer, bytes, exe, place) : null;
    }

    // After the swap: release the window's single-instance guard, start the new exe, and exit whatever it does.
    private static void Restart(ChoiceDialog box, string exe, string oldVersion, string newVersion)
    {
        Program.Instance?.Dispose();
        AppLog.Write("update", "Released the single-instance guard; starting the new version");
        Task.Run(() => UpdateEngine.Restart(exe, oldVersion, newVersion)).ContinueWith(t => box.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (t.IsFaulted)
            {
                var loc = Loc.Instance;
                var folder = System.IO.Path.GetDirectoryName(exe) ?? "";
                var name = System.IO.Path.GetFileName(exe);
                var message = t.Exception?.InnerException is UpdateException { Error: UpdateError.RollbackFailed }
                    ? loc.FormatValues("Update_Err_RollbackFailed", folder, name + UpdateRules.OldSuffix, name)
                    : loc.FormatValues("Update_Err_Unexpected", t.Exception?.InnerException?.GetType().Name ?? "?");
                AppLog.Write("update", "Restart failed: " + (t.Exception?.InnerException?.GetType().Name ?? "?"));
                System.Windows.MessageBox.Show(message, loc["Update_Err_Title"], System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                AppLog.Write("update", "This process exits (" + t.Result + ")");
            }

            Application.Current.Shutdown(0);
        })), TaskScheduler.Default);
    }

    private static async Task<bool> ConfirmAsync(FrameworkElement page, UpdateOffer offer, string exe, UpdatePlace place)
    {
        var loc = Loc.Instance;
        var content = new StackPanel { MaxWidth = 520 };
        content.Children.Add(Text(loc.Format("Update_ConfirmDownload", offer.Version, Size(offer.ExeSize))));
        content.Children.Add(Text(loc["Update_ConfirmLimit"]));
        content.Children.Add(Text(place == UpdatePlace.InPlace
            ? loc.FormatValues("Update_ConfirmInPlace", System.IO.Path.GetFileName(exe))
            : loc["Update_ConfirmSaveAs"]));

        var details = new StackPanel();
        details.Children.Add(Value(loc.FormatValues("Update_ConfirmFrom", offer.ExeUrl)));
        details.Children.Add(Value(loc.FormatValues("Update_ConfirmTo", exe)));
        var expander = new CardExpander { Header = loc["Common_ShowDetails"], Content = details, Margin = new Thickness(0, 0, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(expander, loc["Common_ShowDetails"]);
        ApplyUi.AttachDetails(expander);
        content.Children.Add(expander);

        // No default button: only a click on "Update now" starts it (Windows 11 UI rules).
        var box = ApplyUi.NewChoice(page, loc.Format("Update_ConfirmTitle", offer.Version), content);
        box.AddButton(loc[place == UpdatePlace.InPlace ? "Update_ConfirmButton" : "Update_ConfirmSaveButton"], Wpf.Ui.Controls.MessageBoxButton.Primary, ControlAppearance.Primary);
        box.AddButton(loc["Common_Cancel"], Wpf.Ui.Controls.MessageBoxButton.Close, ControlAppearance.Secondary, isCancel: true);
        void OnToggle(object sender, RoutedEventArgs e) => box.Dispatcher.BeginInvoke(new Action(() => ApplyUi.FitHeight(box)), System.Windows.Threading.DispatcherPriority.Loaded);
        expander.Expanded += OnToggle;
        expander.Collapsed += OnToggle;
        return Core.Text.DialogCloses.Confirms(await box.ShowLoggedAsync("update", "Update_ConfirmTitle"));
    }

    // The verified bytes go where the user says; CPFS never elevates to write an exe.
    private static async Task<UpdateUiResult?> SaveAsAsync(FrameworkElement page, UpdateOffer offer, byte[] bytes, string exe, UpdatePlace place)
    {
        var loc = Loc.Instance;
        if (place == UpdatePlace.InPlace)
        {
            // The folder refused the staging file: say so before the save dialog.
            var box = ApplyUi.NewChoice(page, loc["Update_SaveAs_Title"], Text(loc["Update_SaveAs_WhyWrite"]));
            box.AddButton(loc["Update_SaveAs_Button"], Wpf.Ui.Controls.MessageBoxButton.Primary, ControlAppearance.Primary);
            box.AddButton(loc["Common_Cancel"], Wpf.Ui.Controls.MessageBoxButton.Close, ControlAppearance.Secondary, isCancel: true);
            if (!Core.Text.DialogCloses.Confirms(await box.ShowLoggedAsync("update", "Update_SaveAs_Title")))
            {
                return new UpdateUiResult(InfoBarSeverity.Informational, loc["Update_NotSaved"]);
            }
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = loc["Update_SaveAs_Title"],
            FileName = offer.ExeName,
            InitialDirectory = UpdateEngine.DownloadsFolder(),
            DefaultExt = ".exe",
            AddExtension = true,
            OverwritePrompt = true,
            Filter = loc["Update_SaveAs_Filter"],
        };
        if (dialog.ShowDialog(Window.GetWindow(page)) != true)
        {
            AppLog.Write("update", "Save as: cancelled");
            return new UpdateUiResult(InfoBarSeverity.Informational, loc["Update_NotSaved"]);
        }

        var target = dialog.FileName;
        try
        {
            await Task.Run(() => UpdateEngine.SaveVerified(bytes, target, exe));
        }
        catch (UpdateException ex)
        {
            return new UpdateUiResult(InfoBarSeverity.Error, Failure(ex, offer));
        }

        return new UpdateUiResult(InfoBarSeverity.Success, loc.FormatValues("Update_Saved", offer.Version, target), target);
    }

    private static string Failure(UpdateException ex, UpdateOffer offer)
    {
        var loc = Loc.Instance;
        return ex.Error switch
        {
            UpdateError.WrongVersion => loc.FormatValues("Update_Err_WrongVersion", offer.Version),
            UpdateError.Leftover => loc.FormatValues("Update_Err_Leftover", ex.Detail ?? "?"),
            UpdateError.SwapFailed when ex.Detail is null => loc["Update_Err_SwapReadBack"],
            UpdateError.SwapFailed => loc.FormatValues("Update_Err_SwapFailed", ex.Detail),
            UpdateError.RollbackFailed when ex.Detail is { } exe => loc.FormatValues("Update_Err_PutBackFailed",
                System.IO.Path.GetDirectoryName(exe) ?? "", System.IO.Path.GetFileName(UpdateRules.OldPath(exe)), System.IO.Path.GetFileName(exe)),
            UpdateError.RollbackFailed => loc.FormatValues("Update_Err_PutBackFailed", "?", "?", "?"),
            UpdateError.SaveFailed => loc.FormatValues("Update_Err_SaveFailed", ex.Detail ?? "?"),
            _ => loc["Update_Err_" + ex.Error.ToString()],
        };
    }

    public static string Size(long bytes)
    {
        var (value, unit) = MonitorMath.ScaleBytes(bytes);
        return Loc.Instance.Format("Size_" + unit.ToString(CultureInfo.InvariantCulture), value.ToString(unit == 0 ? "N0" : "N1", Loc.Instance.Culture));
    }

    private static System.Windows.Controls.TextBlock Text(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

    // A URL or a path, in the monospace font.
    private static System.Windows.Controls.TextBlock Value(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), FontFamily = Theming.MonoFamily };
}
