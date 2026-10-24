using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cpfs.Core.Drives;
using Cpfs.Windows;
using Wpf.Ui.Controls;

namespace Cpfs.Pages;

// The Setup page: a few fields, Your decisions, one summary sentence and one button, with the full plan under Show
// details (SetupPage.Plan.cs) and Apply (SetupPage.Apply.cs). The PC state as CPFS reads it is on the Advanced page.
// Windows changes only through Apply, after the user confirmed.
public partial class SetupPage : Page
{
    private bool _loaded;
    private bool _reading;

    public SetupPage()
    {
        InitializeComponent();
        InitPlan();
        InitApply();
        ApplyUi.AttachDetails(DetailsExpander);
        ApplyUi.AttachKeys(MoreOptions);
        Loaded += OnLoaded;
        Theming.ThemeApplied += (_, _) => RenderBrand();
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Loc.Culture))
            {
                RenderAdminCard();
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
        RenderBrand();
        await ReadAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReadAsync();

    // config.json, the drive letters and the plan's state are read off the UI thread.
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
        SetLoading(_plan is null);
        try
        {
            var reader = new StateReader();
            var (section, letters) = await Task.Run(() => (reader.ReadConfigSection(), PcContext.ReadLetters()));
            if (section.Status == ReadStatus.Failed)
            {
                Show(AdminResult, InfoBarSeverity.Error, Loc.Instance["Setup_ConfigFailed"], section.Error ?? "");
            }

            RenderAdminCard();
            await PreparePlanAsync(reader.Config, letters);
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
            SetLoading(false);
        }
    }

    // Until the first plan is ready: the ring in the summary card, the form off, and no summary sentence yet.
    private void SetLoading(bool loading)
    {
        PlanLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        SummaryBody.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        FormArea.IsEnabled = !loading;
    }

    // The admin card shows only until the page runs elevated; a forged hand-over is always reported here.
    private void RenderAdminCard()
    {
        var loc = Loc.Instance;
        AdminCard.Visibility = Startup.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        if (Startup.IsElevated && PcContext.HandOverRows is not null && PcContext.IdentityMismatch)
        {
            Show(AdminResult, InfoBarSeverity.Error, loc["Setup_HandOver_MismatchTitle"], loc["Setup_HandOver_MismatchMsg"]);
        }
    }

    private void RenderBrand() =>
        WelcomeSymbol.Source = (ImageSource)FindResource(Theming.IsDarkSurface() ? "CpfsSymbolOnDark" : "CpfsSymbolOnLight");

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

    internal static void Show(InfoBar bar, InfoBarSeverity severity, string title, string message)
    {
        bar.Severity = severity;
        bar.Title = title;
        bar.Message = message;
        bar.IsOpen = true;
    }
}
