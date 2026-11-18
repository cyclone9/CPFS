using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cpfs.Core.Monitor;
using Cpfs.Core.Text;
using Cpfs.Core.Validation;
using ValidationError = Cpfs.Core.Validation.ValidationError;
using Cpfs.Windows;
using Wpf.Ui.Controls;

namespace Cpfs.Pages;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // Raises the change only when the value differs, so a refresh with the same numbers redraws nothing.
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// A row kept by its key across refreshes, so its controls stay and only changed text is redrawn.
public abstract class KeyedRow : Observable
{
    protected KeyedRow(string key)
    {
        Key = key;
    }

    internal string Key { get; }
}

public sealed class MonitorRow : KeyedRow
{
    private string _actionName = "";
    private string _moreActionName = "";
    private string _client = "";
    private string _hostName = "";
    private string _connection = "";
    private string _user = "";
    private string _dialect = "";
    private string _status = "";
    private string _statusKind = "";
    private string _files = "";
    private string _title = "";
    private string _nameActionName = "";
    private string _forgetActionName = "";
    private SessionInfo? _session;

    internal MonitorRow(string key)
        : base(key)
    {
    }

    internal SessionInfo? Session
    {
        get => _session;
        set
        {
            _session = value;
            Changed(nameof(CanDisconnect));
            Changed(nameof(CanName));
            Changed(nameof(HasActions));
        }
    }

    // DeviceIdentity.Key of the session's device, and the MAC or address it was built from.
    internal string DeviceKey { get; set; } = "";

    internal string DeviceAddress { get; set; } = "";

    // The name the owner gave the device; empty when it has none.
    public string Title
    {
        get => _title;
        set
        {
            Set(ref _title, value);
            Changed(nameof(HasTitle));
            Changed(nameof(CanName));
            Changed(nameof(CanRename));
            Changed(nameof(HasActions));
            Changed(nameof(Shown));
        }
    }

    public bool HasTitle => Title.Length > 0;

    public bool CanRename => HasTitle;

    // The device's IP address as the server sees it.
    public string Client
    {
        get => _client;
        set
        {
            Set(ref _client, value);
            Changed(nameof(Shown));
        }
    }

    // The name, or the address when it has none: the row's name for screen readers.
    public string Shown => HasTitle ? Title : Client;

    // A name found by reverse lookup; empty when none.
    public string HostName
    {
        get => _hostName;
        set
        {
            Set(ref _hostName, value);
            Changed(nameof(HasHostName));
        }
    }

    public bool HasHostName => HostName.Length > 0;

    // How it is connected and for how long.
    public string Connection { get => _connection; set => Set(ref _connection, value); }

    public string User { get => _user; set => Set(ref _user, value); }

    public string Dialect { get => _dialect; set => Set(ref _dialect, value); }

    public string Status { get => _status; set => Set(ref _status, value); }

    // The status dot's state name (Styles.xaml, StatusDot).
    public string StatusKind { get => _statusKind; set => Set(ref _statusKind, value); }

    public string Files { get => _files; set => Set(ref _files, value); }

    public string MoreActionName { get => _moreActionName; set => Set(ref _moreActionName, value); }

    public bool CanName => Session is not null && !HasTitle;

    public string NameActionName { get => _nameActionName; set => Set(ref _nameActionName, value); }

    public string ForgetActionName { get => _forgetActionName; set => Set(ref _forgetActionName, value); }

    public bool CanDisconnect => Session is not null;

    public bool HasActions => Session is not null || HasTitle;

    public string ActionName { get => _actionName; set => Set(ref _actionName, value); }
}

public class MonitorSection : Observable
{
    private string _title = "";
    private string _description = "";
    private string _error = "";
    private string _empty = "";
    private bool _needsAdmin;

    public string Title { get => _title; set => Set(ref _title, value); }

    public string Description { get => _description; set => Set(ref _description, value); }

    public bool NeedsAdmin { get => _needsAdmin; set => Set(ref _needsAdmin, value); }

    public string Error
    {
        get => _error;
        set
        {
            Set(ref _error, value);
            Changed(nameof(HasError));
        }
    }

    public bool HasError => Error.Length > 0;

    public string Empty
    {
        get => _empty;
        set
        {
            Set(ref _empty, value);
            Changed(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => Empty.Length > 0;

    public ObservableCollection<MonitorRow> Rows { get; } = new();
}

// A list of rows: failed sign-ins (FailRowView) and Being copied now (FileRowView).
public sealed class TableSection : MonitorSection
{
    public TableSection()
    {
        Items.CollectionChanged += (_, _) => Changed(nameof(HasItems));
    }

    public ObservableCollection<KeyedRow> Items { get; } = new();

    public bool HasItems => Items.Count > 0;
}

public sealed class FailRowView : KeyedRow
{
    private string _time = "";
    private string _user = "";
    private string _from = "";
    private string _reason = "";

    internal FailRowView(string key)
        : base(key)
    {
    }

    public string Time { get => _time; set => Set(ref _time, value); }

    public string User { get => _user; set => Set(ref _user, value); }

    public string From { get => _from; set => Set(ref _from, value); }

    public string Reason { get => _reason; set => Set(ref _reason, value); }
}

public sealed class FileRowView : KeyedRow
{
    private string _path = "";
    private string _who = "";

    internal FileRowView(string key)
        : base(key)
    {
    }

    public string Path { get => _path; set => Set(ref _path, value); }

    // The device (its name or address) and the account that has the file open.
    public string Who { get => _who; set => Set(ref _who, value); }
}

// Recent activity: a table of changes with one header row, a Clear button and the switch for the activity log.
public sealed class ActivitySection : MonitorSection
{
    private bool _saveToLog;
    private bool _canOpenLog;
    private string _logHelp = "";

    public ObservableCollection<ActivityRowView> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    public bool SaveToLog { get => _saveToLog; set => Set(ref _saveToLog, value); }

    public bool CanOpenLog { get => _canOpenLog; set => Set(ref _canOpenLog, value); }

    public string LogHelp { get => _logHelp; set => Set(ref _logHelp, value); }

    public ActivitySection()
    {
        Items.CollectionChanged += (_, _) => Changed(nameof(HasItems));
    }
}

// One row of the Recent activity table. Path is one line, trimmed, with the full text as its tooltip; a rename has
// the old path on the first line and the new one on the second; an overflow row has a note instead of a path.
public sealed class ActivityRowView : Observable
{
    private string _time = "";
    private string _change = "";
    private string _path = "";
    private string _second = "";
    private string _note = "";
    private string? _tip;

    internal ActivityRowView(long id)
    {
        Id = id;
    }

    internal long Id { get; }

    public string Time { get => _time; set => Set(ref _time, value); }

    public string Change { get => _change; set => Set(ref _change, value); }

    public string Path { get => _path; set => Set(ref _path, value); }

    public string Second
    {
        get => _second;
        set
        {
            Set(ref _second, value);
            Changed(nameof(HasSecond));
        }
    }

    public bool HasSecond => Second.Length > 0;

    public string Note
    {
        get => _note;
        set
        {
            Set(ref _note, value);
            Changed(nameof(HasNote));
        }
    }

    public bool HasNote => Note.Length > 0;

    // Null for no tooltip.
    public string? Tip { get => _tip; set => Set(ref _tip, value); }
}

// The live view of the SMB server, every 2 seconds. Reads run on the thread pool and their results are merged into
// the same view objects, so only values that changed are redrawn. The one action is Disconnect, after confirmation.
public partial class MonitorPage : Page
{
    private const int MaxFiles = 50;
    private const int MaxFailedLogons = 20;
    private const int MaxActivity = 200;

    // The Security log is read every fifth refresh (10 seconds), as in 0.2.3.
    private const int FailedLogonEvery = 5;

    // A watch of the shared folder that failed or stopped is tried again every 20th drain (10 seconds).
    private const int ActivityRetryEvery = 20;

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _activityTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly MonitorSection _sessions = new();
    private readonly TableSection _files = new();
    private readonly ActivitySection _activitySection = new();
    private readonly TableSection _fails = new();

    // Recent activity lives in memory for as long as the app runs, and goes to activity-log.txt only when the user
    // switched that on.
    private readonly FolderActivity _activity = new(TimeSpan.FromSeconds(2), MaxActivity, TimeSpan.FromHours(24));
    private FolderWatcher? _watch;
    private WatchState? _loggedWatchState;
    private int _watchGeneration;
    private int _activityTicks;
    private bool _watchBusy;
    private bool _watchCrashed;
    private readonly HashSet<string> _loggedFails = new(StringComparer.Ordinal);
    private readonly HostNameCache _hostNames = new();

    // The last MAC seen per address this session, so a neighbor entry that is briefly missing doesn't drop the name.
    private readonly Dictionary<string, string> _knownMacs = new(StringComparer.OrdinalIgnoreCase);

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private MonitorSnapshot? _previous;
    private MonitorSnapshot? _current;
    private Part<List<FailedLogon>>? _failed;
    private DeviceNameStore? _deviceNames;
    private int _tick;
    private bool _reading;

    // When the last reading arrived, for "Updated n s ago".
    private TimeSpan? _readAt;

    public MonitorPage()
    {
        InitializeComponent();
        DevicesArea.DataContext = _sessions;
        ActivityArea.DataContext = _activitySection;
        FilesArea.DataContext = _files;
        FailsArea.DataContext = _fails;
        AdminPanel.Visibility = Startup.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        MonitorPanel.Visibility = Startup.IsElevated ? Visibility.Visible : Visibility.Collapsed;

        // Without administrator rights only Recent activity can be read: it watches the shared folder, which needs none.
        FilesArea.Visibility = Startup.IsElevated ? Visibility.Visible : Visibility.Collapsed;
        BottomColumns.MaxColumns = Startup.IsElevated ? 2 : 1;
        _timer.Tick += async (_, _) =>
        {
            RenderUpdated();
            await RefreshAsync();
        };
        _activityTimer.Tick += async (_, _) => await ActivityTickAsync();
        _hostNames.Resolved += (_, _) => Render();
        IsVisibleChanged += OnVisibleChanged;
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Loc.Culture))
            {
                Render();
            }
        };
    }

    // Reads only while the page is on screen.
    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!Startup.IsElevated)
        {
            if (IsVisible)
            {
                await StartWatchingAsync();
            }
            else
            {
                StopWatching();
            }

            return;
        }

        if (IsVisible)
        {
            AppLog.Write("monitor", "Started");
            _tick = 0;
            _hostNames.Clear();
            _knownMacs.Clear();
            _timer.Start();
            var watching = StartWatchingAsync();
            await RefreshAsync();
            await watching;
        }
        else
        {
            _timer.Stop();
            StopWatching();
            AppLog.Write("monitor", "Stopped");
        }
    }

    // Never throws: a failure shows on the Recent activity card and in the log.
    private async Task StartWatchingAsync()
    {
        var generation = ++_watchGeneration;
        _watchBusy = true;
        _watchCrashed = false;
        try
        {
            var watch = await Task.Run(() => FolderWatcher.Open(_clock));
            if (generation != _watchGeneration)
            {
                watch.Dispose();
                return;
            }

            _watch = watch;
            _activity.Watch(watch.Root);
            _activityTicks = 0;
            LogWatchState(watch);
            _activityTimer.Start();
        }
        catch (Exception ex)
        {
            _watchCrashed = true;
            AppLog.Write("monitor", "Folder watcher could not start: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
        }
        finally
        {
            if (generation == _watchGeneration)
            {
                _watchBusy = false;
                RenderActivity();
            }
        }
    }

    private void StopWatching()
    {
        _watchGeneration++;
        _watchBusy = false;
        _activityTimer.Stop();
        if (_watch is not { } watch)
        {
            return;
        }

        foreach (var change in watch.Drain())
        {
            _activity.Add(change);
        }

        SaveFinished(all: true);
        watch.Dispose();
        _watch = null;
        _loggedWatchState = null;

        // Counts only: file names are personal and the log goes to support.
        AppLog.Write("monitor", "Folder watcher stopped: " + watch.Received.ToString(CultureInfo.InvariantCulture) + " changes received, "
            + watch.Overflows.ToString(CultureInfo.InvariantCulture) + " buffer overflows, " + _activity.Rows().Count.ToString(CultureInfo.InvariantCulture) + " rows kept");
    }

    // Every 500 ms: moves the watcher's queued changes into the list and redraws only when something changed.
    private async Task ActivityTickAsync()
    {
        try
        {
            if (_watch is not { } watch)
            {
                return;
            }

            var changed = false;
            foreach (var change in watch.Drain())
            {
                var added = _activity.Add(change);
                changed |= added;
                if (added && change.Kind == ActivityKind.Missed)
                {
                    AppLog.Write("monitor", "Folder watcher: some changes may be missing (" + watch.Overflows.ToString(CultureInfo.InvariantCulture) + " buffer overflows so far)");
                }
            }

            changed |= _activity.Expire(_clock.Elapsed);
            SaveFinished(all: false);
            if (watch.State != _loggedWatchState)
            {
                LogWatchState(watch);
                changed = true;
            }

            if (changed)
            {
                RenderActivity();
            }

            if (++_activityTicks % ActivityRetryEvery == 0 && watch.CanRetry && !_watchBusy)
            {
                var generation = _watchGeneration;
                _watchBusy = true;
                try
                {
                    await Task.Run(watch.TryStart);
                }
                finally
                {
                    if (generation == _watchGeneration)
                    {
                        _watchBusy = false;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("monitor", "Recent activity update failed: " + ex.GetType().Name);
        }
    }

    private void LogWatchState(FolderWatcher watch)
    {
        var state = watch.State;
        _loggedWatchState = state;
        AppLog.Write("monitor", state == WatchState.Watching
            ? "Folder watcher started (subfolders included)"
            : "Folder watcher not running: " + state + (watch.LogReason.Length > 0 ? " (" + watch.LogReason + ")" : ""));
    }

    private async Task RefreshAsync()
    {
        if (_reading)
        {
            return;
        }

        _reading = true;
        try
        {
            var readFails = _tick % FailedLogonEvery == 0;
            _tick++;
            _deviceNames ??= await Task.Run(DeviceLookup.LoadNames);
            var (snapshot, failed) = await Task.Run(() => (SmbMonitor.Read(), readFails ? SmbMonitor.ReadFailedLogons(MaxFailedLogons) : null));
            _previous = _current;
            _current = snapshot;
            if (failed is not null)
            {
                _failed = failed;
            }

            _readAt = _clock.Elapsed;
            Render();
            RenderUpdated();
            MonitorLoading.Visibility = Visibility.Collapsed;
            Readings.Visibility = Visibility.Visible;
        }
        finally
        {
            _reading = false;
        }
    }

    private void Render()
    {
        var loc = Loc.Instance;
        _files.Title = loc["Monitor_Files_TitleShort"];
        _files.Description = loc.Format("Monitor_Files_Desc", MaxFiles);
        _fails.Title = loc["Monitor_Fails_Title"];
        _fails.Description = loc["Monitor_Fails_Desc"];
        RenderActivity();

        var cur = _current;
        if (cur is null)
        {
            return;
        }

        var seconds = _previous is null ? 0 : (cur.Timestamp - _previous.Timestamp) / (double)Stopwatch.Frequency;

        RenderSessions(cur);
        RenderServer(cur, seconds);
        RenderFiles(cur);
        RenderFails();
    }

    // "Updated n s ago" with a static dot; a reading that is late turns the dot to caution.
    private void RenderUpdated()
    {
        if (_readAt is not { } at)
        {
            return;
        }

        var ago = (long)Math.Max(0, (_clock.Elapsed - at).TotalSeconds);
        UpdatedText.Text = Loc.Instance.Format("Monitor_Updated", Formats(ago));
        UpdatedDot.Tag = ago > 10 ? "Caution" : "Success";
        UpdatedLabel.Visibility = Visibility.Visible;
    }

    // The stat row: devices (with how many came through the VPN), Down and Up from the server totals with the current
    // rate, and failed sign-ins in the last 24 hours.
    private void RenderServer(MonitorSnapshot cur, double seconds)
    {
        var loc = Loc.Instance;
        var sessions = cur.Sessions.Status == ReadStatus.Ok ? cur.Sessions.Value ?? new List<SessionInfo>() : null;
        DevicesNumber.Text = sessions is null ? loc["Monitor_NoValue"] : Formats(sessions.Count);
        var vpn = sessions?.Count(x => x.Vpn) ?? 0;
        DevicesSub.Text = sessions is null ? "" : vpn > 0 ? loc.Format("Monitor_Stat_ViaVpn", Formats(vpn)) : loc["Monitor_Stat_NoneViaVpn"];

        var totals = cur.Server.Value;
        var before = _previous?.Server.Value;
        DownNumber.Text = Size(totals?.Down);
        UpNumber.Text = Size(totals?.Up);
        DownSub.Text = totals is null ? "" : loc.Format("Monitor_Stat_Now", Rate(MonitorMath.Rate(before?.Down, totals.Down, seconds)));
        UpSub.Text = totals is null ? "" : loc.Format("Monitor_Stat_Now", Rate(MonitorMath.Rate(before?.Up, totals.Up, seconds)));

        // The totals cover every device; with exactly one connected, its current traffic is all from that one.
        ServerNote.Text = _sessions.Rows.Count == 1 && _sessions.Rows[0] is { Session: not null } only
            ? loc.FormatValues("Monitor_Server_DescOne", only.Shown)
            : loc["Monitor_Server_Desc"];

        var error = cur.Server.Status switch
        {
            ReadStatus.Missing => loc["Monitor_Server_Unavailable"],
            ReadStatus.Failed => loc.FormatValues("Monitor_ReadFailed", cur.Server.Error),
            ReadStatus.NeedsAdmin => loc["Badge_NeedsAdmin"],
            _ => "",
        };
        ServerError.Text = error;
        ServerError.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderSessions(MonitorSnapshot cur)
    {
        var loc = Loc.Instance;
        Status(_sessions, cur.Sessions);
        var list = cur.Sessions.Value ?? new List<SessionInfo>();
        _sessions.Empty = cur.Sessions.Status == ReadStatus.Ok && list.Count == 0 ? loc["Monitor_Sessions_None"] : "";
        var macs = cur.Macs.Value;
        SyncRows(_sessions.Rows, list, s => s.Id.ToString(CultureInfo.InvariantCulture), k => new MonitorRow(k), (row, s) =>
        {
            var address = DeviceIdentity.NormalizeAddress(s.Client);
            if (macs is not null && macs.TryGetValue(address, out var seen))
            {
                _knownMacs[address] = seen;
            }

            _knownMacs.TryGetValue(address, out var mac);

            row.DeviceKey = DeviceIdentity.Key(mac, address);
            row.DeviceAddress = mac ?? address;
            row.Title = _deviceNames?.Get(row.DeviceKey) ?? "";
            row.Session = s;
            row.Client = s.Client;
            row.HostName = _hostNames.Get(address) ?? "";
            row.Connection = loc.Format("Monitor_Device_Connection", loc[s.Vpn ? "Monitor_Session_Vpn" : "Monitor_Device_Lan"], Duration(s.SecondsExists));
            row.User = s.User;
            row.Dialect = s.Dialect.Length == 0 ? loc["Monitor_NoValue"] : loc.Format("Monitor_Device_Smb", s.Dialect);
            var active = MonitorMath.IsActive(s.SecondsIdle, s.OpenFiles);
            row.Status = active ? loc["Monitor_Session_Active"] : loc.Format("Monitor_Session_IdleFor", Duration(s.SecondsIdle));
            row.StatusKind = active ? "Success" : "";
            row.Files = s.OpenFiles == 0 ? loc["Monitor_Device_NoFiles"] : loc.Format(s.OpenFiles == 1 ? "Monitor_Device_OneFile" : "Monitor_Device_Files", Formats(s.OpenFiles));
            row.ActionName = loc.FormatValues("Monitor_Session_DisconnectName", row.Shown);
            row.MoreActionName = loc.FormatValues("Monitor_Device_MoreName", row.Shown);
            row.NameActionName = row.HasTitle ? loc.FormatValues("Monitor_Device_RenameName", row.Title) : loc.FormatValues("Monitor_Device_NameName", s.Client);
            row.ForgetActionName = loc.FormatValues("Monitor_Device_ForgetName", row.Title);
        });
    }

    private void RenderFiles(MonitorSnapshot cur)
    {
        var loc = Loc.Instance;
        Status(_files, cur.Files);
        var all = cur.Files.Value ?? new List<OpenFileInfo>();
        _files.Empty = cur.Files.Status == ReadStatus.Ok && all.Count == 0 ? loc["Monitor_Files_None"] : "";
        var shown = all.Count > MaxFiles ? all.GetRange(0, MaxFiles) : all;
        SyncRows(_files.Items, shown, f => f.Id.ToString(CultureInfo.InvariantCulture), k => new FileRowView(k), (row, f) =>
        {
            var view = (FileRowView)row;
            view.Path = f.Path;
            view.Who = loc.FormatValues("Monitor_File_Who", DeviceOf(f.Client), f.User);
        });
    }

    private void RenderActivity()
    {
        var loc = Loc.Instance;
        _activitySection.Title = loc["Monitor_Activity_Title"];
        _activitySection.Description = loc["Monitor_Activity_Intro"];
        _activitySection.SaveToLog = Startup.Preferences.SaveActivityLog;
        _activitySection.CanOpenLog = ActivityLog.Exists();
        _activitySection.LogHelp = loc.FormatValues("Monitor_Activity_SaveLogHelp", ActivityLog.FilePath ?? loc["Log_None"]);
        var watch = _watch;
        var state = watch?.State;
        var root = watch?.Root ?? "";
        _activitySection.Error = state switch
        {
            WatchState.ConfigUnreadable => loc["Monitor_Activity_ConfigUnreadable"],
            WatchState.Missing => loc.FormatValues("Monitor_Activity_Missing", root),
            WatchState.Failed => loc.FormatValues("Monitor_Activity_Failed", root),
            WatchState.Stopped => loc.FormatValues("Monitor_Activity_Stopped", root, watch!.Error),
            null when _watchCrashed => loc["Monitor_Activity_NotStarted"],
            _ => "",
        };

        var rows = _activity.Rows();
        _activitySection.Empty = state == WatchState.NotConfigured ? loc["Monitor_Activity_NotConfigured"]
            : state == WatchState.Watching && rows.Count == 0 ? loc["Monitor_Activity_None"]
            : "";

        // The same row objects stay while their entry does, so a refresh redraws only what changed.
        var items = _activitySection.Items;
        var wanted = new HashSet<long>(rows.Select(r => r.Id));
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(items[i].Id))
            {
                items.RemoveAt(i);
            }
        }

        var today = DateTime.Now.Date;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var index = -1;
            for (var j = 0; j < items.Count; j++)
            {
                if (items[j].Id == r.Id)
                {
                    index = j;
                    break;
                }
            }

            ActivityRowView view;
            if (index < 0)
            {
                view = new ActivityRowView(r.Id);
                items.Insert(Math.Min(i, items.Count), view);
            }
            else
            {
                view = items[index];
                if (index != i && i < items.Count)
                {
                    items.Move(index, i);
                }
            }

            var local = r.Time.ToLocalTime().DateTime;
            view.Time = local.ToString(local.Date == today ? "T" : "g", loc.Culture);
            view.Change = loc[r.Kind switch
            {
                ActivityKind.Added => "Monitor_Activity_Added",
                ActivityKind.Deleted => "Monitor_Activity_Deleted",
                ActivityKind.Renamed => "Monitor_Activity_Renamed",
                ActivityKind.Missed => "Monitor_Activity_Missed",
                _ => "Monitor_Activity_Changed",
            }];
            switch (r.Kind)
            {
                case ActivityKind.Missed:
                    view.Path = "";
                    view.Second = "";
                    view.Note = loc["Monitor_Activity_MissedRow"];
                    view.Tip = null;
                    break;
                case ActivityKind.Renamed:
                    view.Path = r.OldPath;
                    view.Second = loc.FormatValues("Monitor_Activity_RenamedTo", r.Path);
                    view.Note = "";
                    view.Tip = loc.FormatValues("Monitor_Activity_RenameTip", r.OldPath, r.Path);
                    break;
                default:
                    view.Path = r.Path;
                    view.Second = "";
                    view.Note = "";
                    view.Tip = r.Path;
                    break;
            }
        }
    }

    // Rows that will not change any more go to activity-log.txt while the switch is on; otherwise they are dropped,
    // so switching it on later doesn't write what came before.
    private void SaveFinished(bool all)
    {
        var finished = _activity.TakeFinished(_clock.Elapsed, all);
        if (finished.Count > 0 && Startup.Preferences.SaveActivityLog)
        {
            ActivityLog.Append(finished);
            _activitySection.CanOpenLog = true;
        }
    }

    private void OnClearActivity(object sender, RoutedEventArgs e)
    {
        SaveFinished(all: true);
        var cleared = _activity.Clear();
        AppLog.Write("monitor", "Recent activity cleared: " + cleared.ToString(CultureInfo.InvariantCulture) + " rows");
        RenderActivity();
    }

    private void OnActivityLogSwitch(object sender, RoutedEventArgs e)
    {
        var on = sender is System.Windows.Controls.Primitives.ToggleButton { IsChecked: true };
        if (on == Startup.Preferences.SaveActivityLog)
        {
            return;
        }

        // Rows finished while it was off are not written when it comes on.
        if (on)
        {
            _activity.TakeFinished(_clock.Elapsed);
        }
        else
        {
            SaveFinished(all: true);
        }

        Startup.Preferences.SaveActivityLog = on;
        Startup.SavePreferences();
        _activitySection.SaveToLog = on;
        AppLog.Write("prefs", "Save Recent activity to a log file " + (on ? "on" : "off"));
    }

    private void OnOpenActivityLog(object sender, RoutedEventArgs e)
    {
        if (ActivityLog.FilePath is not { } path || !ActivityLog.Exists())
        {
            _activitySection.CanOpenLog = false;
            return;
        }

        try
        {
            using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }))
            {
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            AppLog.Write("monitor", "Open activity log failed: " + ex.GetType().Name);
            Show(ActionResult, InfoBarSeverity.Error, Loc.Instance.Format("Log_OpenFailed", ex.Message));
        }
    }

    private void RenderFails()
    {
        var loc = Loc.Instance;
        if (_failed is null)
        {
            return;
        }

        Status(_fails, _failed);
        var list = _failed.Value ?? new List<FailedLogon>();
        var read = _failed.Status == ReadStatus.Ok;
        FailsNumber.Text = !read ? loc["Monitor_NoValue"] : list.Count >= MaxFailedLogons ? loc.Format("Monitor_Stat_AtLeast", Formats(list.Count)) : Formats(list.Count);
        FailsSub.Text = !read ? (_failed.Status == ReadStatus.NeedsAdmin ? loc["Badge_NeedsAdmin"] : loc.FormatValues("Monitor_ReadFailed", _failed.Error))
            : list.Count == 0 ? loc["Monitor_Stat_FailsNone"] : loc["Monitor_Stat_FailsSee"];
        FailsDot.Tag = !read ? "" : list.Count == 0 ? "Success" : "Caution";

        // The table only when there is at least one failure.
        FailsArea.Visibility = read && list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var f in list)
        {
            if (_loggedFails.Add(f.Key))
            {
                AppLog.Write("monitor", "Failed logon shown: " + f.LogText);
            }
        }

        SyncRows(_fails.Items, list, f => f.Key, k => new FailRowView(k), (row, f) =>
        {
            var view = (FailRowView)row;
            view.Time = f.Time is { } t ? t.ToLocalTime().DateTime.ToString("g", loc.Culture) : loc["Monitor_NoValue"];
            view.User = f.User ?? loc["Monitor_NoValue"];
            view.From = f.Source ?? f.Workstation ?? loc["Monitor_NoValue"];
            view.Reason = f.ReasonKey is { } key ? loc[key] : loc.FormatValues("Monitor_Fail_Unknown", f.ReasonCode ?? loc["Monitor_NoValue"]);
        });
    }

    private static void Status<T>(MonitorSection section, Part<T> part)
        where T : class
    {
        section.NeedsAdmin = part.Status == ReadStatus.NeedsAdmin;
        section.Error = part.Status == ReadStatus.Failed ? Loc.Instance.FormatValues("Monitor_ReadFailed", part.Error) : "";
    }

    // Keeps the row objects of entries still present, so their controls stay and only changed text is redrawn.
    private static void SyncRows<TRow, T>(ObservableCollection<TRow> rows, IList<T> items, Func<T, string> key, Func<string, TRow> create, Action<TRow, T> update)
        where TRow : KeyedRow
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            wanted.Add(key(item));
        }

        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(rows[i].Key))
            {
                rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < items.Count; i++)
        {
            var k = key(items[i]);
            var index = IndexOf(rows, k);
            TRow row;
            if (index < 0)
            {
                row = create(k);
                rows.Insert(Math.Min(i, rows.Count), row);
            }
            else
            {
                row = rows[index];
                if (index != i && i < rows.Count)
                {
                    rows.Move(index, i);
                }
            }

            update(row, items[i]);
        }
    }

    private static int IndexOf<TRow>(ObservableCollection<TRow> rows, string key)
        where TRow : KeyedRow
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    // The device's name when the owner gave one, otherwise its address.
    private string DeviceOf(string client)
    {
        foreach (var row in _sessions.Rows)
        {
            if (string.Equals(row.Client, client, StringComparison.OrdinalIgnoreCase))
            {
                return row.Shown;
            }
        }

        return client;
    }

    // The "…" button opens its menu of naming actions, by mouse and by keyboard.
    private void OnDeviceMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private static string Formats(double value) => value.ToString("N0", Loc.Instance.Culture);

    private static string Size(double? bytes)
    {
        var loc = Loc.Instance;
        if (bytes is not { } b)
        {
            return loc["Monitor_NoValue"];
        }

        var (value, unit) = MonitorMath.ScaleBytes(b);
        return loc.Format("Size_" + unit.ToString(CultureInfo.InvariantCulture), value.ToString(unit == 0 ? "N0" : "N1", loc.Culture));
    }

    private static string Rate(double? bytesPerSecond) =>
        bytesPerSecond is null ? Loc.Instance["Monitor_NoValue"] : Loc.Instance.Format("Monitor_Rate", Size(bytesPerSecond));

    private static string TotalAndRate(double? total, double? rate) => Loc.Instance.Format("Monitor_TotalAndRate", Size(total), Rate(rate));

    private static string Duration(long seconds)
    {
        var (h, m, s) = MonitorMath.SplitDuration(seconds);
        return h > 0 ? Loc.Instance.Format("Duration_Hours", h, m) : Loc.Instance.Format("Duration_Minutes", m, s);
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MonitorRow { Session: { } session } row })
        {
            return;
        }

        var loc = Loc.Instance;
        var id = session.Id.ToString(CultureInfo.InvariantCulture);

        // async void: anything that escapes here reaches the crash handler, so every failure ends in the InfoBar.
        try
        {
            var shown = row.HasTitle ? row.Title + " (" + session.Client + ")" : session.Client;
            var box = ApplyUi.NewChoice(this, loc["Monitor_Disconnect_Title"], new System.Windows.Controls.TextBlock
            {
                Text = loc.FormatValues("Monitor_Disconnect_Msg", shown, session.User),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            });
            box.AddButton(loc["Monitor_Disconnect_Confirm"], Wpf.Ui.Controls.MessageBoxButton.Primary, ControlAppearance.Danger);
            box.AddButton(loc["Common_Cancel"], Wpf.Ui.Controls.MessageBoxButton.Close, ControlAppearance.Secondary, isCancel: true);

            if (!DialogCloses.Confirms(await box.ShowLoggedAsync("monitor", "Monitor_Disconnect_Title")))
            {
                AppLog.Write("monitor", "Disconnect cancelled for session " + id);
                return;
            }

            var (result, error) = await Task.Run(() =>
            {
                var r = SmbMonitor.Disconnect(session, out var err);
                return (r, err);
            });
            AppLog.Write("monitor", "Disconnect result " + result + " for session " + id);

            switch (result)
            {
                case DisconnectResult.Done:
                    Show(ActionResult, InfoBarSeverity.Success, loc.FormatValues("Monitor_Disconnect_Done", shown));
                    break;
                case DisconnectResult.Gone:
                    Show(ActionResult, InfoBarSeverity.Informational, loc["Monitor_Disconnect_Gone"]);
                    break;
                default:
                    Show(ActionResult, InfoBarSeverity.Error, loc.FormatValues("Monitor_Disconnect_Failed", error));
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("monitor", "Disconnect of session " + id + " failed unexpectedly: " + ex);
            Show(ActionResult, InfoBarSeverity.Error, loc.FormatValues("Monitor_Disconnect_Failed", UiErrors.ForUser(ex)));
        }

        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("monitor", "Refresh after Disconnect failed: " + ex);
        }
    }

    private async void OnNameDevice(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MonitorRow { Session: { } session } row } || _deviceNames is not { } store)
        {
            return;
        }

        var loc = Loc.Instance;
        var key = row.DeviceKey;
        var byMac = DeviceIdentity.IsMacKey(key);
        try
        {
            var input = new System.Windows.Controls.TextBox
            {
                Text = row.Title,
                MinWidth = 320,
                Margin = new Thickness(0, 4, 0, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(input, loc["Monitor_Device_Dialog_Label"]);
            var message = new System.Windows.Controls.TextBlock { Style = (Style)FindResource("ErrorText"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { MaxWidth = 420 };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = byMac ? loc.FormatValues("Monitor_Device_Dialog_Mac", row.DeviceAddress) : loc.FormatValues("Monitor_Device_Dialog_Ip", row.DeviceAddress),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = loc["Monitor_Device_Dialog_Label"] });
            panel.Children.Add(input);
            panel.Children.Add(message);

            var titleKey = row.HasTitle ? "Monitor_Device_RenameTitle" : "Monitor_Device_NameTitle";
            var box = ApplyUi.NewChoice(this, loc[titleKey], panel);

            // Enter in the name box saves: the name only goes to devices.json, nothing on Windows changes.
            var save = box.AddButton(loc["Monitor_Device_Save"], Wpf.Ui.Controls.MessageBoxButton.Primary, ControlAppearance.Primary, isDefault: true);
            box.AddButton(loc["Common_Cancel"], Wpf.Ui.Controls.MessageBoxButton.Close, ControlAppearance.Secondary, isCancel: true);

            void Validate()
            {
                var check = Validators.DeviceName(input.Text);
                save.IsEnabled = check.IsValid;
                message.Text = check.Error switch
                {
                    ValidationError.None => "",
                    ValidationError.TooLong => loc.Format("Monitor_Device_TooLong", Validators.MaxDeviceNameLength),
                    ValidationError.ControlCharacters => loc["Monitor_Device_Control"],
                    _ => loc["Monitor_Device_Empty"],
                };
            }

            input.TextChanged += (_, _) => Validate();
            input.Loaded += (_, _) =>
            {
                input.Focus();
                input.SelectAll();
            };
            Validate();

            if (!DialogCloses.Confirms(await box.ShowLoggedAsync("devices", titleKey)))
            {
                return;
            }

            var name = Validators.DeviceName(input.Text);
            if (!name.IsValid || !store.Set(key, name.Value!))
            {
                return;
            }

            var error = await Task.Run(() => DeviceLookup.SaveNames(store));
            AppLog.Write("devices", "Device name " + (error is null ? "saved" : "not saved") + " for session " + session.Id.ToString(CultureInfo.InvariantCulture) + ", keyed by " + (byMac ? "MAC" : "IP"));
            if (error is null)
            {
                Show(ActionResult, InfoBarSeverity.Success, loc.FormatValues("Monitor_Device_Saved", name.Value));
            }
            else
            {
                Show(ActionResult, InfoBarSeverity.Error, loc.FormatValues("Monitor_Device_SaveFailed", error));
            }

            Render();
        }
        catch (Exception ex)
        {
            AppLog.Write("devices", "Naming a device failed unexpectedly: " + ex);
            Show(ActionResult, InfoBarSeverity.Error, loc.FormatValues("Monitor_Device_SaveFailed", UiErrors.ForUser(ex)));
        }
    }

    private async void OnForgetDevice(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MonitorRow { HasTitle: true } row } || _deviceNames is not { } store)
        {
            return;
        }

        var loc = Loc.Instance;
        var name = row.Title;
        try
        {
            if (!store.Remove(row.DeviceKey))
            {
                return;
            }

            var error = await Task.Run(() => DeviceLookup.SaveNames(store));
            AppLog.Write("devices", "Device name " + (error is null ? "forgotten" : "removed but not saved") + ", keyed by " + (DeviceIdentity.IsMacKey(row.DeviceKey) ? "MAC" : "IP"));
            if (error is null)
            {
                Show(ActionResult, InfoBarSeverity.Informational, loc.FormatValues("Monitor_Device_Forgotten", name));
            }
            else
            {
                Show(ActionResult, InfoBarSeverity.Error, loc.FormatValues("Monitor_Device_SaveFailed", error));
            }

            Render();
        }
        catch (Exception ex)
        {
            AppLog.Write("devices", "Forgetting a device name failed unexpectedly: " + ex);
            Show(ActionResult, InfoBarSeverity.Error, loc.FormatValues("Monitor_Device_SaveFailed", UiErrors.ForUser(ex)));
        }
    }

    private async void OnContinueAsAdministrator(object sender, RoutedEventArgs e)
    {
        ElevateButton.IsEnabled = false;
        try
        {
            // The elevated instance opens on this page; the session's drive letters go along as Setup's do.
            var letters = await Task.Run(() => DriveLetters.SessionLetters(DriveLetters.Scan(null)));
            var result = Relaunch.Start(Core.Elevation.Pages.Monitor, letters, out var error);
            switch (result)
            {
                case RelaunchResult.Started:
                    Application.Current.Shutdown();
                    break;
                case RelaunchResult.Cancelled:
                    Show(AdminResult, InfoBarSeverity.Warning, Loc.Instance["Setup_Admin_CancelledMsg"], Loc.Instance["Setup_Admin_CancelledTitle"]);
                    break;
                default:
                    Show(AdminResult, InfoBarSeverity.Error, error ?? "", Loc.Instance["Setup_Admin_FailedTitle"]);
                    break;
            }
        }
        finally
        {
            ElevateButton.IsEnabled = true;
        }
    }

    private static void Show(InfoBar bar, InfoBarSeverity severity, string message, string title = "")
    {
        bar.Severity = severity;
        bar.Title = title;
        bar.Message = message;
        bar.IsOpen = true;
    }
}
