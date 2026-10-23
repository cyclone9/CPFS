using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Controls = System.Windows.Controls;

namespace Cpfs;

public partial class MainWindow : FluentWindow
{
    private const int DwmwaSystemBackdropType = 38;

    public static MainWindow? Current { get; private set; }

    // Null until the first frame has been rendered.
    public static TimeSpan? StartToFirstFrame { get; private set; }

    public MainWindow()
    {
        Current = this;

        var language = Loc.StartLanguage(Startup.Preferences.Language);
        Loc.Instance.SetCulture(CultureInfo.GetCultureInfo(language));
        AppLog.Write("language", "Start language " + language + " (saved " + (Startup.Preferences.Language ?? "none")
            + ", Windows UI language " + CultureInfo.InstalledUICulture.Name + ")");
        Theming.LoadPreferences();

        InitializeComponent();
        UpdateTitle();
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Loc.Culture))
            {
                UpdateTitle();
            }
        };

        Theming.InstallFontResources(this);
        ApplyFlowDirection();

        ContentRendered += OnFirstRender;
        Loaded += (_, _) =>
        {
            Theming.Init(this);
            Theming.ApplyFont();
            RootNavigation.Navigate(StartPage());
        };
    }

    // "CPFS 0.5.0-dev3", without the commit (About shows that).
    private void UpdateTitle()
    {
        var title = Loc.Instance.FormatValues(Startup.IsElevated ? "App_TitleAdminVersion" : "App_TitleVersion", AppLog.VersionNumber());
        Title = title;
        AppTitleBar.Title = title;
    }

    private static Type StartPage() => Startup.IsHandOver ? Startup.Arguments.Page switch
    {
        Core.Elevation.Pages.Monitor => typeof(Pages.MonitorPage),
        Core.Elevation.Pages.Uninstall => typeof(Pages.UninstallPage),
        _ => typeof(Pages.SetupPage),
    }
    : typeof(Pages.SetupPage);

    public void ApplyFlowDirection()
    {
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Instance.Culture.IetfLanguageTag);
        FlowDirection = Loc.Instance.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Theming.ApplyFont();
    }

    public void SetLanguage(CultureInfo culture)
    {
        var old = Loc.Instance.Culture;
        if (old.Name == culture.Name)
        {
            return;
        }

        Loc.Instance.SetCulture(culture);
        Startup.Preferences.Language = culture.TwoLetterISOLanguageName;
        Startup.SavePreferences();
        AppLog.Write("language", old.Name + " -> " + culture.Name + "; translated " + Loc.Instance.IsTranslated(culture)
            + ", right to left " + culture.TextInfo.IsRightToLeft + ", font " + Theming.CurrentFont);
        ApplyFlowDirection();
    }

    // Pages are hosted in the NavigationView's own ScrollViewer (WPF-UI wraps every Page whose CanContentScroll is
    // true, the default it sets for Page). Controls on a page swallow the wheel before it gets there: a TextBox's inner
    // ScrollViewer marks it handled even with nothing to scroll, and a focused closed ComboBox changes its selection.
    // So the wheel is routed here: an inner scroller that can still move in that direction keeps it, otherwise the
    // page scrolls. Handling the preview event stops WPF from raising MouseWheel on the controls at all.
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var seenPage = false;
        for (var d = source; d is not null; d = ParentOf(d))
        {
            if (d is Visual visual && !ReferenceEquals(PresentationSource.FromVisual(visual), PresentationSource.FromVisual(this)))
            {
                // An open drop-down is a Popup with its own window; it scrolls its own list.
                return;
            }

            if (d is Controls.ComboBox { IsDropDownOpen: true })
            {
                return;
            }

            if (d is Controls.Page)
            {
                seenPage = true;
            }
            else if (d is Controls.ScrollViewer viewer)
            {
                if (!seenPage)
                {
                    if (CanScroll(viewer, e.Delta))
                    {
                        return;
                    }

                    continue;
                }

                e.Handled = true;
                viewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = viewer });
                return;
            }
        }
    }

    private static bool CanScroll(Controls.ScrollViewer viewer, int delta) =>
        delta > 0 ? viewer.VerticalOffset > 0.5 : viewer.VerticalOffset < viewer.ScrollableHeight - 0.5;

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    public string DescribeBackdrop()
    {
        var loc = Loc.Instance;
        var requested = WindowBackdropType;
        string decided;
        if (ApplicationThemeManager.GetAppTheme() == ApplicationTheme.HighContrast)
        {
            decided = loc["About_Backdrop_HighContrast"];
        }
        else if (WindowBackdrop.IsSupported(requested))
        {
            decided = requested.ToString();
        }
        else
        {
            decided = loc["About_Backdrop_Solid"];
        }

        // DWMWA_SYSTEMBACKDROP_TYPE exists from build 22621; older builds return an error HRESULT.
        var hwnd = new WindowInteropHelper(this).Handle;
        var dwm = Windows.Native.DwmGetWindowAttribute(hwnd, DwmwaSystemBackdropType, out var value, sizeof(int)) == 0
            ? value.ToString(CultureInfo.InvariantCulture)
            : "-";
        return loc.Format("About_Backdrop_Value", requested, decided, dwm);
    }

    private void OnFirstRender(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstRender;
        try
        {
            StartToFirstFrame = DateTime.Now - Process.GetCurrentProcess().StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StartToFirstFrame = null;
        }

        AppLog.Write("start", "First frame " + (StartToFirstFrame?.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture) ?? "?")
            + " ms from process start, " + Program.SinceMain.Elapsed.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture) + " ms from Main");
        AppLog.Write("backdrop", DescribeBackdrop());
    }
}
