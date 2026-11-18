using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Cpfs.Core.Planning;
using Wpf.Ui.Controls;

namespace Cpfs.Pages;

public sealed class StatusFactView
{
    public string Text { get; init; } = "";

    // The status dot's state name (Styles.xaml, StatusDot).
    public string Kind { get; init; } = "";
}

// The status card at the top of Setup and the fixed action bar at its bottom. The card claims only what was read
// (SharingStatus); the bar repeats the summary's first sentence beside Show details and the one main button.
public partial class SetupPage
{
    private SharingStatusView? _status;
    private string? _address;

    private void ComputeStatus(SystemSnapshot snapshot, bool? serverRunning)
    {
        var host = snapshot.ComputerNames.Value?.Active ?? snapshot.ComputerName;
        _status = SharingStatus.Decide(_config is not null || ConfigUnreadable, _config?.ShareName, host, snapshot.Shares, serverRunning,
            snapshot.Networks, snapshot.VpnAdapters, snapshot.OwnRules);
        AppLog.Write("setup", "Status card: " + _status.State + "; facts " + string.Join(", ", System.Linq.Enumerable.Select(_status.Facts, f => f.Key + " " + f.Tone)));
        RenderStatus();
    }

    private void RenderStatus()
    {
        var loc = Loc.Instance;
        var state = _status?.State ?? (IsFirstInstall ? SharingState.NotSetUp : SharingState.Unknown);
        var reading = _status is null && !IsFirstInstall;
        StatusTitle.Text = reading ? loc["Common_ReadingPc"] : state switch
        {
            SharingState.NotSetUp => loc["Setup_Welcome_Title"],
            SharingState.On => loc["Setup_Status_On"],
            SharingState.NotRunning => loc["Setup_Status_NotRunning"],
            _ => loc["Setup_Status_Unknown"],
        };
        var desc = reading ? "" : state switch
        {
            SharingState.NotSetUp => loc["Setup_Welcome_Desc"],
            SharingState.NotRunning => loc["Setup_Status_NotRunning_Desc"],
            SharingState.Unknown => loc[Startup.IsElevated ? "Setup_Status_Unknown_DescAdmin" : "Setup_Status_Unknown_Desc"],
            _ => "",
        };
        StatusDesc.Text = desc;
        StatusDesc.Visibility = desc.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        _address = _status?.Address;
        StatusAddress.Visibility = _address is null ? Visibility.Collapsed : Visibility.Visible;
        AddressSlashes.Text = _address is null ? "" : @"\\";
        AddressRest.Text = _address is null ? "" : _address.Substring(2);
        CopyAddressButton.Visibility = _address is null ? Visibility.Collapsed : Visibility.Visible;

        var facts = new List<StatusFactView>();
        foreach (var f in _status?.Facts ?? Array.Empty<StatusFact>())
        {
            facts.Add(new StatusFactView
            {
                Text = loc[f.Key],
                Kind = f.Tone switch { StatusTone.Success => "Success", StatusTone.Caution => "Caution", _ => "" },
            });
        }

        StatusFacts.ItemsSource = facts;
        StatusFacts.Visibility = facts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var folder = SharedFolder();
        OpenFolderButton.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
        StatusActions.Visibility = _address is null && folder is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // The tray's rule (B4): only from a config.json whose folder was read and trusted, and only an existing folder.
    private string? SharedFolder() =>
        _status is { State: not SharingState.NotSetUp } && _snapshot is { } snapshot ? Windows.TrayReader.FolderOf(_config, snapshot.DataFolder) : null;

    private void OnCopyAddress(object sender, RoutedEventArgs e)
    {
        if (_address is { } address)
        {
            Feedback.Copy(sender, "share address", address);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var folder = SharedFolder();
        if (folder is null)
        {
            return;
        }

        try
        {
            Windows.FolderOpener.Open(folder);
            AppLog.Write("setup", "Opened the shared folder in File Explorer");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Write("setup", "Open folder: " + ex.GetType().Name + ": " + ex.Message);
            Show(AdminResult, InfoBarSeverity.Error, Loc.Instance["Setup_Status_OpenFailed"], ex.Message);
        }
    }

    // The bar's Show details opens the full plan and scrolls to it.
    private void OnShowDetails(object sender, RoutedEventArgs e)
    {
        DetailsExpander.IsExpanded = true;
        DetailsExpander.BringIntoView();
    }
}
