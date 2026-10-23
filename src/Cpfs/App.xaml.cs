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

    // Log the full exception and show it, so the owner can send the text back instead of seeing the app vanish.
    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Exception("unhandled", e.Exception);
        MessageBox.Show(e.Exception.ToString(), Loc.Instance["App_ErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
