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
            hint = loc["Apply_NeedsAdmin"];
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
            hint = loc["Apply_Nothing"];
        }
        else
        {
            hint = loc["Apply_Ready"];
            enabled = true;
        }

        ApplyButton.IsEnabled = enabled;
        RestorePointBox.IsEnabled = Startup.IsElevated && !_applying;
        ApplyHint.Text = hint;
        System.Windows.Automation.AutomationProperties.SetHelpText(ApplyButton, hint);
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
        if (!await ApplyUi.ConfirmAsync(this, "Apply_ConfirmTitle", "Apply_ConfirmMsg", "Apply_ConfirmButton", entries, restorePoint))
        {
            AppLog.Write("apply", "Setup: the user cancelled at the summary");
            return;
        }

        _applying = true;
        UpdateApplyState();
        RefreshButton.IsEnabled = false;
        ApplyResult.IsOpen = false;
        _steps.Clear();
        ProgressCard.Visibility = Visibility.Visible;

        // A copy of what the box holds; it is read only by the account steps and disposed below.
        SecureString? password = answers.Password == PasswordEntry.Valid ? PasswordBox1.SecurePassword : null;
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
                var (message, severity) = ApplyUi.DoneMessage(result);
                if (run.RestartNeeded)
                {
                    message += " " + loc["Apply_RestartMsg"];
                }

                Show(ApplyResult, severity, loc["Apply_DoneTitle"], message);
            }
            else
            {
                var failed = entries.First(x => x.Item.Key == result.FailedKey).Item;
                Show(ApplyResult, InfoBarSeverity.Error, loc.FormatValues("Apply_FailedTitle", ToView(failed).Subject),
                    loc.FormatValues("Apply_FailedMsg", result.Error));
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("apply", ex);
            Show(ApplyResult, InfoBarSeverity.Error, loc["Apply_ErrorTitle"], ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            password?.Dispose();
            PasswordBox1.Clear();
            PasswordBox2.Clear();
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
        return await ApplyUi.ContinueWithoutRestorePointAsync(this, result);
    }
}
