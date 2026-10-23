using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Resources;
using System.Threading;
using Cpfs.Core.Text;

namespace Cpfs;

// Strings for the current UI culture, bindable as {Binding [Key], Source={x:Static local:Loc.Instance}}.
// Every language is a .resources stream inside the exe, so there is no satellite folder to ship.
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    // The app's languages, in menu order.
    public static IReadOnlyList<string> Languages { get; } = new[] { "en", "fa", "ar", "tr" };

    private readonly Dictionary<string, ResourceSet?> _sets = new(StringComparer.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");

    public bool IsRtl => Culture.TextInfo.IsRightToLeft;

    public string this[string key] => Get(key);

    public static string StartLanguage(string? saved)
    {
        if (saved is not null && Languages.Contains(saved, StringComparer.OrdinalIgnoreCase))
        {
            return saved.ToLowerInvariant();
        }

        var windows = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.FirstOrDefault(l => string.Equals(l, windows, StringComparison.OrdinalIgnoreCase)) ?? "en";
    }

    public void SetCulture(CultureInfo culture)
    {
        Culture = culture;

        // Code should pass Culture explicitly; this only keeps anything that forgets (WPF controls, thread pool work)
        // on the app's language rather than the Windows regional format.
        Thread.CurrentThread.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
    }

    public string Get(string key)
    {
        for (var c = Culture; ; c = c.Parent)
        {
            var value = Set(c.Name)?.GetString(key);
            if (value is not null)
            {
                return value;
            }

            if (c.Equals(CultureInfo.InvariantCulture))
            {
                return "!" + key + "!";
            }
        }
    }

    // English is the source; the other files carry Meta_Translated = "false" until the writer has translated them.
    public bool IsTranslated(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == "en"
        || string.Equals(Set(culture.TwoLetterISOLanguageName)?.GetString("Meta_Translated"), "true", StringComparison.OrdinalIgnoreCase);

    public string Format(string key, params object[] args) => string.Format(Culture, Get(key), args);

    // For prose that carries values (paths, names, addresses): each value keeps its own direction in RTL text.
    public string FormatValues(string key, params string?[] values)
    {
        var embedded = new object[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            embedded[i] = Bidi.Embed(values[i] ?? "", IsRtl);
        }

        return string.Format(Culture, Get(key), embedded);
    }

    private ResourceSet? Set(string cultureName)
    {
        if (_sets.TryGetValue(cultureName, out var set))
        {
            return set;
        }

        var resource = cultureName.Length == 0 ? "Cpfs.Strings.resources" : "Cpfs.Strings." + cultureName + ".resources";
        var stream = typeof(Loc).Assembly.GetManifestResourceStream(resource);
        set = stream is null ? null : new ResourceSet(stream);
        _sets[cultureName] = set;
        return set;
    }
}
