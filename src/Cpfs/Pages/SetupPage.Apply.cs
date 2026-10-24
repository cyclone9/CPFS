using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using System.Windows;
using Cpfs.Core.Apply;
using Cpfs.Core.Planning;
using Cpfs.Windows;
using Cpfs.Windows.Apply;
using Wpf.Ui.Controls;

namespace Cpfs.Pages;

// Apply: after the user confirmed the summary, a restore point first, then the plan's changes on a worker thread,
// each step shown as it runs. Nothing here runs on page load or while planning.
public partial class SetupPage
{
    private readonly ObservableCollection<StepView> _steps = new();
    private bool _applying;
    private bool _noNewRestorePoint;

    private void InitApply() => ProgressList.ItemsSource = _steps;

    private static bool HasWork(Plan plan) => ApplyList.Setup(plan).Any(e => !e.Deferred);

    // A config.json that exists but could not be read is not a first Setup: applying would take a new backup over it.
    private bool ConfigUnreadable => _config is null && System.IO.File.Exists(StateReader.ConfigPath);

    private void UpdateApplyState()
    {
        var loc = Loc.Instance;
        var enabled = false;
        string hint;
        if (!Startup.IsElevated)
        {
            // The card at the top explains it and holds the button; the shield and its tooltip point there.
            hint = "";
        }
        else if (_applying)
        {
            hint = loc["Apply_Running"];
        }
        else if (_plan is null)
        {
            hint = loc["Setup_Reading"];
        }
        else if (ConfigUnreadable)
        {
            hint = loc["Apply_ConfigUnreadable"];
        }
        else if (ApplyList.Blocking(_plan).Count > 0)
        {
            hint = loc["Apply_Blocked"];
        }
        else if (!HasWork(_plan))
        {
            // The summary already says there is nothing to change.
            hint = "";
        }
        else
        {
            hint = loc["Apply_Ready"];
            enabled = true;
        }

        ApplyButton.IsEnabled = enabled;
        ApplyButton.Icon = Startup.IsElevated ? null : new SymbolIcon { Symbol = SymbolRegular.Shield24 };
        ApplyButton.ToolTip = Startup.IsElevated ? null : loc["Apply_ShieldTip"];
        System.Windows.Controls.ToolTipService.SetShowOnDisabled(ApplyButton, true);
        RestorePointBox.IsEnabled = Startup.IsElevated && !_applying;
        ApplyBusy.Visibility = _applying ? Visibility.Visible : Visibility.Collapsed;
        ApplyHint.Text = hint;
        ApplyHint.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetHelpText(ApplyButton, Startup.IsElevated ? hint : loc["Apply_ShieldTip"]);
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_applying || !Startup.IsElevated)
        {
            return;
        }

        var loc = Loc.Instance;
        HidePasswords();
        _planTimer.Stop();
        await RecomputeAsync();
        var plan = _plan;
        if (plan is null || ConfigUnreadable || ApplyList.Blocking(plan).Count > 0 || !HasWork(plan))
        {
            UpdateApplyState();
            return;
        }

        var answers = ReadForm();
        var entries = ApplyList.Setup(plan);
        var restorePoint = RestorePointBox.IsChecked == true;
        var first = IsFirstInstall;
        if (!await ApplyUi.ConfirmAsync(this, first ? "Setup_ConfirmFirstTitle" : "Apply_ConfirmTitle", "Apply_ConfirmMsg", "Apply_ConfirmUndo",
            first ? "Setup_Plan_SetUp" : "Apply_ConfirmButton", entries, restorePoint))
        {
            AppLog.Write("apply", "Setup: the user cancelled at the summary");
            return;
        }

        _applying = true;
        UpdateApplyState();
        RefreshButton.IsEnabled = false;
        ApplyResult.IsOpen = false;
        ShowActions(connect: false, report: false);
        _noNewRestorePoint = false;
        _steps.Clear();
        ProgressCard.Visibility = Visibility.Visible;

        // A copy of what the box holds; it is read only by the account steps and disposed below.
        SecureString? password = answers.Password == PasswordEntry.Valid ? PasswordInput.SecurePassword : null;
        password?.MakeReadOnly();
        try
        {
            AppLog.Write("apply", "Setup: Apply confirmed, " + entries.Count + " items, restore point " + restorePoint);
            if (restorePoint && !await RestorePointFirstAsync("CPFS: before Setup"))
            {
                Show(ApplyResult, InfoBarSeverity.Warning, loc["Apply_StoppedTitle"], loc["Apply_StoppedMsg"]);
                return;
            }

            var views = new List<StepView>();
            foreach (var entry in entries)
            {
                var view = StepView.For(entry.Item);
                views.Add(view);
                _steps.Add(view);
            }

            var run = new SetupRun(answers, password, plan, ConfigBook.ForSetup(_config));
            var dispatcher = Dispatcher;
            var result = await Task.Run(() => run.Execute(r => dispatcher.BeginInvoke(new Action(() => views[r.Index].Set(r.Outcome, r.Error)))));
            foreach (var r in result.Reports)
            {
                views[r.Index].Set(r.Outcome, r.Error);
            }

            if (result.Completed)
            {
                var (title, message, severity) = ApplyUi.DoneMessage(result, entries);
                var notes = new List<string>();
                if (message.Length > 0)
                {
                    notes.Add(message);
                }

                if (run.RestartNeeded)
                {
                    notes.Add(loc["Apply_RestartMsg"]);
                }

                if (run.DriveRestartNeeded)
                {
                    notes.Add(loc["Apply_RestartDriveMsg"]);
                }

                if (_noNewRestorePoint)
                {
                    notes.Add(loc["Apply_NoRestorePointNote"]);
                }

                Show(ApplyResult, severity, title, string.Join(Environment.NewLine, notes));
                ShowActions(connect: first, report: result.NotDone > 0);
            }
            else
            {
                var failed = entries.First(x => x.Item.Key == result.FailedKey).Item;
                Show(ApplyResult, InfoBarSeverity.Error, loc.FormatValues("Apply_FailedTitle", ToView(failed).Subject),
                    loc.FormatValues("Apply_FailedMsg", result.Error));
                ShowActions(connect: false, report: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("apply", ex);
            Show(ApplyResult, InfoBarSeverity.Error, loc["Apply_ErrorTitle"], ex.GetType().Name + ": " + ex.Message);
            ShowActions(connect: false, report: true);
        }
        finally
        {
            password?.Dispose();
            PasswordInput.Clear();
            _applying = false;
            await ReadAsync();
            UpdateApplyState();
        }
    }

    // True to go on: a new point exists, or the user chose to continue without one.
    private async Task<bool> RestorePointFirstAsync(string description)
    {
        var view = StepView.Named(Loc.Instance["Apply_RestorePointStep"]);
        _steps.Add(view);
        view.Set(StepOutcome.Running, null);
        var result = await Task.Run(() => RestorePoint.Create(description));
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

    // Open Connect after a first Setup; Copy report beside every failure or refused change.
    private void ShowActions(bool connect, bool report)
    {
        OpenConnectButton.Visibility = connect ? Visibility.Visible : Visibility.Collapsed;
        CopyReportButton.Visibility = report ? Visibility.Visible : Visibility.Collapsed;
        ResultActions.Visibility = connect || report ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenConnect(object sender, RoutedEventArgs e) => MainWindow.Current?.Open(typeof(ConnectPage));

    private void OnCopyReport(object sender, RoutedEventArgs e) => ApplyUi.CopyReport(sender, out _);
}
