using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Cpfs.Core.Apply;
using Cpfs.Core.Planning;
using Cpfs.Core.Text;
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
        StateKind = outcome.ToString();
        if (outcome == StepOutcome.Unsupported && SessionDrive.NoDesktopUserText(error) is { } text)
        {
            State = Loc.Instance["Apply_State_NoSession"];
            Error = Loc.Instance[text];
            return;
        }

        State = Loc.Instance["Apply_State_" + outcome];
        Error = outcome == StepOutcome.Unsupported && error is not null ? Loc.Instance.FormatValues("Apply_RefusedReason", error) : error ?? "";
    }

    // A restore point Windows declined without an error (its 24-hour rule): a warning, not a failure.
    public void SetSkipped()
    {
        State = Loc.Instance["Apply_State_Skipped"];
        StateKind = "Skipped";
        Error = "";
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

// What Setup and Uninstall share: the summary sentence, the confirmation, the question after a restore point that was
// not created, the result, Copy report and the behaviour of every Show details.
internal static class ApplyUi
{
    // The summary as a bulleted list, one line per sentence in the UI language: counts formatted for it, names and paths
    // kept in their own direction and in bold. The translated sentence stays whole; only its placeholders become runs.
    public static void FillSummary(Panel panel, PlanSummary? summary)
    {
        panel.Children.Clear();
        if (summary is null)
        {
            return;
        }

        var loc = Loc.Instance;
        var body = panel.TryFindResource("Body") as Style;
        foreach (var line in summary.Lines)
        {
            var text = new System.Windows.Controls.TextBlock { Style = body, TextWrapping = TextWrapping.Wrap };
            var format = loc[line.Key];
            var at = 0;
            foreach (System.Text.RegularExpressions.Match m in Placeholder.Matches(format))
            {
                if (m.Index > at)
                {
                    text.Inlines.Add(new System.Windows.Documents.Run(format.Substring(at, m.Index - at)));
                }

                var i = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var arg = i < line.Args.Count ? line.Args[i] : null;
                text.Inlines.Add(arg switch
                {
                    int n => new System.Windows.Documents.Run(Formats(n)),
                    null => new System.Windows.Documents.Run(m.Value),
                    var value => new System.Windows.Documents.Bold(new System.Windows.Documents.Run(Bidi.Embed(value.ToString() ?? "", loc.IsRtl))),
                });
                at = m.Index + m.Length;
            }

            if (at < format.Length)
            {
                text.Inlines.Add(new System.Windows.Documents.Run(format.Substring(at)));
            }

            panel.Children.Add(ListLine(text, body));
        }
    }

    private static readonly System.Text.RegularExpressions.Regex Placeholder = new(@"\{(\d+)\}");

    // Three lines, how many changes, how they are undone and whether a restore point comes first, with the item list
    // under Show details.
    public static async Task<bool> ConfirmAsync(FrameworkElement page, string titleKey, string messageKey, string undoKey, string buttonKey,
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
            list.Children.Add(ListLine(view.Detail.Length > 0 ? loc.Format("Apply_ConfirmLine", view.Subject, view.Detail) : view.Subject));
        }

        var details = new StackPanel();
        if (deferred > 0)
        {
            details.Children.Add(Text(loc.Format("Apply_ConfirmDeferred", Formats(deferred))));
        }

        // The list scrolls inside the expander, so the dialog stays within the screen and its buttons stay visible.
        details.Children.Add(new ScrollViewer
        {
            Content = list,
            MaxHeight = Math.Max(120, Math.Min(280, SystemParameters.WorkArea.Height - 400)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        // Opening or closing it never closes the dialog: no button is the default, and Enter and Space on its header
        // only toggle it (AttachKeys).
        var expander = new CardExpander
        {
            Header = loc["Common_ShowDetails"],
            Content = details,
            IsExpanded = false,
            Margin = new Thickness(0, 0, 0, 8),
        };
        System.Windows.Automation.AutomationProperties.SetName(expander, loc["Common_ShowDetails"]);
        AttachKeys(expander);

        var content = new StackPanel { MaxWidth = 520 };
        content.Children.Add(Text(loc.Format(messageKey + (run == 1 ? "One" : ""), Formats(run))));
        content.Children.Add(Text(loc[undoKey]));
        content.Children.Add(Text(loc[restorePoint ? "Apply_ConfirmRestorePoint" : "Apply_ConfirmNoRestorePoint"]));
        content.Children.Add(expander);

        // Our own buttons, not the message box's template buttons, so only a press of the primary button confirms; no
        // button is the default, so Enter on the expander confirms nothing.
        var box = NewChoice(page, loc[titleKey], content);
        box.AddButton(loc[buttonKey], Wpf.Ui.Controls.MessageBoxButton.Primary, ControlAppearance.Primary);
        box.AddButton(loc["Common_Cancel"], Wpf.Ui.Controls.MessageBoxButton.Close, ControlAppearance.Secondary, isCancel: true);
        var toggles = 0;
        void OnToggle(object sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, expander))
            {
                return;
            }

            toggles++;
            box.Dispatcher.BeginInvoke(new Action(() => FitHeight(box)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        expander.Expanded += OnToggle;
        expander.Collapsed += OnToggle;

        return DialogCloses.Confirms(await box.ShowLoggedAsync("apply", titleKey,
            () => "; details toggled " + toggles.ToString(CultureInfo.InvariantCulture) + " times, " + (expander.IsExpanded ? "open" : "closed") + " at close"));
    }

    // One item of a list: the dash in its own column, so a long line wraps under the text, not under the dash. The
    // grid mirrors with the dialog, which puts the dash on the right in a right-to-left language.
    private static Grid ListLine(string text) =>
        ListLine(new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, null);

    // The bullet in its own column, so a wrapped line hangs under the text, on the right in right-to-left.
    private static Grid ListLine(System.Windows.Controls.TextBlock body, Style? style)
    {
        var line = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.Children.Add(new System.Windows.Controls.TextBlock { Text = Loc.Instance["Common_ListBullet"], Margin = new Thickness(0, 0, 6, 0), Style = style });
        Grid.SetColumn(body, 1);
        line.Children.Add(body);
        return line;
    }

    public static ChoiceDialog NewChoice(FrameworkElement page, string title, object content)
    {
        var owner = Window.GetWindow(page);
        var box = new ChoiceDialog(content)
        {
            Title = title,
            MaxWidth = 560,
            FlowDirection = owner?.FlowDirection ?? FlowDirection.LeftToRight,
            Owner = owner,
        };
        box.SetResourceReference(Control.FontFamilyProperty, Theming.FontResourceKey);
        return box;
    }

    // True to go on without a new restore point. "Open System Protection" opens its settings and stops.
    public static async Task<bool> ContinueWithoutRestorePointAsync(FrameworkElement page, RestorePointResult result)
    {
        var loc = Loc.Instance;
        var message = result.Outcome switch
        {
            RestorePointOutcome.Skipped when result.Newest is { } newest => loc.Format("Apply_RpSkippedMsg", Cpfs.Core.Text.Formats.DateTime(newest.LocalDateTime, loc.Culture)),
            RestorePointOutcome.Unavailable => loc.FormatValues("Apply_RpUnavailableMsg", result.Error ?? "?"),
            _ => loc["Apply_RpNoneMsg"],
        };
        // No default button: Enter alone must not go on without a restore point.
        var box = NewChoice(page, loc["Apply_RpTitle"], Text(message));
        box.AddButton(loc["Apply_RpContinue"], Wpf.Ui.Controls.MessageBoxButton.Primary, ControlAppearance.Primary);
        box.AddButton(loc["Apply_RpOpenSettings"], Wpf.Ui.Controls.MessageBoxButton.Secondary, ControlAppearance.Secondary);
        box.AddButton(loc["Apply_RpStop"], Wpf.Ui.Controls.MessageBoxButton.Close, ControlAppearance.Secondary, isCancel: true);
        var answer = await box.ShowLoggedAsync("apply", "Apply_RpTitle");
        if (answer == DialogClose.SecondaryButton)
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
        return DialogCloses.Confirms(answer);
    }

    // After a run that finished: how many changes were made as the title; then, by name and with Windows' words, each
    // change Windows refused or did only in part. Those are not done, and never counted as made.
    public static (string Title, string Message, InfoBarSeverity Severity) DoneMessage(ExecutionResult result, IReadOnlyList<ApplyEntry> entries)
    {
        var loc = Loc.Instance;
        var done = result.Count(StepOutcome.Done);
        var title = done == 0 ? loc["Apply_DoneNone"] : loc.Format(done == 1 ? "Apply_DoneOne" : "Apply_DoneMany", Formats(done));
        var notes = new List<string>();
        foreach (var r in result.Reports)
        {
            if (r.Outcome == StepOutcome.Unsupported && SessionDrive.NoDesktopUserText(r.Error) is { } text)
            {
                notes.Add(loc[text]);
            }
            else if (r.Outcome is StepOutcome.Unsupported or StepOutcome.Partial && r.Index >= 0 && r.Index < entries.Count)
            {
                var subject = SetupPage.ToView(entries[r.Index].Item).Subject;
                // Windows' message, possibly in another language than the app's, goes on a line of its own.
                notes.Add(loc.Format(r.Outcome == StepOutcome.Unsupported ? "Apply_DoneRefusedItem" : "Apply_DonePartialItem", subject)
                    + Environment.NewLine + Bidi.Embed(r.Error ?? "?", loc.IsRtl));
            }
        }

        if (result.Count(StepOutcome.Deferred) is var later and > 0)
        {
            notes.Add(loc.Format("Apply_ConfirmDeferred", Formats(later)));
        }

        return (title, string.Join(Environment.NewLine, notes), result.NotDone > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    // The session log on the clipboard for support; it holds no password.
    public static bool CopyReport(object sender, out string? error)
    {
        try
        {
            AppLog.Write("log", "Report copied to the clipboard");
            Clipboard.SetText(AppLog.SessionText());
            Feedback.Show(sender, true, Loc.Instance["Common_Copied"]);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or IOException or UnauthorizedAccessException)
        {
            error = Loc.Instance.Format("Log_CopyFailed", ex.Message);
            Feedback.Show(sender, false, error);
            return false;
        }
    }

    // A Show details expander: it starts collapsed.
    public static void AttachDetails(CardExpander expander)
    {
        expander.IsExpanded = false;
        AttachKeys(expander);
    }

    // A page's own expander: named for screen readers on its toggle button, and opened or closed by Enter or Space on
    // its header, as the Advanced page's sections are.
    public static void AttachKeys(CardExpander expander)
    {
        expander.Loaded += (_, _) => NameToggle(expander);
        expander.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space && Keyboard.Modifiers == ModifierKeys.None
                && ReferenceEquals(e.OriginalSource, expander.Template?.FindName("ExpanderToggleButton", expander)))
            {
                expander.IsExpanded = !expander.IsExpanded;
                e.Handled = true;
            }
        };
    }

    // The header toggle button is the expander's one tab stop; by default it would read its content.
    public static void NameToggle(CardExpander expander)
    {
        if (expander.Template?.FindName("ExpanderToggleButton", expander) is ToggleButton toggle)
        {
            System.Windows.Automation.AutomationProperties.SetName(toggle, System.Windows.Automation.AutomationProperties.GetName(expander));
            System.Windows.Automation.AutomationProperties.SetHelpText(toggle, System.Windows.Automation.AutomationProperties.GetHelpText(expander));
        }
    }

    // WPF-UI 4.3.0's MessageBox sets its height once, when it loads. After its content grows or shrinks, the window
    // is measured again at its current width, as the library does on load, so it neither clips nor leaves a gap.
    private static void FitHeight(Window box)
    {
        if (box.IsLoaded && VisualTreeHelper.GetChildrenCount(box) > 0 && VisualTreeHelper.GetChild(box, 0) is UIElement root)
        {
            root.Measure(new Size(box.ActualWidth, double.PositiveInfinity));
            box.SetCurrentValue(FrameworkElement.HeightProperty, root.DesiredSize.Height);
        }
    }

    public static string Formats(int n) => Cpfs.Core.Text.Formats.Count(n, Loc.Instance.Culture);

    private static System.Windows.Controls.TextBlock Text(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
}
