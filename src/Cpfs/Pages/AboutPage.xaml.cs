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

    private void Refresh()
    {
        var loc = Loc.Instance;
        BrandSymbol.Source = (ImageSource)FindResource(Theming.IsDarkSurface() ? "CpfsSymbolOnDark" : "CpfsSymbolOnLight");

        VersionValue.Text = Version;
        WindowsValue.Text = AppLog.WindowsBuild() + "; " + Environment.OSVersion.Version;
        FrameworkValue.Text = AppLog.FrameworkRelease();
        BackdropValue.Text = MainWindow.Current?.DescribeBackdrop() ?? "?";
        ThemeValue.Text = Theming.Choice + " -> " + ApplicationThemeManager.GetAppTheme() + " (Windows " + ApplicationThemeManager.GetSystemTheme() + ")";
        FontValue.Text = Theming.Describe();
        ElevatedValue.Text = Startup.IsElevated ? loc["Common_Yes"] : loc["Common_No"];

        var exe = typeof(AboutPage).Assembly.Location;
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

        BidiSample.Text = loc.FormatValues("About_Bidi_Sample", @"D:\Shared", "smb://HOST-01.local/Shared", "192.168.1.10", loc["LangNative_tr"], loc["LangNative_fa"]);
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

    // "0.5.0-dev3 (3d20e71)": the version with its commit, for a bug report.
    private void OnCopyVersion(object sender, RoutedEventArgs e) => Feedback.Copy(sender, "version", Version);

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        try
        {
            AppLog.Write("log", "Report copied to the clipboard");
            Clipboard.SetText(AppLog.SessionText());
            LogResult.IsOpen = false;
            Feedback.Show(sender, true, Loc.Instance["Common_Copied"]);
            Refresh();
        }
        catch (Exception ex) when (ex is COMException or ExternalException or IOException or UnauthorizedAccessException)
        {
            ShowLogResult(InfoBarSeverity.Error, Loc.Instance.Format("Log_CopyFailed", ex.Message));
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
