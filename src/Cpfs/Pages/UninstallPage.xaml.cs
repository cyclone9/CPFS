using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Cpfs.Core.Apply;
using Cpfs.Core.Config;
using Cpfs.Core.Drives;
using Cpfs.Core.Planning;
using Cpfs.Windows;
using Cpfs.Windows.Apply;
using Wpf.Ui.Controls;

namespace Cpfs.Pages;

// Uninstall: what it would undo, from config.json's Backup and Tracked and the state now, then Apply after the
// user confirmed, with a restore point first. It reads elevated only; nothing is written on page load.
public partial class UninstallPage : Page
{
    private readonly Dictionary<string, bool> _choices = new(StringComparer.Ordinal);
    private readonly ObservableCollection<StepView> _steps = new();
    private UninstallInput? _input;
    private byte[]? _configBytes;
    private string? _documents;
    private Plan? _plan;
    private PlanSummary? _summary;
    private string? _lastSummaryLogged;
    private bool _noNewRestorePoint;
    private bool _loaded;
    private bool _reading;
    private bool _applying;

    public UninstallPage()
    {
        InitializeComponent();
        ProgressList.ItemsSource = _steps;
        ApplyUi.AttachDetails(DetailsExpander);
        Loaded += OnLoaded;
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Loc.Culture))
            {
                Render();
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!Startup.IsElevated)
        {
            AdminCard.Visibility = Visibility.Visible;
            return;
        }

        PlanArea.Visibility = Visibility.Visible;
        await ReadAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReadAsync();

    private async Task ReadAsync()
    {
        if (_reading)
        {
            return;
        }

        _reading = true;
        RefreshButton.IsEnabled = false;
        SummaryBody.Text = Loc.Instance["Setup_Reading"];
        try
        {
            var (input, bytes, documents) = await Task.Run(ReadInput);
            _input = input;
            _configBytes = bytes;
            _documents = documents;
            Recompute();
        }
        catch (Exception ex) when (State.IsExpected(ex) || ex is ConfigFormatException)
        {
            AppLog.Exception("uninstall", ex);
            Show(ApplyResult, InfoBarSeverity.Error, Loc.Instance["Setup_ReadFailed"], ex.Message);
        }
        finally
        {
            _reading = false;
            RefreshButton.IsEnabled = true;
            UpdateApplyState();
        }
    }

    private static (UninstallInput Input, byte[]? Bytes, string? Documents) ReadInput()
    {
        var path = StateReader.ConfigPath;
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var config = bytes is null ? null : ConfigReader.Parse(bytes, Encoding.Default);
        var desktop = DesktopUser.Current;
        var snapshot = PlanReader.ReadStatic(new LetterScan(), desktop.Sid);
        snapshot = PlanReader.ReadAnswerParts(snapshot, config?.SmbUser ?? "", config?.SharePath ?? "", config?.ShareName ?? "");

        var protectedSids = new List<string>();
        using (var self = WindowsIdentity.GetCurrent())
        {
            if (self.User?.Value is { } sid)
            {
                protectedSids.Add(sid);
            }
        }

        var desktopName = "?";
        if (desktop.Sid is not null)
        {
            protectedSids.Add(desktop.Sid);
            try
            {
                desktopName = new SecurityIdentifier(desktop.Sid).Translate(typeof(NTAccount)).Value;
            }
            catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
            {
                desktopName = desktop.Sid;
            }
        }

        string? profile = null;
        if (snapshot.User.IsOk && snapshot.User.Value is { } user)
        {
            try
            {
                profile = Profiles.PathOf(user.Sid);
            }
            catch (Exception ex) when (State.IsExpected(ex))
            {
                AppLog.Write("uninstall", "Profile: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        var input = new UninstallInput
        {
            Snapshot = snapshot,
            Config = config,
            AutoPrivateNames = AutoPrivateNames(),
            ProfilePath = profile,
            ProtectedSids = protectedSids,
            DesktopUser = desktopName,
            ProtectedFolders = PlanReader.ProtectedFolders(),
        };
        return (input, bytes, desktop.Documents);
    }

    // The names the scripts' Auto-Private task logged: ANSI before 0.2.3, UTF-8 since. A wrong reading matches no
    // network, so both are taken.
    private static List<string> AutoPrivateNames()
    {
        var names = new List<string>();
        var log = Path.Combine(StateReader.DataFolder, "auto-private.log");
        if (!File.Exists(log))
        {
            return names;
        }

        try
        {
            foreach (var encoding in new[] { Encoding.Default, Encoding.UTF8 })
            {
                foreach (var line in File.ReadAllLines(log, encoding))
                {
                    var name = line.Trim();
                    if (name.Length > 0 && !names.Contains(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("uninstall", "auto-private.log: " + ex.Message);
        }

        return names;
    }

    private void Recompute()
    {
        if (_input is null)
        {
            return;
        }

        _plan = UninstallPlanner.Uninstall(_input with { Choices = new Dictionary<string, bool>(_choices, StringComparer.Ordinal) });
        AppLog.Write("uninstall", "Plan: " + string.Join(", ", Enum.GetValues(typeof(PlanAction)).Cast<PlanAction>().Select(a => a + " " + _plan.Count(a))));
        foreach (var line in _plan.LogLines())
        {
            AppLog.Write("uninstall", "  " + line);
        }

        _summary = PlanSummary.Uninstall(_plan);
        var summaryLog = _summary.LogText();
        if (!string.Equals(summaryLog, _lastSummaryLogged, StringComparison.Ordinal))
        {
            _lastSummaryLogged = summaryLog;
            AppLog.Write("uninstall", "Summary: " + summaryLog);
        }

        Render();
    }

    private void Render()
    {
        if (_plan is null)
        {
            return;
        }

        var loc = Loc.Instance;
        SummaryBody.Text = _summary is null ? "" : ApplyUi.SummaryText(_summary);
        var decisions = SetupPage.AskViews(_plan);
        Decisions.ItemsSource = decisions;
        DecisionsCard.Visibility = decisions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlanCounts.Text = loc.Format("Setup_Plan_Summary",
            ApplyUi.Formats(_plan.Count(PlanAction.Keep)),
            ApplyUi.Formats(_plan.Count(PlanAction.Change)),
            ApplyUi.Formats(_plan.Count(PlanAction.Create)),
            ApplyUi.Formats(_plan.Count(PlanAction.RemoveIfOurs)),
            ApplyUi.Formats(_plan.Count(PlanAction.Ask)),
            ApplyUi.Formats(_plan.Count(PlanAction.CannotPlan)));
        PlanGroups.ItemsSource = SetupPage.Groups(_plan);
        UpdateApplyState();
    }

    private void UpdateApplyState()
    {
        var loc = Loc.Instance;
        var enabled = false;
        string hint;
        if (_applying)
        {
            hint = loc["Apply_Running"];
        }
        else if (_plan is null)
        {
            hint = "";
        }
        else if (ApplyList.Blocking(_plan).Count > 0)
        {
            hint = loc["Apply_Blocked"];
        }
        else if (!ApplyList.Uninstall(_plan).Any(e => !e.Deferred))
        {
            // The summary already says there is nothing to undo.
            hint = "";
        }
        else
        {
            hint = loc["Apply_Ready"];
            enabled = true;
        }

        ApplyButton.IsEnabled = enabled && !_reading;
        RestorePointBox.IsEnabled = !_applying;
        ApplyBusy.Visibility = _applying ? Visibility.Visible : Visibility.Collapsed;
        ApplyHint.Text = hint;
        ApplyHint.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetHelpText(ApplyButton, hint);
    }

    private void OnPlanTickChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string key } box && box.IsChecked is { } ticked && !_applying)
        {
            _choices[key] = ticked;
            Recompute();
        }
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_applying || _plan is null || _input is null)
        {
            return;
        }

        var loc = Loc.Instance;
        var plan = _plan;
        var entries = ApplyList.Uninstall(plan);
        var restorePoint = RestorePointBox.IsChecked == true;
        if (!await ApplyUi.ConfirmAsync(this, "Uninst_ConfirmTitle", "Uninst_ConfirmMsg", "Uninst_ConfirmUndo", "Uninst_Apply", entries, restorePoint))
        {
            AppLog.Write("uninstall", "The user cancelled at the summary");
            return;
        }

        _applying = true;
        UpdateApplyState();
        RefreshButton.IsEnabled = false;
        ApplyResult.IsOpen = false;
        ResultActions.Visibility = Visibility.Collapsed;
        _noNewRestorePoint = false;
        _steps.Clear();
        ProgressCard.Visibility = Visibility.Visible;
        try
        {
            AppLog.Write("uninstall", "Apply confirmed, " + entries.Count + " items, restore point " + restorePoint);
            if (restorePoint && !await RestorePointFirstAsync())
            {
                Show(ApplyResult, InfoBarSeverity.Warning, loc["Apply_StoppedTitle"], loc["Uninst_StoppedMsg"]);
                return;
            }

            var views = new List<StepView>();
            foreach (var entry in entries)
            {
                var view = StepView.For(entry.Item);
                views.Add(view);
                _steps.Add(view);
            }

            var input = _input;
            var userSid = input.Snapshot.User.IsOk ? input.Snapshot.User.Value?.Sid : null;
            var run = new UninstallRun(plan, input.Config, _configBytes, userSid, DesktopUser.Current.Sid, _documents);
            var dispatcher = Dispatcher;
            var result = await Task.Run(() => run.Execute(r => dispatcher.BeginInvoke(new Action(() => views[r.Index].Set(r.Outcome, r.Error)))));
            foreach (var r in result.Reports)
            {
                views[r.Index].Set(r.Outcome, r.Error);
            }

            if (result.Completed)
            {
                var (_, message, severity) = ApplyUi.DoneMessage(result, entries);
                var notes = new List<string>();
                if (message.Length > 0)
                {
                    notes.Add(message);
                }

                if (run.SavedCopy is not null)
                {
                    notes.Add(loc.FormatValues("Uninst_CopySaved", run.SavedCopy));
                }

                if (run.DriveRestartNeeded)
                {
                    notes.Add(loc["Uninst_RestartDriveMsg"]);
                }

                if (_noNewRestorePoint)
                {
                    notes.Add(loc["Apply_NoRestorePointNote"]);
                }

                Show(ApplyResult, severity, loc["Uninst_DoneTitle"], string.Join(Environment.NewLine, notes));
                ResultActions.Visibility = result.NotDone > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                var failed = entries.First(x => x.Item.Key == result.FailedKey).Item;
                var message = loc.FormatValues("Uninst_FailedMsg", result.Error);
                if (run.SavedCopy is not null)
                {
                    message += " " + loc.FormatValues("Uninst_CopySaved", run.SavedCopy);
                }

                Show(ApplyResult, InfoBarSeverity.Error, loc.FormatValues("Uninst_FailedTitle", SetupPage.ToView(failed).Subject), message);
                ResultActions.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("uninstall", ex);
            Show(ApplyResult, InfoBarSeverity.Error, loc["Apply_ErrorTitle"], ex.GetType().Name + ": " + ex.Message);
            ResultActions.Visibility = Visibility.Visible;
        }
        finally
        {
            _applying = false;
            _choices.Clear();
            await ReadAsync();
        }
    }

    private async Task<bool> RestorePointFirstAsync()
    {
        var view = StepView.Named(Loc.Instance["Apply_RestorePointStep"]);
        _steps.Add(view);
        view.Set(StepOutcome.Running, null);
        var result = await Task.Run(() => RestorePoint.Create("CPFS: before Uninstall"));
        if (result.Outcome == RestorePointOutcome.Created)
        {
            view.Set(StepOutcome.Done, null);
            return true;
        }

        if (result.Outcome == RestorePointOutcome.Skipped)
        {
            view.SetSkipped();
        }
        else
        {
            view.Set(StepOutcome.Failed, result.Error);
        }

        _noNewRestorePoint = true;
        return await ApplyUi.ContinueWithoutRestorePointAsync(this, result);
    }

    private void OnCopyReport(object sender, RoutedEventArgs e) => ApplyUi.CopyReport(sender, out _);

    private async void OnContinueAsAdministrator(object sender, RoutedEventArgs e)
    {
        ElevateButton.IsEnabled = false;
        try
        {
            var letters = await Task.Run(() => DriveLetters.SessionLetters(DriveLetters.Scan(null)));
            var result = Relaunch.Start(Core.Elevation.Pages.Uninstall, letters, out var error);
            switch (result)
            {
                case RelaunchResult.Started:
                    Application.Current.Shutdown();
                    break;
                case RelaunchResult.Cancelled:
                    Show(AdminResult, InfoBarSeverity.Warning, Loc.Instance["Setup_Admin_CancelledTitle"], Loc.Instance["Setup_Admin_CancelledMsg"]);
                    break;
                default:
                    Show(AdminResult, InfoBarSeverity.Error, Loc.Instance["Setup_Admin_FailedTitle"], error ?? "");
                    break;
            }
        }
        finally
        {
            ElevateButton.IsEnabled = true;
        }
    }

    private static void Show(InfoBar bar, InfoBarSeverity severity, string title, string message)
    {
        bar.Severity = severity;
        bar.Title = title;
        bar.Message = message;
        bar.IsOpen = true;
    }
}
