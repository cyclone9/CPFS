using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Cpfs.Core.Config;
using Cpfs.Core.Planning;
using Cpfs.Windows;
using Cpfs.Windows.Apply;
using Microsoft.Win32;

namespace Cpfs;

// "Cpfs.exe --auto-private", run by the CPFS-AutoPrivate task as SYSTEM from the copy in ProgramData\CPFS. It switches
// to Private only a Public network CPFS has never seen (AutoPrivateRules), writes the name to auto-private.log before it
// switches, and never writes config.json. One line per run goes to auto-private-run.log; no window, no other log.
// Exit code: the number of networks switched; a refusal or "nothing to do" is 0.
internal static class AutoPrivateVerb
{
    public const string Verb = "--auto-private";

    private static string LogPath => Path.Combine(StateReader.DataFolder, AutoPrivateLog.FileName);

    public static int Run()
    {
        try
        {
            var (count, text) = Work(null);
            RunLog(text);
            return count;
        }
        catch (Exception ex)
        {
            RunLog("failed: " + ex.GetType().Name + ": " + ex.Message);
            return 0;
        }
    }

    // configOverride: the CI test's in-process run with its own config (minus the skip list); the task passes null.
    internal static (int Switched, string Text) Work(ConfigDocument? configOverride)
    {
        var self = Path.GetFullPath(typeof(AutoPrivateVerb).Assembly.Location);
        if (configOverride is null && !string.Equals(self, Path.GetFullPath(AutoPrivateCopy.Path), StringComparison.OrdinalIgnoreCase))
        {
            return (0, "refused: not run from " + AutoPrivateCopy.Path);
        }

        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                return (0, "refused: not elevated");
            }
        }

        if (!SetupSteps.Locked())
        {
            return (0, "refused: ProgramData\\CPFS is not locked");
        }

        ConfigDocument config;
        if (configOverride is not null)
        {
            config = configOverride;
        }
        else
        {
            try
            {
                config = ConfigReader.Parse(File.ReadAllBytes(StateReader.ConfigPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ConfigFormatException)
            {
                // A file an Apply is writing right now reads as broken: do nothing.
                return (0, "nothing done: config.json not readable (" + ex.GetType().Name + ")");
            }
        }

        if (!AutoPrivateRules.Enabled(config))
        {
            return (0, "nothing done: Auto-Private is off");
        }

        var vpn = new List<string>();
        foreach (var a in PlanReader.VpnAdapters())
        {
            vpn.Add(a.Name);
        }

        var logBytes = File.Exists(LogPath) ? File.ReadAllBytes(LogPath) : null;
        var logged = logBytes is null ? Array.Empty<string>() : AutoPrivateLog.Names(logBytes, Encoding.Default);
        var decisions = AutoPrivateRules.Decide(PlanReader.Networks(), config, logged, vpn, SavedNames());

        var parts = new List<string>();
        var switched = 0;
        var loggedNow = new List<string>(logged);
        foreach (var d in decisions)
        {
            if (d.Verdict != AutoPrivateVerdict.Switch)
            {
                parts.Add(d.Name + ": " + d.Verdict);
                continue;
            }

            // Recorded first, so Uninstall puts it back even if the switch half-happens.
            if (!loggedNow.Contains(d.Name))
            {
                var bytes = AutoPrivateLog.Append(File.Exists(LogPath) ? File.ReadAllBytes(LogPath) : null, d.Name);
                using (var file = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }

                loggedNow.Add(d.Name);
            }

            Networks.SetCategoryOn(d.Name, d.InterfaceAlias!, 0, 1);
            var now = Category(d.Name, d.InterfaceAlias!);
            parts.Add(d.Name + ": " + (now == 1 ? "switched to Private" : "switch not taken (category " + now.ToString(CultureInfo.InvariantCulture) + ")"));
            if (now == 1)
            {
                switched++;
            }
        }

        return (switched, parts.Count == 0 ? "no networks" : string.Join("; ", parts));
    }

    private static int Category(string name, string alias)
    {
        foreach (var n in PlanReader.Networks())
        {
            if (string.Equals(n.Name, name, StringComparison.Ordinal) && string.Equals(n.InterfaceAlias, alias, StringComparison.OrdinalIgnoreCase))
            {
                return n.Category;
            }
        }

        return -1;
    }

    // The NetworkList profile names: networks Windows has identified and saved.
    private static List<string> SavedNames()
    {
        var list = new List<string>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var profiles = hklm.OpenSubKey(Reg.NetworkProfiles);
        foreach (var id in profiles?.GetSubKeyNames() ?? Array.Empty<string>())
        {
            using var p = profiles!.OpenSubKey(id);
            if (p?.GetValue("ProfileName") is string name)
            {
                list.Add(name);
            }
        }

        return list;
    }

    private static void RunLog(string text)
    {
        try
        {
            var path = Path.Combine(StateReader.DataFolder, AutoPrivateLog.RunLogFileName);
            if (!Directory.Exists(StateReader.DataFolder))
            {
                return;
            }

            var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + text;
            File.WriteAllText(path, AutoPrivateLog.RunLog(existing, line), new UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // The task's result is its exit code; a run log that can't be written changes nothing.
        }
    }
}
