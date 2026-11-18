using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Cpfs.Core.Connections;
using Cpfs.Core.Text;
using Cpfs.Windows;
using Wpf.Ui.Controls;

namespace Cpfs.Pages;

public sealed class ConnectionRowItem
{
    public ClientConnection Source { get; init; } = null!;

    public string Share { get; init; } = "";

    public string Drive { get; init; } = "";

    public bool DriveIsData { get; init; }

    public bool DriveIsWords => !DriveIsData;

    public string Account { get; init; } = "";

    public bool AccountIsData { get; init; }

    public bool AccountIsWords => !AccountIsData;

    public string State { get; init; } = "";

    // The status dot's state name (Styles.xaml, StatusDot).
    public string StateKind { get; init; } = "";

    public bool Reconnects { get; init; }

    public string HelpText { get; init; } = "";

    public string ActionName { get; init; } = "";
}

public sealed class ConnectionGroupItem
{
    public ClientConnectionGroup Source { get; init; } = null!;

    public string Server { get; init; } = "";

    public bool IsThisPc { get; init; }

    public string ActionName { get; init; } = "";

    public List<ConnectionRowItem> Rows { get; } = new();
}

// Connections from this PC: the shared folders this PC uses on other computers (the SMB client side) for the signed-in
// user's own logon session, and Disconnect for them. Only in an unelevated window (see SmbClient). Nothing here reaches
// CPFS's own share: the page calls only SMB client functions.
public partial class ConnectionsPage : Page
{
    private IReadOnlyList<ClientConnectionGroup> _groups = Array.Empty<ClientConnectionGroup>();
    private string? _savedError;
    private bool _busy;

    public ConnectionsPage()
    {
        InitializeComponent();
        ApplyUi.AttachKeys(ConflictHelp);
        Loaded += OnLoaded;
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Loc.Culture) && !Startup.IsElevated)
            {
                Render();
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Windows' own wording of error 1219, in the Windows display language.
        WindowsMessage.Text = Bidi.Embed(new Win32Exception(ClientDisconnect.ErrorSessionCredentialConflict).Message, Loc.Instance.IsRtl);
        if (Startup.IsElevated)
        {
            AdminInfo.IsOpen = true;
            ReopenButton.Visibility = Visibility.Visible;
            ListArea.Visibility = Visibility.Collapsed;
            Bar.Visibility = Visibility.Collapsed;
            AppLog.Write("connections", "Elevated window: the list is not read");
            return;
        }

        ClearArea.Visibility = ServerClear.Shown ? Visibility.Visible : Visibility.Collapsed;
        await ReadAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        ActionResult.IsOpen = false;
        await ReadAsync();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        Groups.IsEnabled = !busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // False when the list could not be read; the page then shows why.
    private async Task<bool> ReadAsync()
    {
        // S6: an elevated window would read the administrator session's connections (B1, B7).
        if (_busy || Startup.IsElevated)
        {
            return false;
        }

        SetBusy(true);
        try
        {
            return await ReadNowAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<bool> ReadNowAsync()
    {
        try
        {
            var (groups, savedError) = await Task.Run(Read);
            _groups = groups;
            _savedError = savedError;
            ReadInfo.IsOpen = false;
            Render();
            return true;
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            AppLog.Exception("connections", ex);
            _groups = Array.Empty<ClientConnectionGroup>();
            Render();
            ReadInfo.Severity = InfoBarSeverity.Error;
            ReadInfo.Title = Loc.Instance["Connections_ReadFailed_Title"];
            ReadInfo.Message = UiErrors.ForUser(ex);
            ReadInfo.IsOpen = true;
            return false;
        }
    }

    // Runs on a thread-pool thread. A failed NetUseEnum fails the reading; the saved mappings and the provider name only
    // add to it, so their failure is shown as a note.
    private static (IReadOnlyList<ClientConnectionGroup> Groups, string? SavedError) Read()
    {
        var uses = SmbClient.ReadUses();
        var ipc = uses.Count(u => u.AssignmentType == SmbUse.TypeIpc || u.Remote.EndsWith(@"\IPC$", StringComparison.OrdinalIgnoreCase));
        AppLog.Write("connections", "NetUseEnum: " + uses.Count.ToString(CultureInfo.InvariantCulture) + " uses, IPC$ "
            + ipc.ToString(CultureInfo.InvariantCulture));
        foreach (var u in uses)
        {
            AppLog.Write("connections", "  use " + (u.Local ?? "-") + " " + u.Remote + ", status " + u.Status.ToString(CultureInfo.InvariantCulture)
                + ", type " + u.AssignmentType.ToString(CultureInfo.InvariantCulture) + ", open " + u.OpenCount.ToString(CultureInfo.InvariantCulture)
                + ", account " + (u.Domain ?? "") + "\\" + (u.User ?? ""));
        }

        string? provider = null;
        try
        {
            provider = SmbClient.LanmanProviderName();
            AppLog.Write("connections", "SMB provider name: " + provider);
        }
        catch (Win32Exception ex)
        {
            AppLog.Write("connections", "WNetGetProviderName failed: " + ex.NativeErrorCode.ToString(CultureInfo.InvariantCulture) + " " + ex.Message);
        }

        var remembered = new List<RememberedMapping>();
        string? savedError = null;
        try
        {
            remembered = SmbClient.ReadRemembered();
            AppLog.Write("connections", "Saved mappings: " + remembered.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var m in remembered)
            {
                AppLog.Write("connections", "  saved " + (m.Local ?? "-") + " " + m.Remote + ", provider " + (m.Provider ?? "-")
                    + ", shown " + ClientConnections.KeepRemembered(m, provider, uses));
            }
        }
        catch (Win32Exception ex)
        {
            AppLog.Write("connections", "WNetOpenEnum(RESOURCE_REMEMBERED) failed: " + ex.NativeErrorCode.ToString(CultureInfo.InvariantCulture) + " " + ex.Message);
            savedError = ex.Message;
        }

        var host = NetworkAddresses.HostName();
        string? domain = null;
        try
        {
            domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
        }
        catch (NetworkInformationException ex)
        {
            AppLog.Write("connections", "Domain name: " + ex.Message);
        }

        var thisPc = new ThisPcNames(host, Environment.MachineName, domain, NetworkAddresses.Read());
        return (ClientConnections.Build(uses, remembered, provider, thisPc.Matches), savedError);
    }

    // ---- Reopen without administrator rights (elevated window only) ----

    private async void OnReopen(object sender, RoutedEventArgs e)
    {
        ReopenButton.IsEnabled = false;
        ReadInfo.IsOpen = false;
        try
        {
            await Task.Run(Reopen.AsDesktopUser);

            // Closing releases the single-instance mutex the new window waits for.
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (State.IsExpected(ex) || ex is Core.Apply.StepUnsupportedException or System.Security.Principal.IdentityNotMappedException)
        {
            AppLog.Exception("connections", ex);
            ReopenButton.IsEnabled = true;
            var inner = ex is System.Reflection.TargetInvocationException { InnerException: { } i } ? i : ex;
            ReadInfo.Severity = InfoBarSeverity.Error;
            ReadInfo.Title = Loc.Instance["Connections_Reopen_Failed"];
            ReadInfo.Message = UiErrors.ForUser(inner);
            ReadInfo.IsOpen = true;
        }
    }

    private void Render()
    {
        var loc = Loc.Instance;
        if (_savedError is not null)
        {
            ReadInfo.Severity = InfoBarSeverity.Warning;
            ReadInfo.Title = loc["Connections_SavedFailed_Title"];
            ReadInfo.Message = loc.FormatValues("Connections_SavedFailed_Msg", _savedError);
            ReadInfo.IsOpen = true;
        }

        EmptyCard.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var (key, connections, servers) = ClientDisconnect.CountLine(_groups);
        CountText.Text = _groups.Count == 0 ? loc["Connections_Count_None"]
            : loc.Format(key, Formats.Count(connections, loc.Culture), Formats.Count(servers, loc.Culture));

        var items = new List<ConnectionGroupItem>();
        foreach (var g in _groups)
        {
            var group = new ConnectionGroupItem
            {
                Source = g,
                Server = g.Server,
                IsThisPc = g.IsThisPc,
                ActionName = loc.FormatValues("Connections_DisconnectAllName", g.Server),
            };
            foreach (var c in g.Connections)
            {
                group.Rows.Add(new ConnectionRowItem
                {
                    Source = c,
                    Share = c.Share,
                    Drive = c.DriveLetter ?? loc[c.IsSignIn ? "Connections_SignInShort" : "Connections_NoDrive"],
                    DriveIsData = c.DriveLetter is not null,
                    Account = c.Account ?? loc["Connections_AccountUnknown"],
                    AccountIsData = c.Account is not null,
                    State = StateText(c.State),
                    StateKind = c.State switch
                    {
                        ClientConnectionState.Connected => "Success",
                        ClientConnectionState.Disconnected => "Caution",
                        _ => "",
                    },
                    Reconnects = c.ReconnectsAtSignIn,
                    HelpText = c.IsSignIn ? loc["Connections_SignIn"] : "",
                    ActionName = loc.FormatValues("Connections_DisconnectName", c.Share, g.Server),
                });
            }

            items.Add(group);
        }

        Groups.ItemsSource = items;
    }

    private static string StateText(ClientConnectionState state) => state switch
    {
        ClientConnectionState.Connected => Loc.Instance["Connections_State_Connected"],
        ClientConnectionState.Reconnecting => Loc.Instance["Connections_State_Reconnecting"],
        ClientConnectionState.SavedNotConnected => Loc.Instance["Connections_State_Saved"],
        _ => Loc.Instance["Connections_State_Disconnected"],
    };

    // ---- Disconnect ----

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        if (_busy || Startup.IsElevated || (sender as FrameworkElement)?.Tag is not ConnectionRowItem item)
        {
            return;
        }

        var row = item.Source;
        var loc = Loc.Instance;
        ActionResult.IsOpen = false;
        if (!await ConfirmAsync(loc["Connections_Disconnect_Title"], MessageFor(row), loc["Connections_Disconnect_Confirm"], "Connections_Disconnect_Title"))
        {
            AppLog.Write("connections", "Disconnect cancelled for " + Describe(row));
            return;
        }

        SetBusy(true);
        try
        {
            var verdict = await DisconnectAsync(row, force: false);
            if (verdict.Verdict == DisconnectVerdict.OpenFiles)
            {
                // Only after Windows refused for open files, and only through this second dialog's own button (B3).
                SetBusy(false);
                var forced = await ConfirmAsync(loc["Connections_Force_Title"], loc.FormatValues("Connections_Force_Msg", row.Share, row.Server),
                    loc["Connections_Force_Confirm"], "Connections_Force_Title", ControlAppearance.Danger);
                SetBusy(true);
                if (!forced)
                {
                    AppLog.Write("connections", "Forced disconnect declined for " + Describe(row));
                    ShowResult(InfoBarSeverity.Informational, loc.FormatValues("Connections_KeptOpen", row.Share, row.Server));
                    return;
                }

                verdict = await DisconnectAsync(row, force: true);
            }

            ShowVerdict(row, verdict.Verdict, verdict.Code);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnDisconnectAll(object sender, RoutedEventArgs e)
    {
        if (_busy || Startup.IsElevated || (sender as FrameworkElement)?.Tag is not ConnectionGroupItem item)
        {
            return;
        }

        var group = item.Source;
        var loc = Loc.Instance;
        ActionResult.IsOpen = false;
        var targets = ClientDisconnect.AllFor(group);
        var content = new StackPanel { MaxWidth = 520 };
        content.Children.Add(DialogText(loc.FormatValues("Connections_All_Msg", group.Server)));
        foreach (var (row, _) in targets)
        {
            content.Children.Add(DialogText(row.DriveLetter is { } letter
                ? loc.FormatValues("Connections_All_LineDrive", row.Share, letter)
                : loc.FormatValues("Connections_All_Line", row.Share), 4));
        }

        if (!await ConfirmAsync(loc["Connections_All_Title"], content, loc["Connections_All_Confirm"], "Connections_All_Title"))
        {
            AppLog.Write("connections", "Disconnect all cancelled for " + group.Server);
            return;
        }

        SetBusy(true);
        try
        {
            // Never forced in bulk: a row with open files is reported and keeps its own Disconnect.
            var codes = await Task.Run(() => targets.Select(t => (t.Row, Code: Cancel(t.Row, t.Target, force: false))).ToList());
            var read = await ReadNowAsync();
            var done = 0;
            var notes = new List<string>();
            foreach (var (row, code) in codes)
            {
                var verdict = read ? ClientDisconnect.Judge(code, ClientDisconnect.IsGone(row, _groups)) : DisconnectVerdict.Failed;
                AppLog.Write("connections", "Disconnect all: " + Describe(row) + " -> code " + code.ToString(CultureInfo.InvariantCulture) + ", " + verdict);
                switch (verdict)
                {
                    case DisconnectVerdict.Done:
                        done++;
                        break;
                    case DisconnectVerdict.OpenFiles:
                        notes.Add(loc.FormatValues("Connections_All_OpenFiles", row.Share));
                        break;
                    default:
                        notes.Add(loc.FormatValues("Connections_Failed", row.Share, row.Server, WindowsText(code)));
                        break;
                }
            }

            var title = loc.Format("Connections_All_Done", Formats.Count(done, loc.Culture), Formats.Count(codes.Count, loc.Culture));
            notes.Insert(0, Bidi.Embed(group.Server, loc.IsRtl));
            ShowResult(done == codes.Count ? InfoBarSeverity.Success : InfoBarSeverity.Warning, string.Join(Environment.NewLine, notes), title);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---- End connections to a typed server (design record 2026-11-19, 3.1; B9-B11) ----

    private async void OnClearServer(object sender, RoutedEventArgs e)
    {
        if (_busy || Startup.IsElevated || !ServerClear.Shown)
        {
            return;
        }

        var loc = Loc.Instance;
        ActionResult.IsOpen = false;
        var error = ServerClear.Parse(ClearServerBox.Text, out var request);
        ClearError.Text = error switch
        {
            ServerClearError.None => "",
            ServerClearError.Empty => loc["Connections_Clear_ErrEmpty"],
            ServerClearError.Device => loc["Connections_Clear_ErrDevice"],
            ServerClearError.Wildcard => loc["Connections_Clear_ErrWildcard"],
            ServerClearError.Path => loc["Connections_Clear_ErrPath"],
            ServerClearError.NotSmb => loc["Connections_Clear_ErrNotSmb"],
            ServerClearError.Share => loc["Connections_Clear_ErrShare"],
            _ => loc["Connections_Clear_ErrServer"],
        };
        ClearError.Visibility = error == ServerClearError.None ? Visibility.Collapsed : Visibility.Visible;
        if (request is null)
        {
            AppLog.Write("connections", "End connections: refused input (" + error + ")");
            return;
        }

        // A listed server has its own Disconnect all, which shows each connection first.
        var group = _groups.FirstOrDefault(g => string.Equals(g.Server, request.Server, StringComparison.OrdinalIgnoreCase));
        if (group is not null)
        {
            AppLog.Write("connections", "End connections: " + request.Server + " is listed; pointed to Disconnect all");
            ShowResult(InfoBarSeverity.Informational, loc.FormatValues("Connections_Clear_Listed", group.Server));
            return;
        }

        var targets = ServerClear.Targets(request, Array.Empty<ClientConnection>());
        var content = new StackPanel { MaxWidth = 520 };
        content.Children.Add(DialogText(loc.FormatValues("Connections_Clear_ConfirmMsg", request.Server)));
        foreach (var t in targets)
        {
            var line = DialogText(t.Remote, 4);
            line.FlowDirection = FlowDirection.LeftToRight;
            line.FontFamily = Theming.MonoFamily;
            content.Children.Add(line);
        }

        if (!await ConfirmAsync(loc["Connections_Clear_ConfirmTitle"], content, loc["Connections_Clear_Confirm"], "Connections_Clear_ConfirmTitle"))
        {
            AppLog.Write("connections", "End connections cancelled for " + request.Server);
            return;
        }

        SetBusy(true);
        ClearButton.IsEnabled = false;
        try
        {
            // Never forced first, and never forced in bulk (B10).
            var results = await Task.Run(() => targets.Select(t => (Target: t, Code: Delete(t, force: false))).ToList());
            for (var i = 0; i < results.Count; i++)
            {
                var (target, code) = results[i];
                if (ServerClear.Judge(code) != ServerClearVerdict.OpenFiles)
                {
                    continue;
                }

                // Only this target, only after Windows refused for open files, only through the danger dialog's own button (B3).
                SetBusy(false);
                var forced = await ConfirmAsync(loc["Connections_Force_Title"], loc.FormatValues("Connections_Force_Msg", ShareOf(target), request.Server),
                    loc["Connections_Force_Confirm"], "Connections_Force_Title", ControlAppearance.Danger);
                SetBusy(true);
                if (forced)
                {
                    results[i] = (target, await Task.Run(() => Delete(target, force: true)));
                }
                else
                {
                    AppLog.Write("connections", "End connections: forcing declined for " + target.Remote);
                }
            }

            // The list read again, so a row Windows still holds shows up; logged by the read itself.
            await ReadNowAsync();
            var lines = new List<string> { Bidi.Embed(request.Server, loc.IsRtl) };
            foreach (var (target, code) in results)
            {
                var verdict = ServerClear.Judge(code);
                AppLog.Write("connections", "End connections: " + target.Remote + " -> code " + code.ToString(CultureInfo.InvariantCulture) + ", " + verdict);
                lines.Add(verdict switch
                {
                    ServerClearVerdict.Ended => loc.FormatValues("Connections_Clear_Ended", target.Remote),
                    ServerClearVerdict.NotHeld => loc.FormatValues("Connections_Clear_NotHeld", target.Remote),
                    ServerClearVerdict.OpenFiles => loc.FormatValues("Connections_Clear_OpenFiles", target.Remote),
                    _ => loc.FormatValues("Connections_Clear_Failed", target.Remote, new Win32Exception(code).Message),
                });
            }

            // 2250 everywhere: Windows holds nothing CPFS can end by that name. Never "cleared" then.
            var verdicts = results.Select(r => ServerClear.Judge(r.Code)).ToList();
            var noneHeld = verdicts.All(v => v == ServerClearVerdict.NotHeld);
            if (noneHeld)
            {
                lines.Add(loc["Connections_Clear_TryRemedies"]);
            }

            var ok = !noneHeld && verdicts.All(v => v is ServerClearVerdict.Ended or ServerClearVerdict.NotHeld);
            ShowResult(ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning, string.Join(Environment.NewLine, lines),
                loc[noneHeld ? "Connections_Clear_NothingTitle" : "Connections_Clear_DoneTitle"]);
        }
        finally
        {
            ClearButton.IsEnabled = true;
            SetBusy(false);
        }
    }

    // Logged before the call: the name, that it was typed or added, forced or not. No password exists on this path (B6).
    private static int Delete(ServerClearTarget target, bool force)
    {
        AppLog.Write("connections", "End connections: NetUseDel " + target.Remote + (target.Typed ? " (typed)" : " (added)") + (force ? ", forced" : ", not forced"));
        return SmbClient.Delete(target, force);
    }

    private static string ShareOf(ServerClearTarget target) => ClientConnections.SplitUnc(target.Remote)?.Share ?? target.Remote;

    // One cancel, then the list read again; the verdict comes from the new list.
    private async Task<(DisconnectVerdict Verdict, int Code)> DisconnectAsync(ClientConnection row, bool force)
    {
        var target = ClientDisconnect.TargetFor(row);
        var code = await Task.Run(() => Cancel(row, target, force));
        var read = await ReadNowAsync();
        var verdict = read ? ClientDisconnect.Judge(code, ClientDisconnect.IsGone(row, _groups)) : DisconnectVerdict.Failed;
        AppLog.Write("connections", "Disconnect " + Describe(row) + (force ? " (forced)" : "") + " -> code "
            + code.ToString(CultureInfo.InvariantCulture) + ", " + verdict);
        return (verdict, code);
    }

    // Logged before the call, so a forgotten drive can be mapped again from the log. Never a password: there is none here.
    private static int Cancel(ClientConnection row, DisconnectTarget target, bool force)
    {
        AppLog.Write("connections", "Disconnecting " + Describe(row) + ": cancel " + target.Name + (target.UpdateProfile ? " and forget it" : "")
            + (force ? ", forced" : ", not forced"));
        return SmbClient.Cancel(target, force);
    }

    private static string Describe(ClientConnection row) =>
        @"\\" + row.Server + @"\" + row.Share + " (drive " + (row.DriveLetter ?? "none") + ", account " + (row.Account ?? "unknown")
        + ", reconnects at sign-in " + row.ReconnectsAtSignIn + ", state " + row.State + ")";

    private void ShowVerdict(ClientConnection row, DisconnectVerdict verdict, int code)
    {
        var loc = Loc.Instance;
        switch (verdict)
        {
            case DisconnectVerdict.Done:
                ShowResult(InfoBarSeverity.Success, loc.FormatValues("Connections_Done", row.Share, row.Server));
                break;
            case DisconnectVerdict.StillListed:
                ShowResult(InfoBarSeverity.Warning, loc.FormatValues("Connections_StillListed", row.Share, row.Server));
                break;
            case DisconnectVerdict.OpenFiles:
                ShowResult(InfoBarSeverity.Warning, loc.FormatValues("Connections_All_OpenFiles", row.Share));
                break;
            default:
                ShowResult(InfoBarSeverity.Error, loc.FormatValues("Connections_Failed", row.Share, row.Server, WindowsText(code)));
                break;
        }
    }

    private static string WindowsText(int code) => code == 0 ? Loc.Instance["Connections_StillThere"] : new Win32Exception(code).Message;

    private void ShowResult(InfoBarSeverity severity, string message, string title = "")
    {
        ActionResult.Severity = severity;
        ActionResult.Title = title;
        ActionResult.Message = message;
        ActionResult.IsOpen = true;
        ActionResult.BringIntoView();
    }

    private static string MessageFor(ClientConnection row)
    {
        var loc = Loc.Instance;
        var account = row.Account ?? loc["Connections_AccountUnknown"];
        if (row.IsSignIn)
        {
            return loc.FormatValues("Connections_Disconnect_MsgSignIn", row.Server, account);
        }

        if (row.DriveLetter is { } letter)
        {
            return loc.FormatValues(row.ReconnectsAtSignIn ? "Connections_Disconnect_MsgDriveSaved" : "Connections_Disconnect_MsgDrive",
                row.Share, row.Server, letter, account);
        }

        return loc.FormatValues("Connections_Disconnect_Msg", row.Share, row.Server, account);
    }

    // A dialog that changes Windows: no default button; Enter, Esc and the X never confirm, and every close is logged.
    private async Task<bool> ConfirmAsync(string title, object message, string action, string logName, ControlAppearance appearance = ControlAppearance.Primary)
    {
        var box = ApplyUi.NewChoice(this, title, message is string text ? DialogText(text) : message);
        box.AddButton(action, Wpf.Ui.Controls.MessageBoxButton.Primary, appearance);
        box.AddButton(Loc.Instance["Common_Cancel"], Wpf.Ui.Controls.MessageBoxButton.Close, ControlAppearance.Secondary, isCancel: true);
        return DialogCloses.Confirms(await box.ShowLoggedAsync("connections", logName));
    }

    private static System.Windows.Controls.TextBlock DialogText(string text, double bottom = 8) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, bottom) };

    // Opens Credential Manager; CPFS never reads or changes the credential store. Canonical name from Learn, "Canonical
    // Names of Control Panel Items": https://learn.microsoft.com/en-us/windows/win32/shell/controlpanel-canonical-names
    private void OnOpenCredentialManager(object sender, RoutedEventArgs e)
    {
        try
        {
            using (Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "control.exe"), "/name Microsoft.CredentialManager")
            {
                UseShellExecute = false,
            }))
            {
            }

            AppLog.Write("connections", "Credential Manager opened");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Write("connections", "Credential Manager: " + ex.GetType().Name + ": " + ex.Message);
            ShowResult(InfoBarSeverity.Error, Loc.Instance.FormatValues("Connections_CredManFailed", ex.Message));
        }
    }
}
