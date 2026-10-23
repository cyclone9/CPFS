using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cpfs.Core.Monitor;
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

public sealed class MonitorField : Observable
{
    private string _label = "";
    private string _value = "";
    private string _detail = "";
    private bool _isData;

    public string Label { get => _label; set => Set(ref _label, value); }

    public string Value { get => _value; set => Set(ref _value, value); }

    // A secondary line under the value, such as a device's network name under its IP.
    public string Detail
    {
        get => _detail;
        set
        {
            Set(ref _detail, value);
            Changed(nameof(HasDetail));
        }
    }

    public bool HasDetail => Detail.Length > 0;

    public bool IsData
    {
        get => _isData;
        set
        {
            Set(ref _isData, value);
            Changed(nameof(IsText));
        }
    }

    public bool IsText => !IsData;
}

public sealed class MonitorRow : Observable
{
    private string _badge = "";
    private string _actionName = "";
    private string _title = "";
    private string _nameActionName = "";
    private string _forgetActionName = "";
    private SessionInfo? _session;

    internal MonitorRow(string key)
    {
        Key = key;
    }

    internal string Key { get; }

    internal SessionInfo? Session
    {
        get => _session;
        set
        {
            _session = value;
            Changed(nameof(CanDisconnect));
            Changed(nameof(CanName));
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
        }
    }

    public bool HasTitle => Title.Length > 0;

    public bool CanName => Session is not null && !HasTitle;

    public string NameActionName { get => _nameActionName; set => Set(ref _nameActionName, value); }

    public string ForgetActionName { get => _forgetActionName; set => Set(ref _forgetActionName, value); }

    public string Badge
    {
        get => _badge;
        set
        {
            Set(ref _badge, value);
            Changed(nameof(HasBadge));
        }
    }

    public bool HasBadge => Badge.Length > 0;

    public bool CanDisconnect => Session is not null;

    public string ActionName { get => _actionName; set => Set(ref _actionName, value); }

    public ObservableCollection<MonitorField> Fields { get; } = new();
}

public sealed class MonitorSection : Observable
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

    public ObservableCollection<MonitorField> Fields { get; } = new();

    public ObservableCollection<MonitorRow> Rows { get; } = new();
}

// The live view of the SMB server, every 2 seconds. Reads run on the thread pool and their results are merged into
// the same view objects, so only values that changed are redrawn. The one action is Disconnect, after confirmation.
public partial class MonitorPage : Page
{
    private const int MaxFiles = 50;
    private const int MaxFailedLogons = 20;

    // The Security log is read every fifth refresh (10 seconds), as in 0.2.3.
    private const int FailedLogonEvery = 5;

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private readonly MonitorSection _server = new();
    private readonly MonitorSection _sessions = new();
    private readonly MonitorSection _shares = new();
    private readonly MonitorSection _files = new();
    private readonly MonitorSection _fails = new();
    private readonly HashSet<string> _loggedFails = new(StringComparer.Ordinal);
    private readonly HostNameCache _hostNames = new();

    // The last MAC seen per address this session, so a neighbor entry that is briefly missing doesn't drop the name.
    private readonly Dictionary<string, string> _knownMacs = new(StringComparer.OrdinalIgnoreCase);

    private MonitorSnapshot? _previous;
    private MonitorSnapshot? _current;
    private Part<List<FailedLogon>>? _failed;
    private DeviceNameStore? _deviceNames;
    private int _tick;
    private bool _reading;

    public MonitorPage()
    {
        InitializeComponent();
        Sections.ItemsSource = new[] { _server, _sessions, _shares, _files, _fails };
        AdminPanel.Visibility = Startup.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        MonitorPanel.Visibility = Startup.IsElevated ? Visibility.Visible : Visibility.Collapsed;
        _timer.Tick += async (_, _) => await RefreshAsync();
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
            return;
        }

        if (IsVisible)
        {
            AppLog.Write("monitor", "Started");
            _tick = 0;
            _hostNames.Clear();
            _knownMacs.Clear();
            _timer.Start();
            await RefreshAsync();
        }
        else
        {
            _timer.Stop();
            AppLog.Write("monitor", "Stopped");
        }
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

            Render();
        }
        finally
        {
            _reading = false;
        }
    }

    private void Render()
    {
        var loc = Loc.Instance;
        _server.Title = loc["Monitor_Server_Title"];
        _server.Description = loc["Monitor_Server_Desc"];
        _shares.Title = loc["Monitor_Shares_Title"];
        _shares.Description = loc["Monitor_Shares_Desc"];
        _sessions.Description = loc["Monitor_Sessions_Desc"];
        _files.Description = loc.Format("Monitor_Files_Desc", MaxFiles);
        _fails.Title = loc["Monitor_Fails_Title"];
        _fails.Description = loc["Monitor_Fails_Desc"];

        var cur = _current;
        if (cur is null)
        {
            _sessions.Title = loc.Format("Monitor_Sessions_Title", 0);
            _files.Title = loc.Format("Monitor_Files_Title", 0);
            return;
        }

        UpdatedText.Text = loc.Format("Monitor_Updated", cur.Taken.ToString("T", loc.Culture));
        var seconds = _previous is null ? 0 : (cur.Timestamp - _previous.Timestamp) / (double)Stopwatch.Frequency;

        RenderServer(cur, seconds);
        RenderSessions(cur);
        RenderShares(cur, seconds);
        RenderFiles(cur);
        RenderFails();
    }

    private void RenderServer(MonitorSnapshot cur, double seconds)
    {
        var loc = Loc.Instance;
        Status(_server, cur.Server);
        var totals = cur.Server.Value;
        var before = _previous?.Server.Value;
        SetFields(_server.Fields, totals is null ? new List<(string, string, bool)>() : new List<(string, string, bool)>
        {
            ("Monitor_Server_Down", TotalAndRate(totals.Down, MonitorMath.Rate(before?.Down, totals.Down, seconds)), true),
            ("Monitor_Server_Up", TotalAndRate(totals.Up, MonitorMath.Rate(before?.Up, totals.Up, seconds)), true),
            ("Monitor_Server_LogonErrors", totals.LogonErrors is { } n ? Formats(n) : loc["Monitor_NoValue"], true),
        });
        _server.Empty = cur.Server.Status == ReadStatus.Missing ? loc["Monitor_Server_Unavailable"] : "";
    }

    private void RenderSessions(MonitorSnapshot cur)
    {
        var loc = Loc.Instance;
        Status(_sessions, cur.Sessions);
        var list = cur.Sessions.Value ?? new List<SessionInfo>();
        _sessions.Title = loc.Format("Monitor_Sessions_Title", list.Count);
        _sessions.Empty = cur.Sessions.Status == ReadStatus.Ok && list.Count == 0 ? loc["Monitor_Sessions_None"] : "";
        var macs = cur.Macs.Value;
        SyncRows(_sessions.Rows, list, s => s.Id.ToString(CultureInfo.InvariantCulture), (row, s) =>
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
            row.Badge = s.Vpn ? loc["Monitor_Session_Vpn"] : "";
            var shown = row.HasTitle ? row.Title : s.Client;
            row.ActionName = loc.FormatValues("Monitor_Session_DisconnectName", shown);
            row.NameActionName = row.HasTitle ? loc.FormatValues("Monitor_Device_RenameName", row.Title) : loc.FormatValues("Monitor_Device_NameName", s.Client);
            row.ForgetActionName = loc.FormatValues("Monitor_Device_ForgetName", row.Title);
            SetFields(row.Fields, new List<(string, string, bool)>
            {
                ("Monitor_Session_Client", s.Client, true),
                ("Monitor_Session_User", s.User, true),
                ("Monitor_Session_Dialect", s.Dialect.Length == 0 ? loc["Monitor_NoValue"] : s.Dialect, true),
                ("Monitor_Session_Files", Formats(s.OpenFiles), false),
                ("Monitor_Session_Connected", Duration(s.SecondsExists), false),
                ("Monitor_Session_Idle", Duration(s.SecondsIdle), false),
            }, _hostNames.Get(address));
        });
    }

    private void RenderShares(MonitorSnapshot cur, double seconds)
    {
        var loc = Loc.Instance;
        Status(_shares, cur.Shares);
        _shares.Empty = cur.Shares.Status == ReadStatus.Missing ? loc["Monitor_Shares_Unavailable"]
            : cur.Shares.Status == ReadStatus.Ok && cur.Shares.Value!.Count == 0 ? loc["Monitor_Shares_None"] : "";
        var shares = new List<KeyValuePair<string, (double Down, double Up)>>(cur.Shares.Value ?? new Dictionary<string, (double, double)>());
        shares.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        var before = _previous?.Shares.Value;
        SyncRows(_shares.Rows, shares, s => s.Key, (row, s) =>
        {
            (double Down, double Up)? old = before is not null && before.TryGetValue(s.Key, out var o) ? o : null;
            SetFields(row.Fields, new List<(string, string, bool)>
            {
                ("Monitor_Share_Name", s.Key, true),
                ("Monitor_Share_Down", Size(s.Value.Down), true),
                ("Monitor_Share_Up", Size(s.Value.Up), true),
                ("Monitor_Share_DownNow", Rate(MonitorMath.Rate(old?.Down, s.Value.Down, seconds)), true),
                ("Monitor_Share_UpNow", Rate(MonitorMath.Rate(old?.Up, s.Value.Up, seconds)), true),
            });
        });
    }

    private void RenderFiles(MonitorSnapshot cur)
    {
        var loc = Loc.Instance;
        Status(_files, cur.Files);
        var all = cur.Files.Value ?? new List<OpenFileInfo>();
        _files.Title = loc.Format("Monitor_Files_Title", all.Count);
        _files.Empty = cur.Files.Status == ReadStatus.Ok && all.Count == 0 ? loc["Monitor_Files_None"] : "";
        var shown = all.Count > MaxFiles ? all.GetRange(0, MaxFiles) : all;
        SyncRows(_files.Rows, shown, f => f.Id.ToString(CultureInfo.InvariantCulture), (row, f) => SetFields(row.Fields, new List<(string, string, bool)>
        {
            ("Monitor_File_Path", f.Path, true),
            ("Monitor_File_User", f.User, true),
            ("Monitor_File_Client", f.Client, true),
        }));
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
        _fails.Empty = _failed.Status == ReadStatus.Ok && list.Count == 0 ? loc["Monitor_Fails_None"] : "";
        foreach (var f in list)
        {
            if (_loggedFails.Add(f.Key))
            {
                AppLog.Write("monitor", "Failed logon shown: " + f.LogText);
            }
        }

        SyncRows(_fails.Rows, list, f => f.Key, (row, f) =>
        {
            var reason = f.ReasonKey is { } key ? loc[key] : loc.FormatValues("Monitor_Fail_Unknown", f.ReasonCode ?? loc["Monitor_NoValue"]);
            SetFields(row.Fields, new List<(string, string, bool)>
            {
                ("Monitor_Fail_Time", f.Time is { } t ? t.ToLocalTime().DateTime.ToString("g", loc.Culture) : loc["Monitor_NoValue"], false),
                ("Monitor_Fail_User", f.User ?? loc["Monitor_NoValue"], true),
                ("Monitor_Fail_From", f.Source ?? f.Workstation ?? loc["Monitor_NoValue"], true),
                ("Monitor_Fail_Reason", reason, false),
            });
        });
    }

    private static void Status<T>(MonitorSection section, Part<T> part)
        where T : class
    {
        section.NeedsAdmin = part.Status == ReadStatus.NeedsAdmin;
        section.Error = part.Status == ReadStatus.Failed ? Loc.Instance.FormatValues("Monitor_ReadFailed", part.Error) : "";
    }

    // Keeps the row objects of entries still present, so their controls stay and only changed text is redrawn.
    private static void SyncRows<T>(ObservableCollection<MonitorRow> rows, IList<T> items, Func<T, string> key, Action<MonitorRow, T> update)
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
            MonitorRow row;
            if (index < 0)
            {
                row = new MonitorRow(k);
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

    private static int IndexOf(ObservableCollection<MonitorRow> rows, string key)
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

    private static void SetFields(ObservableCollection<MonitorField> fields, List<(string LabelKey, string Value, bool IsData)> values, string? firstDetail = null)
    {
        while (fields.Count > values.Count)
        {
            fields.RemoveAt(fields.Count - 1);
        }

        for (var i = 0; i < values.Count; i++)
        {
            if (i == fields.Count)
            {
                fields.Add(new MonitorField());
            }

            fields[i].Label = Loc.Instance[values[i].LabelKey];
            fields[i].Value = values[i].Value;
            fields[i].IsData = values[i].IsData;
            fields[i].Detail = i == 0 ? firstDetail ?? "" : "";
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
            var box = NewDialog(loc["Monitor_Disconnect_Title"], new System.Windows.Controls.TextBlock
            {
                Text = loc.FormatValues("Monitor_Disconnect_Msg", shown, session.User),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            });
            box.PrimaryButtonText = loc["Monitor_Disconnect_Confirm"];
            box.PrimaryButtonAppearance = ControlAppearance.Danger;

            if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
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
            Show(ActionResult, InfoBarSeverity.Error, loc.FormatValues("Monitor_Disconnect_Failed", ex.GetType().Name + ": " + ex.Message));
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

            var box = NewDialog(loc[row.HasTitle ? "Monitor_Device_RenameTitle" : "Monitor_Device_NameTitle"], panel);
            box.PrimaryButtonText = loc["Monitor_Device_Save"];
            box.PrimaryButtonAppearance = ControlAppearance.Primary;

            void Validate()
            {
                var check = Validators.DeviceName(input.Text);
                box.IsPrimaryButtonEnabled = check.IsValid;
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

            if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
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
            Show(ActionResult, InfoBarSeverity.Error, loc.FormatValues("Monitor_Device_SaveFailed", ex.GetType().Name + ": " + ex.Message));
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
            Show(ActionResult, InfoBarSeverity.Error, loc.FormatValues("Monitor_Device_SaveFailed", ex.GetType().Name + ": " + ex.Message));
        }
    }

    private Wpf.Ui.Controls.MessageBox NewDialog(string title, object content)
    {
        var owner = Window.GetWindow(this);
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            CloseButtonText = Loc.Instance["Common_Cancel"],
            FlowDirection = owner?.FlowDirection ?? FlowDirection.LeftToRight,
            Owner = owner,
        };
        box.SetResourceReference(FontFamilyProperty, Theming.FontResourceKey);
        return box;
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
