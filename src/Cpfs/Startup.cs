using System;
using System.IO;
using System.Security;
using System.Text;
using Cpfs.Core.Elevation;
using Cpfs.Core.Preferences;

namespace Cpfs;

// What this instance was started with: elevation, the hand-over from the unelevated instance, the preferences.
internal static class Startup
{
    public static bool IsElevated { get; private set; }

    public static RelaunchArguments Arguments { get; private set; } = new();

    // True when this instance was started by "Continue as administrator" (and really is elevated).
    public static bool IsHandOver => IsElevated && Arguments.Elevated;

    public static AppPreferences Preferences { get; private set; } = new();

    public static string PreferencesPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CPFS", "preferences.json");

    public static void Init(string[] args)
    {
        IsElevated = AppLog.IsElevated();
        Arguments = RelaunchArgs.Parse(args);
        if (Arguments.Rejected.Count > 0)
        {
            AppLog.Write("start", "Rejected command-line values: " + string.Join(" ", Arguments.Rejected));
        }

        if (Arguments.Elevated && !IsElevated)
        {
            AppLog.Write("start", "--elevated given but the process is not elevated; the hand-over is ignored");
        }

        Preferences = LoadPreferences();

        // An over-the-shoulder elevation runs as another account with its own %LOCALAPPDATA%, so the hand-over
        // carries the choices of the instance that asked for it.
        if (IsHandOver)
        {
            if (Arguments.Theme is not null)
            {
                Preferences.Theme = Arguments.Theme;
            }

            if (Arguments.Language is not null)
            {
                Preferences.Language = Arguments.Language;
            }

            if (Arguments.Language is not null && Arguments.Font is not null)
            {
                Preferences.Fonts[Arguments.Language] = Arguments.Font;
            }
        }
    }

    public static void SavePreferences()
    {
        try
        {
            var dir = Path.GetDirectoryName(PreferencesPath)!;
            Directory.CreateDirectory(dir);
            var temp = PreferencesPath + ".tmp";
            File.WriteAllText(temp, Preferences.Serialize(), new UTF8Encoding(false));
            if (File.Exists(PreferencesPath))
            {
                File.Replace(temp, PreferencesPath, null);
            }
            else
            {
                File.Move(temp, PreferencesPath);
            }

            AppLog.Write("prefs", "Saved " + PreferencesPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            AppLog.Write("prefs", "Could not save " + PreferencesPath + ": " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static AppPreferences LoadPreferences()
    {
        try
        {
            if (!File.Exists(PreferencesPath))
            {
                AppLog.Write("prefs", "No saved preferences; defaults");
                return new AppPreferences();
            }

            var prefs = AppPreferences.Parse(File.ReadAllText(PreferencesPath, Encoding.UTF8));
            AppLog.Write("prefs", "Loaded " + PreferencesPath + ": theme " + prefs.Theme + ", language " + (prefs.Language ?? "(Windows)"));
            return prefs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            AppLog.Write("prefs", "Could not read " + PreferencesPath + ": " + ex.GetType().Name + ": " + ex.Message);
            return new AppPreferences();
        }
    }
}
