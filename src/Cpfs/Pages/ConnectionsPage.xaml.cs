using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Cpfs.Core.Connections;
using Cpfs.Windows;
using Wpf.Ui.Controls;

namespace Cpfs.Pages;

public abstract class ConnectionField
{
    public string Label { get; init; } = "";

    public string Value { get; init; } = "";
}

public sealed class ConnectionDataField : ConnectionField
{
}

public sealed class ConnectionWordField : ConnectionField
{
}

public sealed class ConnectionRowItem
{
    public string Share { get; init; } = "";

    public string Note { get; init; } = "";

    public bool HasNote => Note.Length > 0;

    public List<ConnectionField> Fields { get; } = new();
}

public sealed class ConnectionGroupItem
{
    public string Server { get; init; } = "";

    public bool IsThisPc { get; init; }

    public List<ConnectionRowItem> Rows { get; } = new();
}

// Connections from this PC: the shared folders this PC uses on other computers (the SMB client side), read for the
// signed-in user's own logon session. Read-only, and only in an unelevated window (see SmbClient).
public partial class ConnectionsPage : Page
{
    private static bool _probed;

    private IReadOnlyList<ClientConnectionGroup> _groups = Array.Empty<ClientConnectionGroup>();
    private string? _savedError;
    private bool _reading;

    public ConnectionsPage()
    {
        InitializeComponent();
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
        if (Startup.IsElevated)
        {
            AdminInfo.IsOpen = true;
            ListCard.Visibility = Visibility.Collapsed;
            Groups.Visibility = Visibility.Collapsed;
            AppLog.Write("connections", "Elevated window: the list is not read");
            return;
        }

        await ReadAsync();
        if (!_probed)
        {
            _probed = true;
            _ = Task.Run(Probes.Run);
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReadAsync();

    private async Task ReadAsync()
    {
        if (_reading)
        {
            return;
        }

        _reading = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var (groups, savedError) = await Task.Run(Read);
            _groups = groups;
            _savedError = savedError;
            ReadInfo.IsOpen = false;
            Render();
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
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _reading = false;
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
        catch (System.ComponentModel.Win32Exception ex)
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
        catch (System.ComponentModel.Win32Exception ex)
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

        EmptyText.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var items = new List<ConnectionGroupItem>();
        foreach (var g in _groups)
        {
            var group = new ConnectionGroupItem { Server = g.Server, IsThisPc = g.IsThisPc };
            foreach (var c in g.Connections)
            {
                var row = new ConnectionRowItem { Share = c.Share, Note = c.IsSignIn ? loc["Connections_SignIn"] : "" };
                row.Fields.Add(c.DriveLetter is null
                    ? new ConnectionWordField { Label = loc["Connections_Label_Drive"], Value = loc["Connections_NoDrive"] }
                    : new ConnectionDataField { Label = loc["Connections_Label_Drive"], Value = c.DriveLetter });
                row.Fields.Add(c.Account is null
                    ? new ConnectionWordField { Label = loc["Connections_Label_Account"], Value = loc["Connections_AccountUnknown"] }
                    : new ConnectionDataField { Label = loc["Connections_Label_Account"], Value = c.Account });
                row.Fields.Add(new ConnectionWordField { Label = loc["Connections_Label_State"], Value = StateText(c.State) });
                row.Fields.Add(new ConnectionWordField
                {
                    Label = loc["Connections_Label_Reconnects"],
                    Value = c.ReconnectsAtSignIn ? loc["Common_Yes"] : loc["Common_No"],
                });
                group.Rows.Add(row);
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

    // dev1 probes for the 0.9.0 design: written to the log only, once per run, from the unelevated window.
    internal static class Probes
    {
        public static void Run()
        {
            Probe("MSFT_SmbConnection (unelevated)", SmbClient.ProbeSmbConnectionCim);
            Probe("NetSessionEnum level 10 (unelevated)", SmbClient.ProbeSessionEnum);
            Probe(@"HKCU StartupApproved\Run", SmbClient.ProbeStartupApproved);
        }

        // A probe must never take the app down, whatever Windows throws.
        public static void Probe(string name, Func<string> read)
        {
            try
            {
                AppLog.Write("probe", name + ": " + read());
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                AppLog.Write("probe", name + " failed: " + ex.GetType().Name + " (0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + "): " + ex.Message);
            }
        }
    }
}
