using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Cpfs.Pages;

public sealed class LanguageOption
{
    public LanguageOption(string code)
    {
        Code = code;
        var culture = CultureInfo.GetCultureInfo(code);
        NativeName = Loc.Instance["LangNative_" + code];
        UiName = Loc.Instance["Lang_" + code];
        NativeFlow = culture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        NativeLanguage = XmlLanguage.GetLanguage(culture.IetfLanguageTag);

        // The second name only when it adds something: not for the UI language itself.
        UiNameVisibility = string.Equals(code, Loc.Instance.Culture.TwoLetterISOLanguageName, System.StringComparison.Ordinal)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public string Code { get; }

    public string NativeName { get; }

    public string UiName { get; }

    public FlowDirection NativeFlow { get; }

    public XmlLanguage NativeLanguage { get; }

    public Visibility UiNameVisibility { get; }

    // Screen readers read the item through its ToString.
    public override string ToString() => UiNameVisibility == Visibility.Visible ? NativeName + ", " + UiName : NativeName;
}

public partial class PreferencesPage : Page
{
    private bool _syncing;

    public PreferencesPage()
    {
        InitializeComponent();
        Refill();
        _syncing = true;
        UpdatesSwitch.IsChecked = Startup.Preferences.CheckUpdatesAtStart;
        _syncing = false;
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Loc.Culture))
            {
                // Not inside the language box's own SelectionChanged: its ItemsSource is replaced.
                Dispatcher.BeginInvoke(new System.Action(Refill));
            }
        };
    }

    private void OnUpdatesSwitch(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        Startup.Preferences.CheckUpdatesAtStart = UpdatesSwitch.IsChecked == true;
        Startup.SavePreferences();
        AppLog.Write("prefs", "Check for updates at start " + (Startup.Preferences.CheckUpdatesAtStart ? "on" : "off"));
    }

    private void Refill()
    {
        _syncing = true;
        ThemeBox.SelectedIndex = (int)Theming.Choice;

        var options = new List<LanguageOption>();
        foreach (var code in Loc.Languages)
        {
            options.Add(new LanguageOption(code));
        }

        LanguageBox.ItemsSource = options;
        LanguageBox.SelectedItem = options.Find(o => o.Code == Loc.Instance.Culture.TwoLetterISOLanguageName);
        FillFonts();
        ShowTranslationState();
        _syncing = false;
    }

    // The list depends on the language: right-to-left languages get fonts with Arabic-script glyphs.
    private void FillFonts()
    {
        FontBox.Items.Clear();
        var current = Theming.CurrentFont;
        foreach (var font in Theming.FontsFor(Loc.Instance.Culture))
        {
            var item = new ComboBoxItem { Content = Theming.NameOf(font), Tag = font, FontFamily = Theming.FamilyOf(font) };
            FontBox.Items.Add(item);
            if (font == current)
            {
                FontBox.SelectedItem = item;
            }
        }
    }

    private void ShowTranslationState()
    {
        var culture = Loc.Instance.Culture;
        if (Loc.Instance.IsTranslated(culture))
        {
            LanguageInfo.IsOpen = false;
            return;
        }

        LanguageInfo.Title = Loc.Instance["Lang_NotTranslated_Title"];
        LanguageInfo.Message = Loc.Instance.FormatValues("Lang_NotTranslated_Msg", Loc.Instance["LangNative_" + culture.TwoLetterISOLanguageName]);
        LanguageInfo.IsOpen = true;
    }

    private void OnThemeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && ThemeBox.SelectedItem is ComboBoxItem { Tag: ThemeChoice choice })
        {
            Theming.SetTheme(choice);
        }
    }

    private void OnLanguageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && LanguageBox.SelectedItem is LanguageOption option)
        {
            MainWindow.Current?.SetLanguage(CultureInfo.GetCultureInfo(option.Code));
        }
    }

    private void OnFontSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && FontBox.SelectedItem is ComboBoxItem { Tag: UiFont font })
        {
            Theming.SetFont(font);
        }
    }
}
