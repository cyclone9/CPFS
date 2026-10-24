using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Cpfs;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Exception("unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    // The app-wide ui:Button style must carry WPF-UI's template through its BasedOn chain; without it every button
    // falls back to the classic look (0.6.0 dev3). The log says which it is.
    internal void LogButtonStyle()
    {
        try
        {
            var hasTemplate = false;
            for (var style = TryFindResource(typeof(Wpf.Ui.Controls.Button)) as Style; style is not null && !hasTemplate; style = style.BasedOn)
            {
                foreach (var setter in style.Setters)
                {
                    hasTemplate |= setter is Setter { Property: var p } && p == System.Windows.Controls.Control.TemplateProperty;
                }
            }

            AppLog.Write("theme", "Button style carries the WPF-UI template: " + hasTemplate);
        }
        catch (System.Windows.Markup.XamlParseException ex)
        {
            AppLog.Write("theme", "Button style failed to load: " + ex.Message);
        }
    }

    // A second start set the show event: bring this window forward. Nothing else can be asked of it.
    internal static void BringToFront()
    {
        if (Current?.MainWindow is not { } window)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        var active = window.Activate();
        AppLog.Write("instance", "A second start asked this window to come to the front; activated " + active);
    }

    // Before any window exists, so the language comes from the saved preferences.
    internal static void ShowSecondStartMessage(global::Cpfs.Windows.InstanceOutcome outcome)
    {
        var key = outcome switch
        {
            global::Cpfs.Windows.InstanceOutcome.OtherIsAdmin => "App_AlreadyOpenAdmin",
            global::Cpfs.Windows.InstanceOutcome.OtherNoAnswer => "App_AlreadyOpen",
            global::Cpfs.Windows.InstanceOutcome.HandOverTimedOut => "App_HandOverTimedOut",
            _ => null,
        };
        if (key is null)
        {
            return;
        }

        var loc = Loc.Instance;
        loc.SetCulture(System.Globalization.CultureInfo.GetCultureInfo(Loc.StartLanguage(global::Cpfs.Startup.Preferences.Language)));
        var options = loc.IsRtl ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
        MessageBox.Show(loc[key], loc["App_Title"], MessageBoxButton.OK,
            outcome == global::Cpfs.Windows.InstanceOutcome.HandOverTimedOut ? MessageBoxImage.Warning : MessageBoxImage.Information, MessageBoxResult.OK, options);
    }

    // Log the full exception and show it, so the owner can send the text back instead of seeing the app vanish.
    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Exception("unhandled", e.Exception);
        MessageBox.Show(e.Exception.ToString(), Loc.Instance["App_ErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
