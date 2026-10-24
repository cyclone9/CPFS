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

    // Reading Windows again between the button and the confirmation.
    private bool _checking;
    private bool _staleShown;

    // WindowsChanges.Count when the last reading began.
    private int _readAt = -1;

    // Whether the page shows "nothing to uninstall"; null until the first reading.
    private bool? _nothingShown;

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
            // Back on the page after Windows was changed elsewhere in CPFS (a Setup): the reading is stale.
            if (!Startup.IsElevated)
            {
                ShowUnelevated();
            }
            else if (_readAt != WindowsChanges.Count)
            {
                await ReadAsync();
            }

            return;
        }

        _loaded = true;
        if (!Startup.IsElevated)
        {
            ShowUnelevated();
            return;
        }

        PlanArea.Visibility = Visibility.Visible;
        await ReadAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (!Startup.IsElevated)
        {
            ShowUnelevated();
            return;
        }

        await ReadAsync();
    }

    private void OnGoToSetup(object sender, RoutedEventArgs e) => MainWindow.Current?.Open(typeof(SetupPage));

    // Unelevated, the page can tell only that config.json is absent. Anything else needs the elevated plan.
    private void ShowUnelevated()
    {
        var nothing = ConfigFile.KnownMissing();
        ShowState(nothing);
        AdminCard.Visibility = nothing ? Visibility.Collapsed : Visibility.Visible;
    }

    // "Nothing to uninstall" replaces the intro, the summary, the decisions, the button and the details. The progress
    // of an Uninstall that just ran stays reachable under Show details.
    private void ShowState(bool nothing)
    {
        if (_nothingShown != nothing)
        {
            _nothingShown = nothing;
            AppLog.Write("uninstall", nothing ? "Nothing installed: the page says there is nothing to uninstall" : "Showing the plan");
        }

        var normal = nothing ? Visibility.Collapsed : Visibility.Visible;
        IntroBar.Visibility = normal;
        NothingCard.Visibility = nothing ? Visibility.Visible : Visibility.Collapsed;
        SummaryCard.Visibility = normal;
        ApplyCard.Visibility = normal;
        if (nothing)
        {
            DecisionsCard.Visibility = Visibility.Collapsed;
        }

        DetailsExpander.Visibility = nothing && _steps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // False when the reading failed, so the plan on screen is not from it.
    private async Task<bool> ReadAsync()
    {
        if (_reading)
        {
            return false;
        }

        _reading = true;
        _readAt = WindowsChanges.Count;
        var ok = false;
        RefreshButton.IsEnabled = false;
        NothingRefreshButton.IsEnabled = false;
        var first = _plan is null;
        PlanLoading.Visibility = first ? Visibility.Visible : Visibility.Collapsed;
        SummaryBody.Visibility = first ? Visibility.Collapsed : Visibility.Visible;
        try
        {
            var (input, bytes, documents) = await Task.Run(ReadInput);
            _input = input;
            _configBytes = bytes;
            _documents = documents;
            Recompute();
            ok = true;
        }
        catch (Exception ex) when (State.IsExpected(ex) || ex is ConfigFormatException)
        {
            AppLog.Exception("uninstall", ex);
            Show(ApplyResult, InfoBarSeverity.Error, Loc.Instance["Setup_ReadFailed"], UiErrors.ForUser(ex));
        }
        finally
        {
            _reading = false;
            RefreshButton.IsEnabled = true;
            NothingRefreshButton.IsEnabled = true;
            PlanLoading.Visibility = Visibility.Collapsed;
            SummaryBody.Visibility = Visibility.Visible;
            UpdateApplyState();
        }

        return ok;
    }

    private static (UninstallInput Input, byte[]? Bytes, string? Documents) ReadInput()
    {
        var path = StateReader.ConfigPath;
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var config = bytes is null ? null : ConfigReader.Parse(bytes, Encoding.Default);
        var desktop = DesktopUser.Current;
        var snapshot = PlanReader.ReadStatic(new LetterScan(), desktop.Sid);
        snapshot = PlanReader.ReadAnswerParts(snapshot, config?.SmbUser ?? "", config?.SharePath ?? "", config?.ShareName ?? "");

        // The File Explorer entry lives in the hive of the user it was made for, who may not be the desktop user now.
        if (config?.TrackedExplorerEntry?.Sid is { } entrySid && !string.Equals(entrySid, desktop.Sid, StringComparison.OrdinalIgnoreCase))
        {
            snapshot = snapshot with { ExplorerEntry = PlanReader.ExplorerEntryFor(entrySid) };
        }

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
            AutoPrivateNames = PlanReader.AutoPrivateNames(),
            ProfilePath = profile,
            ProtectedSids = protectedSids,
            DesktopUser = desktopName,
            ProtectedFolders = PlanReader.ProtectedFolders(),
        };
        return (input, bytes, desktop.Documents);
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
        ApplyUi.FillSummary(SummaryBody, _summary);
        var decisions = SetupPage.AskViews(_plan);
        Decisions.ItemsSource = decisions;
        DecisionsCard.Visibility = decisions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowState(UninstallPlanner.NothingInstalled(_plan));
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
        else if (_checking)
        {
            hint = loc["Apply_Checking"];
        }
        else if (ApplyList.Blocking(_plan).Count > 0)
        {
            // The summary says what keeps the button off.
            hint = "";
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
        RestorePointBox.IsEnabled = !_applying && !_checking && _plan is not null;
        ApplyBusy.Visibility = _applying || _checking ? Visibility.Visible : Visibility.Collapsed;
        ApplyHint.Text = hint;
        ApplyHint.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetHelpText(ApplyButton, hint);
    }

    private void OnPlanTickChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string key } box && box.IsChecked is { } ticked && !_applying && !_checking)
        {
            _choices[key] = ticked;
            Recompute();
        }
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_applying || _reading || _plan is null || _input is null)
        {
            return;
        }

        var loc = Loc.Instance;
        if (_staleShown)
        {
            ApplyResult.IsOpen = false;
            _staleShown = false;
        }

        // Windows may have changed since the page read it (a Setup from this app, a change outside CPFS), so
        // everything is read and planned again before anything is confirmed.
        var shown = _plan;
        bool fresh;
        _checking = true;
        UpdateApplyState();
        try
        {
            fresh = await ReadAsync();
        }
        finally
        {
            _checking = false;
            UpdateApplyState();
        }

        var plan = _plan;
        if (!fresh || plan is null || _input is null || ApplyList.Blocking(plan).Count > 0 || !ApplyList.Uninstall(plan).Any(x => !x.Deferred))
        {
            AppLog.Write("uninstall", "Not confirmed, " + (fresh ? "the new plan has nothing to undo or is blocked" : "the state could not be read again"));
            return;
        }

        if (!shown.SameItemsAs(plan))
        {
            AppLog.Write("uninstall", "The plan changed when Windows was read again; the summary is shown again instead of the confirmation: "
                + string.Join(", ", shown.DifferencesFrom(plan)));
            Show(ApplyResult, InfoBarSeverity.Warning, loc["Apply_StaleTitle"], loc["Uninst_StaleMsg"]);
            _staleShown = true;
            return;
        }

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
        _staleShown = false;
        ResultActions.Visibility = Visibility.Collapsed;
        RestartButton.Visibility = Visibility.Collapsed;
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
            var result = await Task.Run(() => run.Execute(r => dispatcher.BeginInvoke(new Action(() => views[r.Index].Set(r)))));
            foreach (var r in result.Reports)
            {
                views[r.Index].Set(r);
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

                if (run.RenamedTo is { } renamed)
                {
                    notes.Add(loc.FormatValues("Uninst_RenameMsg", renamed));
                }

                if (_noNewRestorePoint)
                {
                    notes.Add(loc["Apply_NoRestorePointNote"]);
                }

                Show(ApplyResult, severity, loc["Uninst_DoneTitle"], string.Join(Environment.NewLine, notes));
                RestartButton.Visibility = run.RenamedTo is not null ? Visibility.Visible : Visibility.Collapsed;
                ResultActions.Visibility = result.NotDone > 0 || run.RenamedTo is not null ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                var failed = entries.First(x => x.Item.Key == result.FailedKey).Item;

                // Windows' words come last, on a line of their own.
                var message = loc.FormatValues("Uninst_FailedMsg", UiErrors.Of(result.Error, result.ErrorText));
                if (run.SavedCopy is not null)
                {
                    message = loc.FormatValues("Uninst_CopySaved", run.SavedCopy) + Environment.NewLine + message;
                }

                Show(ApplyResult, InfoBarSeverity.Error, loc.FormatValues("Uninst_FailedTitle", SetupPage.ToView(failed).Subject), message);
                RestartButton.Visibility = run.RenamedTo is not null ? Visibility.Visible : Visibility.Collapsed;
                ResultActions.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("uninstall", ex);
            Show(ApplyResult, InfoBarSeverity.Error, loc["Apply_ErrorTitle"], UiErrors.ForUser(ex));
            ResultActions.Visibility = Visibility.Visible;
        }
        finally
        {
            _applying = false;
            _choices.Clear();
            WindowsChanges.Bump("Uninstall");
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

    private async void OnRestart(object sender, RoutedEventArgs e)
    {
        if (await ApplyUi.RestartAsync(this) is { } error)
        {
            Show(ApplyResult, InfoBarSeverity.Error, Loc.Instance["Restart_FailedTitle"], error);
        }
    }

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
