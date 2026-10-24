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

// "Cpfs.exe --auto-private", run by the CPFS-AutoPrivate task as SYSTEM from the copy in Program Files\CPFS. It switches
// to Private only a Public network CPFS has never seen (AutoPrivateRules), writes the name to auto-private.log before it
// switches, and never writes config.json. One line per run goes to auto-private-run.log (only into a locked folder);
// no window, no other log. The exit code (AutoPrivateExit) tells every refusal apart, so a refused run still leaves a
// trace in the task's last result: 0 nothing to do, 10-17 a refusal or a failure, 100+N switched N networks.
internal static class AutoPrivateVerb
{
    public const string Verb = "--auto-private";

    private static string LogPath => Path.Combine(StateReader.DataFolder, AutoPrivateLog.FileName);

    public static int Run()
    {
        try
        {
            var (code, text) = Work(null);
            RunLog(text + " (exit " + code.ToString(CultureInfo.InvariantCulture) + ")");
            return code;
        }
        catch (Exception ex)
        {
            RunLog("failed: " + ex.GetType().Name + ": " + ex.Message + " (exit " + AutoPrivateExit.Crashed.ToString(CultureInfo.InvariantCulture) + ")");
            return AutoPrivateExit.Crashed;
        }
    }

    // configOverride: the CI test's in-process run with its own config (minus the skip list); the task passes null.
    internal static (int Code, string Text) Work(ConfigDocument? configOverride)
    {
        var self = Path.GetFullPath(typeof(AutoPrivateVerb).Assembly.Location);
        if (configOverride is null && !string.Equals(self, Path.GetFullPath(AutoPrivateCopy.Path), StringComparison.OrdinalIgnoreCase))
        {
            return (AutoPrivateExit.NotFromCopy, "refused: not run from " + AutoPrivateCopy.Path);
        }

        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                return (AutoPrivateExit.NotElevated, "refused: not elevated");
            }
        }

        if (!SetupSteps.Locked(AutoPrivateCopy.Folder))
        {
            return (AutoPrivateExit.CopyFolderNotLocked, "refused: " + AutoPrivateCopy.Folder + " is not locked");
        }

        // Before config.json is read, never after: a standard user can re-create a hand-deleted ProgramData\CPFS and put
        // a crafted config.json in it, and this check is what keeps that file from steering the verb.
        if (!SetupSteps.Locked())
        {
            return (AutoPrivateExit.DataFolderNotLocked, "refused: ProgramData\\CPFS is not locked");
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
                return (AutoPrivateExit.ConfigUnreadable, "nothing done: config.json not readable (" + ex.GetType().Name + ")");
            }
        }

        if (!AutoPrivateRules.Enabled(config))
        {
            return (AutoPrivateExit.Off, "nothing done: Auto-Private is off");
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
        var notTaken = 0;
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
                using (var file = PlainFiles.OpenWrite(LogPath, append: true))
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
            else
            {
                notTaken++;
            }
        }

        // A run that switched some networks says so even when another switch didn't take; the run log has each one.
        var code = switched > 0 ? AutoPrivateExit.Switched(switched) : notTaken > 0 ? AutoPrivateExit.NotTaken : AutoPrivateExit.NothingToDo;
        return (code, parts.Count == 0 ? "no networks" : string.Join("; ", parts));
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
            // SYSTEM never writes into a ProgramData\CPFS someone else may have re-created, nor through a link left in
            // it: either would turn the write into one anywhere on the disk.
            var path = Path.Combine(StateReader.DataFolder, AutoPrivateLog.RunLogFileName);
            if (!SetupSteps.Locked())
            {
                return;
            }

            var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + text;
            var utf8 = new UTF8Encoding(true);
            var bom = utf8.GetPreamble();
            var body = utf8.GetBytes(AutoPrivateLog.RunLog(existing, line));
            using var file = PlainFiles.OpenWrite(path, append: false);
            file.Write(bom, 0, bom.Length);
            file.Write(body, 0, body.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // The task's result is its exit code; a run log that can't be written changes nothing.
        }
    }
}
