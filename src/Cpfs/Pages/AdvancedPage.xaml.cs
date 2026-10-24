using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using Cpfs.Core.Layout;
using Cpfs.Core.Text;
using Cpfs.Windows;
using Wpf.Ui.Appearance;
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

// The Advanced page: the "Always show details" switch, this PC as CPFS reads it (read-only, collapsible sections),
// the hand-over from the unelevated instance, the diagnostics and the log. It never changes Windows.
public partial class AdvancedPage : Page
{
    // Open and closed sections are remembered for as long as the app runs.
    private static readonly SectionExpansion Expansion = new();

    private readonly StateSection?[] _sections = new StateSection?[StateReader.SectionIds.Count];
    private readonly ObservableCollection<SectionView> _views = new();
    private bool _loaded;
    private bool _reading;

    // WindowsChanges.Count when the last reading began.
    private int _readAt = -1;

    public AdvancedPage()
    {
        InitializeComponent();
        Sections.ItemsSource = _views;
        Loaded += OnLoaded;
        Theming.ThemeApplied += (_, _) => RenderDiagnostics();
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Loc.Culture))
            {
                Render();
                RenderDiagnostics();
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RenderDiagnostics();
        if (_loaded)
        {
            if (_readAt != WindowsChanges.Count)
            {
                await ReadAsync();
            }

            return;
        }

        _loaded = true;
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
        _readAt = WindowsChanges.Count;
        RefreshButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        BusyText.Visibility = Visibility.Visible;
        Render();
        try
        {
            var dispatcher = Dispatcher;
            var letters = Task.Run(PcContext.ReadLetters);
            var reader = new StateReader();
            var sections = Task.Run(() => reader.ReadAllAsync(letters, (index, section) =>
                dispatcher.BeginInvoke(new Action(() =>
                {
                    _sections[index] = section;
                    RenderSection(index);
                }))));

            await letters;
            Render();
            await sections;
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            AppLog.Exception("advanced", ex);
            SetupPage.Show(ReadResult, InfoBarSeverity.Error, Loc.Instance["Setup_ReadFailed"], ex.Message);
        }
        finally
        {
            _reading = false;
            RefreshButton.IsEnabled = true;
            Busy.Visibility = Visibility.Collapsed;
            BusyText.Visibility = Visibility.Collapsed;
        }
    }

    private void Render()
    {
        if (PcContext.HandOverRows is { } handOver)
        {
            HandOverCard.Visibility = Visibility.Visible;
            var rows = new List<RowView>();
            foreach (var row in handOver)
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

    private void RenderDiagnostics()
    {
        var loc = Loc.Instance;
        VersionValue.Text = AboutPage.Version;
        WindowsValue.Text = AppLog.WindowsBuild() + "; " + Environment.OSVersion.Version;
        FrameworkValue.Text = AppLog.FrameworkRelease();
        BackdropValue.Text = MainWindow.Current?.DescribeBackdrop() ?? "?";
        ThemeValue.Text = Theming.Choice + " -> " + ApplicationThemeManager.GetAppTheme() + " (Windows " + ApplicationThemeManager.GetSystemTheme() + ")";
        FontValue.Text = Theming.Describe();
        ElevatedValue.Text = Startup.IsElevated ? loc["Common_Yes"] : loc["Common_No"];

        var exe = typeof(AdvancedPage).Assembly.Location;
        long size = 0;
        var modified = DateTime.MinValue;
        try
        {
            var info = new FileInfo(exe);
            size = info.Length;
            modified = info.LastWriteTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("about", "Exe size: " + ex.Message);
        }

        ExeValue.Text = exe;
        ExeFileValue.Text = loc.Format("About_ExeFile_Value", Formats.Count(size, loc.Culture), Formats.DateTime(modified, loc.Culture));
        LibrariesValue.Text = string.Join(", ", Program.EmbeddedLibraries()) + Environment.NewLine
            + loc["About_Libraries_Loaded"] + " " + string.Join(", ", Program.LoadedFromBytes());

        LogPathValue.Text = AppLog.FilePath ?? loc["Log_None"];
        var logSize = AppLog.FileSize();
        var sizeText = logSize is { } b ? loc.Format("About_LogSize_Value", Formats.Count(b, loc.Culture)) : loc["State_Unknown"];
        LogSizeValue.Text = AppLog.FailedWrites == 0
            ? sizeText
            : loc.Format("About_LogSize_Failures", sizeText, AppLog.FailedWrites, AppLog.LastWriteError ?? "?");
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        var path = AppLog.FilePath;
        if (path is null)
        {
            ShowLogResult(InfoBarSeverity.Error, Loc.Instance["Log_None"]);
            return;
        }

        try
        {
            using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }))
            {
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Write("log", "Open log failed: " + ex.Message);
            ShowLogResult(InfoBarSeverity.Error, Loc.Instance.Format("Log_OpenFailed", ex.Message));
        }
    }

    private void OnCopyVersion(object sender, RoutedEventArgs e) => Feedback.Copy(sender, "version", AboutPage.Version);

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        if (ApplyUi.CopyReport(sender, out var error))
        {
            LogResult.IsOpen = false;
            RenderDiagnostics();
        }
        else
        {
            ShowLogResult(InfoBarSeverity.Error, error ?? "");
        }
    }

    private void ShowLogResult(InfoBarSeverity severity, string message)
    {
        LogResult.Severity = severity;
        LogResult.Title = "";
        LogResult.Message = message;
        LogResult.IsOpen = true;
    }
}
