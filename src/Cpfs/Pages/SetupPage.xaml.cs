using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using Cpfs.Core.Drives;
using Cpfs.Core.Elevation;
using Cpfs.Core.Layout;
using Cpfs.Core.Text;
using Cpfs.Windows;
using Wpf.Ui.Controls;

namespace Cpfs.Pages;

public sealed class RowView
{
    public string Label { get; init; } = "";

    public bool LabelIsData { get; init; }

    public bool LabelIsText => !LabelIsData;

    public string Value { get; init; } = "";

    public bool ValueIsData { get; init; }

    public bool ValueIsText => !ValueIsData && Value.Length > 0;

    public string Badge { get; init; } = "";

    public bool HasBadge => Badge.Length > 0;

    public string Error { get; init; } = "";

    public bool HasError => Error.Length > 0;
}

public sealed class SectionView : INotifyPropertyChanged
{
    private readonly Action<bool> _expandedChanged;
    private bool _isExpanded;

    public SectionView(bool isExpanded, Action<bool> expandedChanged)
    {
        _isExpanded = isExpanded;
        _expandedChanged = expandedChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; init; } = "";

    public string Description { get; init; } = "";

    public string Summary { get; init; } = "";

    public bool HasSummary => Summary.Length > 0;

    public string Badge { get; init; } = "";

    // Ok, Missing, NeedsAdmin or Failed: picks the badge colour.
    public string BadgeKind { get; init; } = "";

    public bool HasBadge => Badge.Length > 0;

    public string HelpText { get; init; } = "";

    public string Error { get; init; } = "";

    public bool HasError => Error.Length > 0;

    public List<RowView> Rows { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            _expandedChanged(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }
}

// The Setup page: the plan (SetupPage.Plan.cs) and Apply (SetupPage.Apply.cs), then config.json as found and the
// Windows state this process may read. Windows changes only through Apply, after the user confirmed.
public partial class SetupPage : Page
{
    // Open and closed sections are remembered for as long as the app runs.
    private static readonly SectionExpansion Expansion = new();

    private readonly StateSection?[] _sections = new StateSection?[StateReader.SectionIds.Count];
    private readonly ObservableCollection<SectionView> _views = new();
    private List<StateRow>? _handOver;
    private bool _loaded;
    private bool _reading;

    public SetupPage()
    {
        InitializeComponent();
        InitPlan();
        InitApply();
        Sections.ItemsSource = _views;
        Loaded += OnLoaded;
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Loc.Culture))
            {
                Render();
                RenderPlan();
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
        RenderAdminCard();
        await ReadAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReadAsync();

    // WMI, the registry, Task Scheduler and the account store run off the UI thread; each section comes back here as
    // soon as it is read. A refresh keeps the previous reading on screen until the new one replaces it.
    private async Task ReadAsync()
    {
        if (_reading)
        {
            return;
        }

        _reading = true;
        RefreshButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        BusyText.Visibility = Visibility.Visible;
        Render();
        try
        {
            var dispatcher = Dispatcher;
            var context = Task.Run(ReadContext);
            var letters = LettersOf(context);
            var reader = new StateReader();
            var sections = Task.Run(() => reader.ReadAllAsync(letters, (index, section) =>
                dispatcher.BeginInvoke(new Action(() =>
                {
                    _sections[index] = section;
                    RenderSection(index);
                }))));

            _handOver = (await context).HandOver;
            Render();
            await sections;
            await PreparePlanAsync(reader.Config, await letters);
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            AppLog.Exception("setup", ex);
            Show(AdminResult, InfoBarSeverity.Error, Loc.Instance["Setup_ReadFailed"], ex.Message);
        }
        finally
        {
            _reading = false;
            RefreshButton.IsEnabled = true;
            Busy.Visibility = Visibility.Collapsed;
            BusyText.Visibility = Visibility.Collapsed;
        }
    }

    private static async Task<LetterScan> LettersOf(Task<(LetterScan Letters, List<StateRow>? HandOver)> context) =>
        (await context.ConfigureAwait(false)).Letters;

    private static (LetterScan Letters, List<StateRow>? HandOver) ReadContext()
    {
        var watch = Stopwatch.StartNew();
        List<StateRow>? handOver = null;
        string? networkSid = null;
        string received = "";
        if (Startup.IsElevated)
        {
            // Elevated, the desktop user comes from explorer's token; what was passed is only compared with it.
            var user = DesktopUser.Current;
            AppLog.Write("elevation", "Desktop user: " + user.Describe());
            networkSid = user.Sid;
            handOver = BuildHandOver(user, out received);
        }

        var letters = DriveLetters.Scan(networkSid);
        if (received.Length > 0)
        {
            letters.AddAll(received, LetterSource.UserSessionReceived);
        }

        AppLog.Write("state", "Desktop user and drive letters in " + StateReader.Ms(watch));
        return (letters, handOver);
    }

    private static List<StateRow> BuildHandOver(DesktopUser user, out string acceptedLetters)
    {
        var rows = new List<StateRow>();
        var args = Startup.Arguments;
        acceptedLetters = "";
        if (!Startup.IsHandOver)
        {
            rows.Add(StateRow.Word("Setup_HandOver_State", "Setup_HandOver_NotPassed"));
            rows.Add(StateRow.Word("Setup_HandOver_Letters", "Setup_HandOver_LettersNotChecked"));
            rows.Add(StateRow.Data("Setup_HandOver_DerivedSid", user.Sid));
            return rows;
        }

        var check = Identity.Compare(args, user.Sid, user.Desktop);
        AppLog.Write("elevation", "Identity check " + check + "; passed SID " + (args.DesktopSid ?? "none") + ", derived " + (user.Sid ?? "none"));
        rows.Add(StateRow.Word("Setup_HandOver_State", "Identity_" + check));
        rows.Add(StateRow.Data("Setup_HandOver_PassedSid", args.DesktopSid));
        rows.Add(StateRow.Data("Setup_HandOver_DerivedSid", user.Sid));
        rows.Add(StateRow.Data("Setup_HandOver_PassedDesktop", args.DesktopPath));
        rows.Add(StateRow.Data("Setup_HandOver_DerivedDesktop", user.Desktop));
        if (user.Error is not null)
        {
            rows.Add(new StateRow { LabelKey = "Setup_HandOver_DerivedSid", Status = ReadStatus.Failed, Error = user.Error });
        }

        // A forged hand-over is refused whole, letters included.
        if (check == IdentityCheck.Mismatch)
        {
            rows.Add(StateRow.Word("Setup_HandOver_Letters", "Setup_HandOver_LettersRefused"));
        }
        else if (!args.LettersPassed)
        {
            rows.Add(StateRow.Word("Setup_HandOver_Letters", "Setup_HandOver_LettersNotChecked"));
        }
        else
        {
            acceptedLetters = args.Letters;
            rows.Add(StateRow.Data("Setup_HandOver_Letters", args.Letters.Length == 0 ? "-" : string.Join(" ", args.Letters.ToCharArray())));
        }

        if (args.Rejected.Count > 0)
        {
            rows.Add(StateRow.Data("Setup_HandOver_Rejected", string.Join(" ", args.Rejected)));
        }

        return rows;
    }

    private void Render()
    {
        var loc = Loc.Instance;
        RenderAdminCard();

        if (_handOver is not null)
        {
            HandOverCard.Visibility = Visibility.Visible;
            var rows = new List<RowView>();
            foreach (var row in _handOver)
            {
                rows.Add(ToView(row));
            }

            HandOverRows.ItemsSource = rows;
        }

        for (var i = 0; i < _sections.Length; i++)
        {
            RenderSection(i);
        }
    }

    private void RenderSection(int index)
    {
        var loc = Loc.Instance;
        var id = StateReader.SectionIds[index];
        var section = _sections[index];
        var status = section is null ? (ReadStatus?)null : Worst(section);
        var attention = status is ReadStatus.Failed or ReadStatus.NeedsAdmin;
        var badge = status switch
        {
            ReadStatus.Ok => loc["Badge_Ok"],
            ReadStatus.Missing => loc["Badge_NotFound"],
            ReadStatus.NeedsAdmin => loc["Badge_NeedsAdmin"],
            ReadStatus.Failed => loc["Badge_Error"],
            _ => "",
        };
        var summary = section is null ? loc["Setup_Reading"]
            : section.SummaryKey is { } key ? Resolve(key, section.SummaryArgs)
            : "";
        var view = new SectionView(Expansion.IsExpanded(id, attention), open => Expansion.Set(id, open))
        {
            Title = loc["Setup_" + id + "_Title"],
            Description = loc["Setup_" + id + "_Desc"],
            Summary = summary,
            Badge = badge,
            BadgeKind = status?.ToString() ?? "",
            HelpText = badge.Length == 0 ? summary : summary.Length == 0 ? badge : loc.Format("Setup_Section_Help", badge, summary),
            Error = section?.Status == ReadStatus.Failed ? loc.FormatValues("Setup_SectionFailed", section.Error) : "",
        };
        foreach (var row in section?.Rows ?? new List<StateRow>())
        {
            view.Rows.Add(ToView(row));
        }

        // Replacing one item re-creates only that card, so the others don't flicker.
        if (index < _views.Count)
        {
            _views[index] = view;
        }
        else
        {
            _views.Add(view);
        }
    }

    // A section needs attention when it, or any of its rows, failed or could only be read by an administrator.
    private static ReadStatus Worst(StateSection section)
    {
        var worst = section.Status;
        foreach (var row in section.Rows)
        {
            if (Rank(row.Status) > Rank(worst))
            {
                worst = row.Status;
            }
        }

        return worst;
    }

    private static int Rank(ReadStatus status) => status switch
    {
        ReadStatus.Failed => 3,
        ReadStatus.NeedsAdmin => 2,
        ReadStatus.Missing => 1,
        _ => 0,
    };

    private void OnExpandAll(object sender, RoutedEventArgs e) => SetAllExpanded(true);

    private void OnCollapseAll(object sender, RoutedEventArgs e) => SetAllExpanded(false);

    private void SetAllExpanded(bool open)
    {
        Expansion.SetAll(StateReader.SectionIds, open);
        foreach (var view in _views)
        {
            view.IsExpanded = open;
        }

        AppLog.Write("setup", open ? "Expand all" : "Collapse all");
    }

    // The header toggle button carries the section's name for screen readers; by default it would read its content.
    private void OnSectionLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CardExpander expander && expander.Template?.FindName("ExpanderToggleButton", expander) is ToggleButton toggle)
        {
            toggle.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(SectionView.Title)));
            toggle.SetBinding(AutomationProperties.HelpTextProperty, new Binding(nameof(SectionView.HelpText)));
        }
    }

    // Enter or Space on the header toggles the section, whatever the toggle button would do with Enter itself.
    private void OnSectionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && Keyboard.Modifiers == ModifierKeys.None
            && sender is CardExpander { DataContext: SectionView view } expander
            && ReferenceEquals(e.OriginalSource, expander.Template?.FindName("ExpanderToggleButton", expander)))
        {
            view.IsExpanded = !view.IsExpanded;
            e.Handled = true;
        }
    }

    private void RenderAdminCard()
    {
        var loc = Loc.Instance;
        if (Startup.IsElevated)
        {
            AdminTitle.Text = loc["Setup_Admin_ElevatedTitle"];
            AdminDescription.Text = loc["Setup_Admin_ElevatedDesc"];
            ElevateButton.Visibility = Visibility.Collapsed;
            if (_handOver is not null && Startup.IsHandOver
                && Identity.Compare(Startup.Arguments, DesktopUser.Current.Sid, DesktopUser.Current.Desktop) == IdentityCheck.Mismatch)
            {
                Show(AdminResult, InfoBarSeverity.Error, loc["Setup_HandOver_MismatchTitle"], loc["Setup_HandOver_MismatchMsg"]);
            }
        }
        else
        {
            AdminTitle.Text = loc["Setup_Admin_Title"];
            AdminDescription.Text = loc["Setup_Admin_Desc"];
        }
    }

    private static RowView ToView(StateRow row)
    {
        var loc = Loc.Instance;
        var label = row.LabelKey is not null ? loc[row.LabelKey] : row.Label ?? "";
        var badge = row.Status == ReadStatus.NeedsAdmin ? loc["Badge_NeedsAdmin"] : "";
        var error = row.Status == ReadStatus.Failed ? row.Error ?? "" : "";
        string value;
        var isData = false;
        if (row.Value is not null)
        {
            value = row.Value;
            isData = true;
        }
        else if (row.ValueKey is not null)
        {
            value = Resolve(row.ValueKey, row.ValueArgs);
        }
        else
        {
            value = "";
        }

        return new RowView
        {
            Label = label,
            LabelIsData = row.LabelKey is null,
            Value = value,
            ValueIsData = isData,
            Badge = badge,
            Error = error,
        };
    }

    // Arguments that are words are translated; data arguments keep their own direction inside the sentence.
    private static string Resolve(string key, object[] args)
    {
        var loc = Loc.Instance;
        if (args.Length == 0)
        {
            return loc[key];
        }

        var resolved = new object[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            resolved[i] = args[i] switch
            {
                LocKey word => Resolve(word.Key, word.Args),
                string s => Bidi.Embed(s, loc.IsRtl),
                IFormattable f => Bidi.Embed(f.ToString(null, CultureInfo.InvariantCulture), loc.IsRtl),
                var other => other?.ToString() ?? "",
            };
        }

        return string.Format(loc.Culture, loc[key], resolved);
    }

    private async void OnContinueAsAdministrator(object sender, RoutedEventArgs e)
    {
        ElevateButton.IsEnabled = false;
        try
        {
            // Only this session's letters are handed over; the elevated instance can't see them itself.
            var letters = await Task.Run(() => DriveLetters.SessionLetters(DriveLetters.Scan(null)));
            var result = Relaunch.Start(Core.Elevation.Pages.Setup, letters, out var error);
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
