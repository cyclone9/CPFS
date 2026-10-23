using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cpfs.Core.Config;
using Cpfs.Core.Connect;
using Cpfs.Core.Qr;
using Cpfs.Windows;
using Wpf.Ui.Controls;

namespace Cpfs.Pages;

public abstract class GuideItem
{
}

public sealed class StepItem : GuideItem
{
    public string Text { get; init; } = "";
}

public sealed class NoteItem : GuideItem
{
    public string Text { get; init; } = "";
}

public sealed class ValueItem : GuideItem
{
    public string Label { get; init; } = "";

    public string Value { get; init; } = "";

    public string CopyName { get; init; } = "";
}

public sealed class QrItem : GuideItem
{
    public ImageSource? Image { get; init; }

    public string Name { get; init; } = "";

    public string Caption { get; init; } = "";
}

public sealed class GuideCard
{
    public string Title { get; init; } = "";

    public SymbolRegular Icon { get; init; }

    public List<GuideItem> Items { get; } = new();
}

public sealed class AddressOption
{
    internal AddressOption(ConnectAddress address)
    {
        Address = address;
    }

    internal ConnectAddress Address { get; }

    public string Host => Address.Host;

    public string Description
    {
        get
        {
            var loc = Loc.Instance;
            return Address.Kind switch
            {
                AddressKind.Mdns => loc["Connect_Kind_Mdns"],
                AddressKind.Lan => loc.FormatValues("Connect_Kind_Lan", Address.Adapter),
                AddressKind.Vpn => loc.FormatValues("Connect_Kind_Vpn", Address.Adapter),
                _ => loc["Connect_Kind_MagicDns"],
            };
        }
    }

    // Screen readers read a ComboBox item through its ToString.
    public override string ToString() => Host + ", " + Description;
}

// The connect guide: every address of this PC, and for each kind of device the steps, the values to copy and a QR
// code where the device can use one. Needs no administrator rights and changes nothing; the password appears nowhere.
public partial class ConnectPage : Page
{
    private ConfigDocument? _config;
    private ConfigState _configState;
    private string? _configError;
    private string _hostName = "";
    private bool _loaded;
    private bool _syncing;

    public ConnectPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Loc.Culture) && _loaded)
            {
                // The address descriptions are built from the language; re-create the items, keep the choice.
                RefillAddresses(AddressBox.ItemsSource as List<AddressOption> ?? new List<AddressOption>());
                Render();
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await ReadAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReadAsync();

    private async Task ReadAsync()
    {
        RefreshButton.IsEnabled = false;
        try
        {
            var (state, config, error, host, addresses) = await Task.Run(() =>
            {
                var s = ConfigFile.TryRead(out var c, out var err);
                var h = NetworkAddresses.HostName();
                var list = ConnectAddresses.Build(h, NetworkAddresses.Read());
                return (s, c, err, h, list);
            });

            _configState = state;
            _config = config;
            _configError = error;
            _hostName = host;
            AppLog.Write("connect", "Config " + state + "; host " + host + "; addresses " + string.Join(", ", Hosts(addresses)));

            var options = new List<AddressOption>();
            foreach (var a in addresses)
            {
                options.Add(new AddressOption(a));
            }

            RefillAddresses(options);
            Render();
        }
        catch (Exception ex) when (State.IsExpected(ex) || ex is System.Net.NetworkInformation.NetworkInformationException)
        {
            AppLog.Exception("connect", ex);
            Show(ConfigInfo, InfoBarSeverity.Error, Loc.Instance["Connect_ConfigFailed_Title"], ex.Message);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private static IEnumerable<string> Hosts(IEnumerable<ConnectAddress> addresses)
    {
        foreach (var a in addresses)
        {
            yield return a.Host + " (" + a.Kind + ")";
        }
    }

    private void RefillAddresses(List<AddressOption> current)
    {
        var selected = (AddressBox.SelectedItem as AddressOption)?.Host;
        var options = new List<AddressOption>();
        foreach (var o in current)
        {
            options.Add(new AddressOption(o.Address));
        }

        _syncing = true;
        AddressBox.ItemsSource = options;
        AddressBox.SelectedItem = options.Find(o => string.Equals(o.Host, selected, StringComparison.OrdinalIgnoreCase)) ?? (options.Count > 0 ? options[0] : null);
        _syncing = false;
    }

    private void OnAddressSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && _loaded)
        {
            Render();
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ValueItem item })
        {
            Feedback.Copy(item.Label, item.Value);
        }
    }

    private void Render()
    {
        var loc = Loc.Instance;
        var share = _config?.ShareName;
        var user = _config?.SmbUser;
        var installed = _configState == ConfigState.Found && !string.IsNullOrWhiteSpace(share) && !string.IsNullOrWhiteSpace(user);

        switch (_configState)
        {
            case ConfigState.Missing:
                Show(ConfigInfo, InfoBarSeverity.Warning, loc["Connect_NoInstall_Title"], loc["Connect_NoInstall_Msg"]);
                break;
            case ConfigState.Unreadable:
                Show(ConfigInfo, InfoBarSeverity.Error, loc["Connect_ConfigFailed_Title"], loc.FormatValues("Connect_ConfigFailed_Msg", _configError));
                break;
            default:
                if (installed)
                {
                    ConfigInfo.IsOpen = false;
                }
                else
                {
                    Show(ConfigInfo, InfoBarSeverity.Warning, loc["Connect_Incomplete_Title"], loc["Connect_Incomplete_Msg"]);
                }

                break;
        }

        // Until the restart, devices still find the PC by its old name (the 0.2.3 guide said the same).
        var configured = _config?.HostName;
        if (installed && !string.IsNullOrWhiteSpace(configured) && !string.Equals(configured, _hostName, StringComparison.OrdinalIgnoreCase))
        {
            Show(RenameInfo, InfoBarSeverity.Warning, loc["Connect_Rename_Title"], loc.FormatValues("Connect_Rename_Msg", configured, _hostName));
        }
        else
        {
            RenameInfo.IsOpen = false;
        }

        var strict = installed && _config?.Strict == true;
        if (strict)
        {
            Show(StrictInfo, InfoBarSeverity.Informational, loc["Connect_Strict_Title"], loc["Connect_Strict_Msg"]);
        }
        else
        {
            StrictInfo.IsOpen = false;
        }

        if (AddressBox.SelectedItem is not AddressOption option)
        {
            WhereText.Text = "";
            AccountItems.ItemsSource = null;
            Guides.ItemsSource = null;
            return;
        }

        var v = new ConnectValues(
            option.Host,
            installed ? share! : loc["Connect_Placeholder_Share"],
            installed ? user! : loc["Connect_Placeholder_User"],
            Environment.MachineName,
            strict);
        WhereText.Text = option.Address.ThroughVpn ? loc["Connect_Where_Vpn"] : loc["Connect_Where_Lan"];

        AccountItems.ItemsSource = new List<GuideItem>
        {
            Value("Connect_Label_Share", v.Share),
            Value("Connect_Label_User", v.User),
            Value("Connect_Label_Domain", v.Domain),
        };

        // Placeholder values make no usable QR code, so none is drawn until CPFS is set up.
        Guides.ItemsSource = new List<GuideCard>
        {
            Ios(v, installed),
            Android(v, installed),
            Windows(v),
            Mac(v, installed),
            Linux(v, installed),
        };
    }

    private static GuideCard Windows(ConnectValues v)
    {
        var card = Card("Connect_Win_Title", SymbolRegular.Desktop24);
        card.Items.Add(Step("Connect_Win_Step1"));
        card.Items.Add(Value("Connect_Label_Unc", v.Unc));
        card.Items.Add(Step("Connect_Win_Step2", v.QualifiedUser));
        card.Items.Add(Value("Connect_Label_SignIn", v.QualifiedUser));
        card.Items.Add(Step("Connect_Win_Step3"));
        card.Items.Add(Step("Connect_Win_Step4"));
        card.Items.Add(Value("Connect_Label_NetUse", v.NetUse));
        card.Items.Add(new StepItem { Text = Loc.Instance["Connect_Win_NoQr"] });
        if (v.Strict)
        {
            card.Items.Add(Note("Connect_Win_Strict"));
        }

        return card;
    }

    private static GuideCard Mac(ConnectValues v, bool qr)
    {
        var card = Card("Connect_Mac_Title", SymbolRegular.Laptop24);
        card.Items.Add(Step("Connect_Mac_Step1"));
        card.Items.Add(Value("Connect_Label_SmbUrl", v.SmbUrl));
        card.Items.Add(Step("Connect_Mac_Step2", v.User));
        AddQr(card, qr, v.SmbUrl, "Connect_Mac_Qr");
        if (v.Strict)
        {
            card.Items.Add(Note("Connect_Mac_Strict"));
        }

        return card;
    }

    private static GuideCard Linux(ConnectValues v, bool qr)
    {
        var card = Card("Connect_Linux_Title", SymbolRegular.WindowConsole20);
        card.Items.Add(Step("Connect_Linux_Step1"));
        card.Items.Add(Value("Connect_Label_SmbUrl", v.SmbUrl));
        card.Items.Add(Step("Connect_Linux_Step2", v.User));
        AddQr(card, qr, v.SmbUrl, "Connect_Linux_Qr");
        card.Items.Add(Step("Connect_Linux_Step3"));
        card.Items.Add(Value("Connect_Label_MountDir", ConnectValues.MountDir));
        card.Items.Add(Value("Connect_Label_Mount", v.Mount));
        if (v.IsLocalName)
        {
            card.Items.Add(Note("Connect_Linux_Local"));
        }

        if (v.Strict)
        {
            card.Items.Add(Note("Connect_Linux_Strict"));
        }

        return card;
    }

    private static GuideCard Android(ConnectValues v, bool qr)
    {
        var card = Card("Connect_Android_Title", SymbolRegular.Phone24);
        card.Items.Add(Step("Connect_Android_Step1"));
        card.Items.Add(Step("Connect_Android_Step2"));
        AddQr(card, qr, v.Host, "Connect_Android_Qr");
        card.Items.Add(Value("Connect_Label_Host", v.Host));
        card.Items.Add(Value("Connect_Label_Path", v.Share));
        card.Items.Add(Value("Connect_Label_User", v.User));
        card.Items.Add(Step("Connect_Android_Step3"));
        if (v.IsLocalName)
        {
            card.Items.Add(Note("Connect_Android_Local"));
        }

        if (v.Strict)
        {
            card.Items.Add(Note("Connect_Android_Strict"));
        }

        return card;
    }

    private static GuideCard Ios(ConnectValues v, bool qr)
    {
        var card = Card("Connect_Ios_Title", SymbolRegular.Phone24);
        card.Items.Add(Step("Connect_Ios_Step1"));
        AddQr(card, qr, v.IosUrl, "Connect_Ios_Qr");
        card.Items.Add(Step("Connect_Ios_Step2"));
        card.Items.Add(Value("Connect_Label_IosUrl", v.IosUrl));
        card.Items.Add(Step("Connect_Ios_Step3", v.User));
        card.Items.Add(new StepItem { Text = Loc.Instance["Connect_Ios_Version"] });
        if (v.Strict)
        {
            card.Items.Add(Note("Connect_Ios_Strict"));
        }

        return card;
    }

    private static GuideCard Card(string titleKey, SymbolRegular icon) => new() { Title = Loc.Instance[titleKey], Icon = icon };

    private static StepItem Step(string key, params string[] values) =>
        new() { Text = values.Length == 0 ? Loc.Instance[key] : Loc.Instance.FormatValues(key, values) };

    private static NoteItem Note(string key) => new() { Text = Loc.Instance[key] };

    private static ValueItem Value(string labelKey, string value)
    {
        var label = Loc.Instance[labelKey];
        return new ValueItem { Label = label, Value = value, CopyName = Loc.Instance.Format("Common_CopyName", label) };
    }

    private static void AddQr(GuideCard card, bool show, string text, string captionKey)
    {
        if (!show)
        {
            return;
        }

        var loc = Loc.Instance;
        try
        {
            card.Items.Add(new QrItem
            {
                Image = QrImages.Create(text),
                Name = loc.FormatValues("Connect_Qr_Name", text),
                Caption = loc[captionKey],
            });
        }
        catch (QrCapacityException ex)
        {
            AppLog.Write("connect", "QR code not made: " + ex.Message);
            card.Items.Add(new NoteItem { Text = loc.Format("Connect_QrFailed", ex.Message) });
        }
    }

    private static void Show(InfoBar bar, InfoBarSeverity severity, string title, string message)
    {
        bar.Severity = severity;
        bar.Title = title;
        bar.Message = message;
        bar.IsOpen = true;
    }
}
