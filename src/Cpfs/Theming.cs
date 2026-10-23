using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Cpfs;

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

public enum UiFont
{
    SegoeUiVariable,
    SegoeUi,
    Tahoma,
    Vazirmatn,
}

// Theme, brand accent and UI font. WPF-UI 4.3.0 behaviour this relies on (read in its source):
// ApplicationThemeManager.Apply swaps the theme dictionary and raises Changed; SystemThemeWatcher re-applies the
// system theme on WM_THEMECHANGED / ImmersiveColorSet; ApplicationAccentColorManager.Apply(4 colours) writes the
// accent resources for the current theme, using the secondary colour in Dark and the primary in Light.
public static class Theming
{
    private static readonly Color Tangerine = Color.FromRgb(0xFF, 0x7F, 0x45);
    private static readonly Color DeepTangerine = Color.FromRgb(0xB9, 0x44, 0x0D);

    private static Window? _window;
    private static bool _watching;
    private static bool? _hasSegoeVariable;

    // Every control gets its font from this application resource: the window references it, and so do the
    // implicit ToolTip and ContextMenu styles, which open in their own trees and don't inherit from the window.
    public const string FontResourceKey = "CpfsUiFontFamily";

    public static ThemeChoice Choice { get; private set; } = ThemeChoice.System;

    public static FontFamily SegoeVariableFamily { get; } = new("Segoe UI Variable Text, Segoe UI");

    public static FontFamily SegoeFamily { get; } = new("Segoe UI");

    public static FontFamily TahomaFamily { get; } = new("Tahoma");

    public static FontFamily VazirmatnFamily { get; } = new(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Vazirmatn");

    public static FontFamily MonoFamily { get; } = new("Cascadia Mono, Consolas");

    public static event EventHandler? ThemeApplied;

    public static void LoadPreferences()
    {
        Choice = Enum.TryParse<ThemeChoice>(Startup.Preferences.Theme, ignoreCase: true, out var theme) && Enum.IsDefined(typeof(ThemeChoice), theme)
            ? theme
            : ThemeChoice.System;
    }

    public static void Init(Window window)
    {
        _window = window;
        ApplicationThemeManager.Changed += OnThemeChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        SetTheme(Choice, save: false);
        LogFontAvailability();
    }

    public static void SetTheme(ThemeChoice choice, bool save = true)
    {
        Choice = choice;
        if (save)
        {
            Startup.Preferences.Theme = choice.ToString();
            Startup.SavePreferences();
        }

        if (_window is null)
        {
            return;
        }

        if (choice == ThemeChoice.System)
        {
            if (!_watching)
            {
                // Watch applies the system theme itself on its first registration.
                SystemThemeWatcher.Watch(_window, WindowBackdropType.Mica, updateAccents: false);
                _watching = true;
            }

            ApplicationThemeManager.ApplySystemTheme(updateAccent: false);
        }
        else
        {
            if (_watching)
            {
                SystemThemeWatcher.UnWatch(_window);
                _watching = false;
            }

            var theme = SystemParameters.HighContrast
                ? ApplicationTheme.HighContrast
                : choice == ThemeChoice.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false);
        }

        ApplyAccent();
        AppLog.Write("theme", "Choice " + choice + " -> app theme " + ApplicationThemeManager.GetAppTheme()
            + ", system theme " + ApplicationThemeManager.GetSystemTheme() + ", high contrast " + SystemParameters.HighContrast);
        ThemeApplied?.Invoke(null, EventArgs.Empty);
    }

    public static bool IsDarkSurface()
    {
        var theme = ApplicationThemeManager.GetAppTheme();
        return theme == ApplicationTheme.Dark
            || (theme == ApplicationTheme.HighContrast && ApplicationThemeManager.GetSystemTheme() != SystemTheme.HCWhite);
    }

    public static bool HasSegoeVariable => _hasSegoeVariable ??= InstalledFamilies().Overlaps(new[] { "Segoe UI Variable", "Segoe UI Variable Text" });

    public static UiFont CurrentFont => FontFor(Loc.Instance.Culture);

    // A saved font is used only when it is offered for that language.
    public static UiFont FontFor(CultureInfo culture)
    {
        var language = culture.TwoLetterISOLanguageName;
        if (Startup.Preferences.Fonts.TryGetValue(language, out var saved)
            && Enum.TryParse<UiFont>(saved, ignoreCase: true, out var font)
            && FontsFor(culture).Contains(font))
        {
            return font;
        }

        return DefaultFontFor(culture);
    }

    // Right-to-left languages (Persian, Arabic) default to Vazirmatn; the others to Segoe UI Variable where Windows has it.
    public static UiFont DefaultFontFor(CultureInfo culture) =>
        culture.TextInfo.IsRightToLeft ? UiFont.Vazirmatn : LatinDefault;

    public static IReadOnlyList<UiFont> FontsFor(CultureInfo culture) => culture.TextInfo.IsRightToLeft
        ? new[] { UiFont.Vazirmatn, UiFont.SegoeUi, UiFont.Tahoma }
        : new[] { LatinDefault, UiFont.Vazirmatn };

    private static UiFont LatinDefault => HasSegoeVariable ? UiFont.SegoeUiVariable : UiFont.SegoeUi;

    public static FontFamily FamilyOf(UiFont font) => font switch
    {
        UiFont.SegoeUiVariable => SegoeVariableFamily,
        UiFont.SegoeUi => SegoeFamily,
        UiFont.Tahoma => TahomaFamily,
        _ => VazirmatnFamily,
    };

    public static string NameOf(UiFont font) => Loc.Instance["Font_" + font];

    // Called once from the MainWindow constructor, before any page exists.
    public static void InstallFontResources(Window window)
    {
        var app = Application.Current;
        app.Resources[FontResourceKey] = FamilyOf(CurrentFont);
        foreach (var type in new[] { typeof(System.Windows.Controls.ToolTip), typeof(System.Windows.Controls.ContextMenu) })
        {
            var style = new Style(type, app.TryFindResource(type) as Style);
            style.Setters.Add(new Setter(System.Windows.Controls.Control.FontFamilyProperty, new DynamicResourceExtension(FontResourceKey)));
            app.Resources[type] = style;
        }

        window.SetResourceReference(System.Windows.Controls.Control.FontFamilyProperty, FontResourceKey);
    }

    public static void SetFont(UiFont font)
    {
        Startup.Preferences.Fonts[Loc.Instance.Culture.TwoLetterISOLanguageName] = font.ToString();
        Startup.SavePreferences();
        ApplyFont();
    }

    public static void ApplyFont()
    {
        var culture = Loc.Instance.Culture;
        var font = CurrentFont;
        Application.Current.Resources[FontResourceKey] = FamilyOf(font);
        AppLog.Write("font", "Applied " + font + " for language " + culture.Name);
    }

    private static void ApplyAccent()
    {
        var theme = ApplicationThemeManager.GetAppTheme();
        if (theme == ApplicationTheme.HighContrast)
        {
            // High contrast keeps the system colours; a brand accent would break the user's contrast choice.
            ApplicationAccentColorManager.ApplySystemAccent();
            return;
        }

        var accent = theme == ApplicationTheme.Dark ? Tangerine : DeepTangerine;
        ApplicationAccentColorManager.Apply(accent, accent, accent, accent);
    }

    private static void OnThemeChanged(ApplicationTheme theme, Color systemAccent)
    {
        // Covers the watcher's own re-apply when Windows switches light/dark while the choice is System.
        ApplyAccent();
        ThemeApplied?.Invoke(null, EventArgs.Empty);
    }

    private static void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast) && Choice != ThemeChoice.System)
        {
            SetTheme(Choice, save: false);
        }
    }

    private static HashSet<string> InstalledFamilies()
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Fonts.SystemFontFamilies)
        {
            foreach (var n in f.FamilyNames.Values)
            {
                installed.Add(n);
            }
        }

        return installed;
    }

    private static void LogFontAvailability()
    {
        var installed = InstalledFamilies();
        var wanted = new[] { "Segoe UI Variable", "Segoe UI Variable Text", "Segoe UI", "Tahoma", "Cascadia Mono", "Consolas" };
        AppLog.Write("font", "Installed: " + string.Join(", ", wanted.Select(w => w + "=" + (installed.Contains(w) ? "yes" : "no"))));
    }
}
