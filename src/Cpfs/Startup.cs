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

    // True when the elevated window's "Reopen without administrator rights" started this instance (and it really is
    // not elevated).
    public static bool IsReopen => !IsElevated && Arguments.Reopened && !Arguments.Elevated;

    public static AppPreferences Preferences { get; private set; } = new();

    // Started by the updater (--after-update <the version it replaced>), or after its rollback (--update-failed <the
    // version that did not start>). Ignored in an elevated process: the updater never starts one.
    public static string? AfterUpdateFrom { get; private set; }

    public static string? UpdateFailedVersion { get; private set; }

    public static bool IsUpdateRestart => AfterUpdateFrom is not null || UpdateFailedVersion is not null;

    public static string PreferencesPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CPFS", "preferences.json");

    public static void Init(string[] args)
    {
        IsElevated = AppLog.IsElevated();
        args = Core.Updates.UpdateRules.TakeRestartOptions(args, out var afterUpdate, out var updateFailed);
        var refuse = IsElevated;
#if CPFS_UPDATE_TEST
        // The CI runner runs everything elevated; its headless restart is a test build's only exception.
        refuse &= !Windows.Update.UpdateTestHooks.Headless;
#endif
        if ((afterUpdate ?? updateFailed) is not null && refuse)
        {
            AppLog.Write("start", "Update restart options ignored in an elevated process");
        }
        else
        {
            AfterUpdateFrom = afterUpdate;
            UpdateFailedVersion = updateFailed;
            if (afterUpdate is not null)
            {
                AppLog.Write("update", "Started after update from " + afterUpdate);
            }

            if (updateFailed is not null)
            {
                AppLog.Write("update", "Started again after version " + updateFailed + " failed to start; it was rolled back");
            }
        }

        Arguments = RelaunchArgs.Parse(args);
        if (Arguments.Rejected.Count > 0)
        {
            AppLog.Write("start", "Rejected command-line values: " + string.Join(" ", Arguments.Rejected));
        }

        if (Arguments.Elevated && !IsElevated)
        {
            AppLog.Write("start", "--elevated given but the process is not elevated; the hand-over is ignored");
        }

        if (Arguments.Reopened && (IsElevated || Arguments.Elevated))
        {
            AppLog.Write("start", "--reopened given to an elevated start; ignored");
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

    // Returns the error for the user, or null when saved. Most callers only log it.
    public static string? SavePreferences()
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
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            AppLog.Write("prefs", "Could not save " + PreferencesPath + ": " + ex.GetType().Name + ": " + ex.Message);
            return ex.Message;
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
