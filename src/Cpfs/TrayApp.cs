using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Cpfs.Core.Planning;
using Cpfs.Core.Preferences;
using Cpfs.Core.Text;
using Cpfs.Windows;

namespace Cpfs;

// "Cpfs.exe --tray": the optional notification-area icon, its own always-unelevated process (design 2.2, B2).
// WinForms from the Framework only: no WPF, WPF-UI or Theming type may be reached from here.
internal static class TrayVerb
{
    public static int Run()
    {
        AppLog.Open();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Write("tray", "Unhandled, terminating " + e.IsTerminating + ": " + (e.ExceptionObject?.ToString() ?? "null"));
        string pid;
        using (var self = Process.GetCurrentProcess())
        {
            pid = self.Id.ToString(CultureInfo.InvariantCulture);
        }

        // With UAC switched off, or as the built-in Administrator, every process is elevated: no tray there.
        if (AppLog.IsElevated())
        {
            AppLog.Write("tray", "Refused: this process runs with administrator rights, and the tray icon runs only without them (process " + pid + ")");
            return TrayExit.Elevated;
        }

        // A Run entry that outlived the preference does nothing.
        var prefs = TrayReader.ReadPreferences(out var prefsError);
        if (prefs is not { TrayIcon: true })
        {
            AppLog.Write("tray", "Not started: the tray icon is off in Preferences" + (prefsError is null ? "" : " (" + prefsError + ")"));
            return TrayExit.Off;
        }

        Mutex mutex;
        try
        {
            mutex = new Mutex(true, TrayControl.MutexName, out var created);
            if (!created)
            {
                mutex.Dispose();
                return TrayExit.AlreadyRunning;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return TrayExit.AlreadyRunning;
        }

        using (mutex)
        {
            EventWaitHandle? stop = null;
            try
            {
                stop = new EventWaitHandle(false, EventResetMode.AutoReset, TrayControl.ExitEventName);

                // A signal left from a tray that was already on its way out is not meant for this one.
                stop.Reset();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                AppLog.Write("tray", "No exit event (" + ex.GetType().Name + ": " + ex.Message + "); switching the icon off in Preferences still ends it within a minute");
            }

            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (_, e) => AppLog.Exception("tray", e.Exception);

                // Posts to it run on this thread's message loop; reads come back through it.
                var ui = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(ui);
                using var tray = new TrayIcon(ui, prefs, stop);
                AppLog.Write("tray", "==== Tray icon started: CPFS " + AppLog.VersionText() + ", process " + pid + ", exe " + typeof(TrayVerb).Assembly.Location
                    + ", language " + Loc.Instance.Culture.Name);
                Application.Run(tray);
                return TrayExit.Done;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Win32Exception)
            {
                AppLog.Write("tray", "Stopped by an error: " + ex);
                return TrayExit.Crashed;
            }
            finally
            {
                stop?.Dispose();
                mutex.ReleaseMutex();
            }
        }
    }
}

internal sealed class TrayIcon : ApplicationContext
{
    private const int PollMs = 60 * 1000;

    private readonly SynchronizationContext _ui;
    private readonly Icon _image;
    private readonly NotifyIcon _icon;
    private readonly ContextMenu _menu;
    private readonly MenuItem _status = new() { Enabled = false };
    private readonly MenuItem _share = new() { Enabled = false, Visible = false };
    private readonly MenuItem _hint = new() { Enabled = false, Visible = false };
    private readonly MenuItem _open;
    private readonly MenuItem _folder;
    private readonly MenuItem _exit;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = PollMs };
    private readonly RegisteredWaitHandle? _stopWait;
    private TrayReading? _last;
    private bool _reading;
    private bool _closed;
    private DateTime _lastOpen;

    public TrayIcon(SynchronizationContext ui, AppPreferences prefs, EventWaitHandle? stop)
    {
        _ui = ui;
        SetLanguage(prefs.Language);

        using (var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("Cpfs.Assets.cpfs.ico")
            ?? throw new InvalidOperationException("The embedded icon Cpfs.Assets.cpfs.ico is missing"))
        {
            _image = new Icon(stream, SystemInformation.SmallIconSize);
        }

        _open = new MenuItem("", (_, _) => OpenCpfs()) { DefaultItem = true };
        _folder = new MenuItem("", (_, _) => OpenFolder()) { Visible = false };
        _exit = new MenuItem("", (_, _) => Quit("Exit in its menu"));
        _menu = new ContextMenu(new[] { _status, _share, _hint, new MenuItem("-"), _open, _folder, new MenuItem("-"), _exit });
        _menu.Popup += (_, _) => Refresh();
        _icon = new NotifyIcon { Icon = _image, ContextMenu = _menu };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenCpfs();
            }
        };

        Texts();
        _icon.Visible = true;

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        if (stop is not null)
        {
            _stopWait = ThreadPool.RegisterWaitForSingleObject(stop, (_, _) => _ui.Post(_ => Quit("asked to exit by a CPFS window"), null), null, Timeout.Infinite, true);
        }

        Refresh();
    }

    // Raised on a thread-pool thread.
    private void OnNetworkChanged(object? sender, EventArgs e) => _ui.Post(_ => Refresh(), null);

    private void Refresh()
    {
        if (_closed || _reading)
        {
            return;
        }

        _reading = true;
        Task.Run(TrayReader.Read).ContinueWith(t => _ui.Post(_ => Apply(t), null), TaskScheduler.Default);
    }

    private void Apply(Task<TrayReading> task)
    {
        _reading = false;
        if (_closed)
        {
            return;
        }

        var reading = task.Status == TaskStatus.RanToCompletion
            ? task.Result
            : new TrayReading(null, TrayState.Unknown, null, null, "read: " + task.Exception?.GetBaseException());

        if (reading.Preferences is { } prefs)
        {
            if (!prefs.TrayIcon)
            {
                Quit("switched off in Preferences");
                return;
            }

            SetLanguage(prefs.Language);
        }

        if (_last is null || _last.State != reading.State || (_last.Folder is null) != (reading.Folder is null)
            || !string.Equals(_last.Detail, reading.Detail, StringComparison.Ordinal))
        {
            var line = "State " + reading.State + ", Open the shared folder " + (reading.Folder is null ? "hidden" : "shown")
                + (reading.Detail.Length == 0 ? "" : "; not read: " + reading.Detail);
            if (_last is null)
            {
                // For the laptop test: what the tray costs, and that nothing of WPF came along.
                using var self = Process.GetCurrentProcess();
                var wpf = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "PresentationFramework" or "Wpf.Ui");
                line += "; working set " + (self.WorkingSet64 / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB, private "
                    + (self.PrivateMemorySize64 / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB, WPF loaded " + wpf;
            }

            AppLog.Write("tray", line);
        }

        _last = reading;
        Texts();
    }

    private static void SetLanguage(string? saved)
    {
        var language = Loc.StartLanguage(saved);
        if (!string.Equals(language, Loc.Instance.Culture.Name, StringComparison.OrdinalIgnoreCase))
        {
            Loc.Instance.SetCulture(CultureInfo.GetCultureInfo(language));
        }
    }

    private void Texts()
    {
        var loc = Loc.Instance;
        var state = _last?.State ?? TrayState.Reading;
        var status = loc[TrayStatus.MessageKey(state)];
        var share = TrayStatus.ShowsShare(state) && !string.IsNullOrWhiteSpace(_last?.ShareName) ? _last!.ShareName!.Trim() : null;

        _status.Text = Escape(status);
        _share.Text = share is null ? "" : Escape(share);
        _share.Visible = share is not null;
        _hint.Text = loc["Tray_Status_PublicHint"];
        _hint.Visible = state == TrayState.OnPublic;
        _open.Text = loc["Tray_OpenCpfs"];
        _folder.Text = loc["Tray_OpenFolder"];
        _folder.Visible = _last?.Folder is not null;
        _exit.Text = loc["Tray_Exit"];
        _menu.RightToLeft = loc.IsRtl ? RightToLeft.Yes : RightToLeft.No;
        _icon.Text = TrayTooltip.Fit(loc["App_Title"], status, share);
    }

    // An ampersand in a menu item marks its access key; a share name may hold one.
    private static string Escape(string text) => text.Replace("&", "&&");

    // An ordinary second start: SingleInstance brings an open window forward, an elevated one too, or becomes the
    // window. Nothing is passed to it.
    private void OpenCpfs()
    {
        // A double click is two clicks.
        if (DateTime.UtcNow - _lastOpen < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastOpen = DateTime.UtcNow;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(typeof(TrayIcon).Assembly.Location) { UseShellExecute = false });
            AppLog.Write("tray", "Open CPFS: started process " + (process?.Id.ToString(CultureInfo.InvariantCulture) ?? "?"));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Write("tray", "Open CPFS: " + ex.GetType().Name + ": " + ex.Message);
            ShowError(ex.Message);
        }
    }

    private void OpenFolder()
    {
        Task.Run(TrayReader.TrustedFolder).ContinueWith(t => _ui.Post(_ =>
        {
            var folder = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            if (folder is null)
            {
                AppLog.Write("tray", "Open the shared folder: no trusted folder any more");
                Refresh();
                return;
            }

            try
            {
                FolderOpener.Open(folder);
                AppLog.Write("tray", "Opened the shared folder in File Explorer");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                AppLog.Write("tray", "Open the shared folder: " + ex.GetType().Name + ": " + ex.Message);
                ShowError(ex.Message);
            }
        }, null), TaskScheduler.Default);
    }

    private static void ShowError(string message)
    {
        var loc = Loc.Instance;
        var options = loc.IsRtl ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0;
        MessageBox.Show(loc.Format("Tray_OpenFailed", message), loc["App_Title"], MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, options);
    }

    private void Quit(string reason)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        AppLog.Write("tray", "Exit: " + reason);
        _timer.Stop();
        _icon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            _stopWait?.Unregister(null);
            _timer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            _image.Dispose();
        }

        base.Dispose(disposing);
    }
}
