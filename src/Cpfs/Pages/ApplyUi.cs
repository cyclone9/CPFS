using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Cpfs.Core.Apply;
using Cpfs.Core.Planning;
using Cpfs.Windows.Apply;
using Wpf.Ui.Controls;

namespace Cpfs.Pages;

// One row of the progress list: what is being changed and how far it got.
public sealed class StepView : INotifyPropertyChanged
{
    private string _state = "";
    private string _stateKind = "";
    private string _error = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Subject { get; init; } = "";

    public bool SubjectIsData { get; init; }

    public bool SubjectIsText => !SubjectIsData;

    public string Detail { get; init; } = "";

    public bool HasDetail => Detail.Length > 0;

    public string State
    {
        get => _state;
        private set => SetField(ref _state, value, nameof(State));
    }

    // The StepOutcome name: picks the badge colour.
    public string StateKind
    {
        get => _stateKind;
        private set => SetField(ref _stateKind, value, nameof(StateKind));
    }

    public string Error
    {
        get => _error;
        private set
        {
            SetField(ref _error, value, nameof(Error));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
        }
    }

    public bool HasError => Error.Length > 0;

    public static StepView For(PlanItem item)
    {
        var view = SetupPage.ToView(item);
        var step = new StepView { Subject = view.Subject, SubjectIsData = view.SubjectIsData, Detail = view.Detail };
        step.Set(StepOutcome.Pending, null);
        return step;
    }

    public static StepView Named(string subject)
    {
        var step = new StepView { Subject = subject };
        step.Set(StepOutcome.Pending, null);
        return step;
    }

    public void Set(StepOutcome outcome, string? error)
    {
        State = Loc.Instance["Apply_State_" + outcome];
        StateKind = outcome.ToString();
        Error = outcome == StepOutcome.Unsupported && error is not null ? Loc.Instance.FormatValues("Apply_RefusedReason", error) : error ?? "";
    }

    private void SetField(ref string field, string value, string name)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

// The dialogs both Apply pages share: the summary to confirm, and the question after a restore point that was not
// created.
internal static class ApplyUi
{
    public static Wpf.Ui.Controls.MessageBox NewDialog(FrameworkElement page, string title, object content)
    {
        var owner = Window.GetWindow(page);
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            CloseButtonText = Loc.Instance["Common_Cancel"],
            FlowDirection = owner?.FlowDirection ?? FlowDirection.LeftToRight,
            Owner = owner,
        };
        box.SetResourceReference(Control.FontFamilyProperty, Theming.FontResourceKey);
        return box;
    }

    // Every change that will run, the items left for 0.6.0, and whether a restore point comes first.
    public static async Task<bool> ConfirmAsync(FrameworkElement page, string titleKey, string messageKey, string buttonKey,
        IReadOnlyList<ApplyEntry> entries, bool restorePoint)
    {
        var loc = Loc.Instance;
        var run = 0;
        var deferred = 0;
        var list = new StackPanel();
        foreach (var e in entries)
        {
            if (e.Deferred)
            {
                deferred++;
                continue;
            }

            run++;
            var view = SetupPage.ToView(e.Item);
            list.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = view.Detail.Length > 0 ? loc.Format("Apply_ConfirmLine", view.Subject, view.Detail) : view.Subject,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        var content = new StackPanel { MaxWidth = 520 };
        content.Children.Add(Text(loc.Format(messageKey, Formats(run))));
        if (deferred > 0)
        {
            content.Children.Add(Text(loc.Format("Apply_ConfirmDeferred", Formats(deferred))));
        }

        content.Children.Add(Text(loc[restorePoint ? "Apply_ConfirmRestorePoint" : "Apply_ConfirmNoRestorePoint"]));
        content.Children.Add(new ScrollViewer
        {
            Content = list,
            MaxHeight = 280,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        var box = NewDialog(page, loc[titleKey], content);
        box.PrimaryButtonText = loc[buttonKey];
        box.PrimaryButtonAppearance = ControlAppearance.Primary;
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    // True to go on without a new restore point. "Open System Protection" opens its settings and stops.
    public static async Task<bool> ContinueWithoutRestorePointAsync(FrameworkElement page, RestorePointResult result)
    {
        var loc = Loc.Instance;
        var message = result.Outcome switch
        {
            RestorePointOutcome.Skipped when result.Newest is { } newest => loc.Format("Apply_RpSkippedMsg", newest.ToString("g", loc.Culture)),
            RestorePointOutcome.Unavailable => loc.FormatValues("Apply_RpUnavailableMsg", result.Error ?? "?"),
            _ => loc["Apply_RpNoneMsg"],
        };
        var box = NewDialog(page, loc["Apply_RpTitle"], Text(message));
        box.PrimaryButtonText = loc["Apply_RpContinue"];
        box.SecondaryButtonText = loc["Apply_RpOpenSettings"];
        box.CloseButtonText = loc["Apply_RpStop"];
        var answer = await box.ShowDialogAsync();
        if (answer == Wpf.Ui.Controls.MessageBoxResult.Secondary)
        {
            try
            {
                using (Process.Start(new ProcessStartInfo("SystemPropertiesProtection.exe") { UseShellExecute = true }))
                {
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                AppLog.Write("apply", "System Protection settings: " + ex.Message);
            }
        }

        AppLog.Write("apply", "Restore point " + result.Outcome + "; the user chose " + answer);
        return answer == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    // After a run that finished: what was done, what was already so, what waits for 0.6.0, and, as a warning, what
    // Windows refused or what was done only in part. Those are not done, and never counted as planned for later.
    public static (string Message, InfoBarSeverity Severity) DoneMessage(ExecutionResult result)
    {
        var loc = Loc.Instance;
        var message = loc.Format("Apply_DoneMsg", Formats(result.Count(StepOutcome.Done)), Formats(result.Count(StepOutcome.Kept)),
            Formats(result.Count(StepOutcome.Deferred)));
        if (result.Count(StepOutcome.Unsupported) is var refused and > 0)
        {
            message += " " + loc.Format("Apply_DoneRefused", Formats(refused));
        }

        if (result.Count(StepOutcome.Partial) is var partial and > 0)
        {
            message += " " + loc.Format("Apply_DonePartial", Formats(partial));
        }

        return (message, result.NotDone > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    public static string Formats(int n) => Cpfs.Core.Text.Formats.Count(n, Loc.Instance.Culture);

    private static System.Windows.Controls.TextBlock Text(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
}
