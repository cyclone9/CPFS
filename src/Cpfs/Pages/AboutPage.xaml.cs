using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cpfs.Core.Text;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Cpfs.Pages;

public partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        HomepageText.Text = "cyclone9.ir/cpfs";
        GitHubText.Text = "github.com/cyclone9/CPFS";
        LicenceMit.Text = Embedded("Cpfs.Licenses.LICENSE.txt");
        LicenceNotices.Text = Embedded("Cpfs.Licenses.THIRD-PARTY-NOTICES.md");
        LicenceOfl.Text = Embedded("Cpfs.Licenses.Vazirmatn-OFL.txt");
        Loaded += (_, _) => Refresh();
        Theming.ThemeApplied += (_, _) => Refresh();
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Loc.Culture))
            {
                Refresh();
            }
        };
    }

    public static string Version => AppLog.VersionText();

    private static string Embedded(string name)
    {
        using var stream = typeof(AboutPage).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return "!" + name + "!";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public const string HomepageUrl = "https://cyclone9.ir/cpfs";

    public const string GitHubUrl = "https://github.com/cyclone9/CPFS";

    private string? _releaseUrl;

    private void Refresh()
    {
        BrandSymbol.Source = (ImageSource)FindResource(Theming.IsDarkSurface() ? "CpfsSymbolOnDark" : "CpfsSymbolOnLight");
        VersionValue.Text = Version;
    }

    // "0.5.0-dev3 (3d20e71)": the version with its commit, for a bug report.
    private void OnCopyVersion(object sender, RoutedEventArgs e) => Feedback.Copy(sender, "version", Version);

    private void OnOpenHomepage(object sender, RoutedEventArgs e) => OpenInBrowser(sender, HomepageUrl);

    private void OnOpenGitHub(object sender, RoutedEventArgs e) => OpenInBrowser(sender, GitHubUrl);

    private void OnOpenRelease(object sender, RoutedEventArgs e)
    {
        if (_releaseUrl is { } url && Core.Updates.UpdateCheck.IsReleasePage(url))
        {
            OpenInBrowser(sender, url);
        }
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        var loc = Loc.Instance;
        CheckUpdatesButton.IsEnabled = false;
        OpenReleaseButton.Visibility = Visibility.Collapsed;
        UpdateResultText.Text = loc["About_Update_Checking"];
        UpdateResultText.Visibility = Visibility.Visible;
        try
        {
            var result = await Windows.UpdateClient.CheckAsync("about");
            _releaseUrl = result.ReleaseUrl;
            UpdateResultText.Text = UpdateText(result);
            OpenReleaseButton.Visibility = result.Status == Core.Updates.UpdateStatus.Newer ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    public static string UpdateText(Core.Updates.UpdateResult result) => result.Status switch
    {
        Core.Updates.UpdateStatus.Latest => Loc.Instance["Update_Latest"],
        Core.Updates.UpdateStatus.Newer => Loc.Instance.FormatValues("Update_Newer", result.Version ?? "?"),
        Core.Updates.UpdateStatus.NoRelease => Loc.Instance["Update_NoRelease"],
        _ => Loc.Instance["Update_Failed"],
    };

    // Opens the page in the default browser; the app itself goes online only for the update check.
    public static void OpenInBrowser(object? sender, string url)
    {
        try
        {
            using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }))
            {
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppLog.Write("about", "Open " + url + " failed: " + ex.Message);
            if (sender is not null)
            {
                Feedback.Show(sender, false, Loc.Instance.FormatValues("About_Homepage_Failed", ex.Message));
            }
        }
    }

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        if (ApplyUi.CopyReport(sender, out var error))
        {
            LogResult.IsOpen = false;
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
