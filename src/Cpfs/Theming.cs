using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
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
    private static readonly Lazy<HashSet<string>> Installed = new(InstalledFamilies);
    private static readonly Dictionary<string, string> ResolvedCache = new(StringComparer.Ordinal);

    // Every control gets its font from this application resource: the window references it, and so do the
    // implicit ToolTip and ContextMenu styles, which open in their own trees and don't inherit from the window.
    public const string FontResourceKey = "CpfsUiFontFamily";

    // Page titles and large text (WPF-UI's Subtitle and Title sizes): Segoe UI Variable's Display optical size.
    public const string TitleFontResourceKey = "CpfsTitleFontFamily";

    // WPF knows Segoe UI Variable only by its named instances; "Segoe UI Variable" alone silently becomes Segoe UI.
    private const string VariableText = "Segoe UI Variable Text";
    private const string VariableDisplay = "Segoe UI Variable Display";

    public static ThemeChoice Choice { get; private set; } = ThemeChoice.System;

    public static FontFamily SegoeVariableFamily { get; } = new(VariableText + ", Segoe UI");

    public static FontFamily SegoeVariableTitleFamily { get; } = new(VariableDisplay + ", " + VariableText + ", Segoe UI");

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

    // Windows 11 ships it; Windows 10 doesn't, and gets Segoe UI.
    public static bool HasSegoeVariable => Installed.Value.Contains(VariableText);

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

    public static FontFamily TitleFamilyOf(UiFont font) => font == UiFont.SegoeUiVariable ? SegoeVariableTitleFamily : FamilyOf(font);

    public static string NameOf(UiFont font) => Loc.Instance["Font_" + font];

    public static string Describe()
    {
        var font = CurrentFont;
        return font + ": body " + Resolve(FamilyOf(font), FontWeights.Normal) + "; title " + Resolve(TitleFamilyOf(font), FontWeights.SemiBold);
    }

    // Called once from the MainWindow constructor, before any page exists.
    public static void InstallFontResources(Window window)
    {
        var app = Application.Current;
        app.Resources[FontResourceKey] = FamilyOf(CurrentFont);
        app.Resources[TitleFontResourceKey] = TitleFamilyOf(CurrentFont);
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
        Application.Current.Resources[TitleFontResourceKey] = TitleFamilyOf(font);
        AppLog.Write("font", "Applied " + font + " for language " + culture.Name + "; resolved " + Describe());
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

    // The face WPF loads for a family at a weight. A family list resolves to its first installed member, which is
    // what WPF uses for Latin text; the file name and any simulated bold show whether the named instance was found.
    private static string Resolve(FontFamily family, FontWeight weight)
    {
        var key = family.Source + "|" + weight;
        if (ResolvedCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        FontFamily? chosen = family;
        if (family.BaseUri is null)
        {
            chosen = null;
            foreach (var name in family.Source.Split(','))
            {
                if (Installed.Value.Contains(name.Trim()))
                {
                    chosen = new FontFamily(name.Trim());
                    break;
                }
            }
        }

        string result;
        if (chosen is null)
        {
            result = "none of '" + family.Source + "' installed, WPF fallback";
        }
        else if (!new Typeface(chosen, FontStyles.Normal, weight, FontStretches.Normal).TryGetGlyphTypeface(out var glyph))
        {
            result = chosen.Source + " (composite font)";
        }
        else
        {
            string file;
            try
            {
                file = Path.GetFileName(glyph.FontUri.AbsolutePath);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                file = "?";
            }

            result = English(glyph.FamilyNames) + " " + English(glyph.FaceNames) + " (" + file
                + (glyph.StyleSimulations == StyleSimulations.None ? "" : ", simulated " + glyph.StyleSimulations) + ")";
        }

        ResolvedCache[key] = result;
        return result;
    }

    private static string English(IDictionary<CultureInfo, string> names) =>
        names.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out var name) ? name : names.Values.FirstOrDefault() ?? "?";

    private static void LogFontAvailability()
    {
        var installed = Installed.Value;
        var wanted = new[] { "Segoe UI Variable", VariableText, VariableDisplay, "Segoe UI", "Tahoma", "Cascadia Mono", "Consolas" };
        AppLog.Write("font", "Installed: " + string.Join(", ", wanted.Select(w => w + "=" + (installed.Contains(w) ? "yes" : "no"))));
    }
}
