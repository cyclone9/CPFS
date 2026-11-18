using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Cpfs.Core.Config;
using Cpfs.Core.Planning;
using Cpfs.Core.Preferences;

namespace Cpfs.Windows;

// Preferences: null when preferences.json could not be read this time (the tray keeps what it had).
// Detail: what could not be read, in English for the log; empty when everything was read.
internal sealed record TrayReading(AppPreferences? Preferences, TrayState State, string? ShareName, string? Folder, string Detail);

// The tray's reads, all of them ones the unelevated Setup page already makes. Runs on a thread-pool thread and
// writes nothing to the log itself: the tray logs only what changed.
internal static class TrayReader
{
    public static TrayReading Read()
    {
        var failures = new List<string>();
        var prefs = ReadPreferences(out var prefsError);
        if (prefsError is not null)
        {
            failures.Add("preferences.json: " + prefsError);
        }

        var config = ReadConfig(failures, out var folder, out var found, out var unreadable);
        var ignored = found && ConfigTrust.Ignored(folder);
        var knownMissing = !found && !unreadable && ConfigFile.KnownMissing(log: false);
        var installed = TrayStatus.Installed(found, unreadable, knownMissing, ignored);
        var shareName = ignored ? null : config?.ShareName;

        var state = TrayState.NotSetUp;
        if (installed)
        {
            var shares = Part("shares", PlanReader.Shares, failures);
            var running = PlanReader.ServerRunning(log: false);
            if (running is null)
            {
                failures.Add("Server service: not read");
            }

            var networks = Part("networks", PlanReader.Networks, failures);
            state = TrayStatus.Decide(true, shareName, shares, running, networks, PlanReader.VpnAdapters());
        }

        return new TrayReading(prefs, state, shareName, FolderOf(config, folder), string.Join("; ", failures));
    }

    // Read again when "Open the shared folder" is clicked, so the click acts on what is true now (design B4).
    public static string? TrustedFolder()
    {
        var config = ReadConfig(new List<string>(), out var folder, out _, out _);
        return FolderOf(config, folder);
    }

    public static AppPreferences? ReadPreferences(out string? error)
    {
        error = null;
        try
        {
            return File.Exists(Startup.PreferencesPath)
                ? AppPreferences.Parse(File.ReadAllText(Startup.PreferencesPath, Encoding.UTF8))
                : new AppPreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private static ConfigDocument? ReadConfig(List<string> failures, out Reading<DataFolderInfo> folder, out bool found, out bool unreadable)
    {
        var state = ConfigFile.TryRead(out var config, out var error, log: false);
        found = state == ConfigState.Found;
        unreadable = state == ConfigState.Unreadable;
        if (unreadable)
        {
            failures.Add("config.json: " + error);
        }

        folder = found ? Part("data folder", PlanReader.DataFolder, failures) : Reading.Failed<DataFolderInfo>(null);
        return config;
    }

    // Only from a config.json that passes ConfigTrust's owner and link checks, and only a folder that exists:
    // Explorer given a file would run it, and an untrusted config.json could name any file.
    private static string? FolderOf(ConfigDocument? config, Reading<DataFolderInfo> folder)
    {
        var path = config?.SharePath;
        return ConfigTrust.Trusted(folder) && !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && Directory.Exists(path) ? path : null;
    }

    private static Reading<T> Part<T>(string name, Func<T> read, List<string> failures)
    {
        try
        {
            return Reading.Ok(read());
        }
        catch (Exception ex) when (State.IsExpected(ex) || ex is IdentityNotMappedException)
        {
            var inner = ex is System.Reflection.TargetInvocationException { InnerException: { } i } ? i : ex;
            var text = inner.GetType().Name + ": " + inner.Message;
            failures.Add(name + ": " + text);
            return Reading.Failed<T>(text);
        }
    }
}
