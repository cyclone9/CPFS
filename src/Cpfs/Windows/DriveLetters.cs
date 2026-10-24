using System;
using System.IO;
using System.Security;
using Cpfs.Core.Drives;
using Microsoft.Win32;

namespace Cpfs.Windows;

// Reads which drive letters are taken, and where each was seen. Reads letters only: never touches the drives
// (IsReady, VolumeLabel), because a network drive whose server is gone, such as an SSHFS-Win mount, can block.
internal static class DriveLetters
{
    // Unelevated: GetLogicalDrives and QueryDosDevice see the user's own session letters (subst, net use, SSHFS-Win).
    // Elevated: the desktop user's session is a different device map, so its letters come from the hand-over.
    public static LetterScan Scan(string? networkUserSid)
    {
        var scan = new LetterScan();

        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
        if (!string.IsNullOrEmpty(systemDrive))
        {
            scan.Add(systemDrive![0], LetterSource.SystemDrive, systemDrive);
        }

        Step(scan, LetterSource.ThisProcess, () =>
        {
            foreach (var d in Environment.GetLogicalDrives())
            {
                if (d.Length > 0)
                {
                    scan.Add(d[0], LetterSource.ThisProcess, DosDeviceTarget(d.Substring(0, 2)));
                }
            }
        });

        Step(scan, LetterSource.MountedDevices, () =>
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SYSTEM\MountedDevices");
            foreach (var name in key?.GetValueNames() ?? Array.Empty<string>())
            {
                const string prefix = @"\DosDevices\";
                if (name.Length == prefix.Length + 2 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name[name.Length - 1] == ':')
                {
                    scan.Add(name[prefix.Length], LetterSource.MountedDevices);
                }
            }
        });

        Step(scan, LetterSource.DosDevices, () =>
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\DOS Devices");
            foreach (var name in key?.GetValueNames() ?? Array.Empty<string>())
            {
                if (name.Length == 2 && name[1] == ':')
                {
                    scan.Add(name[0], LetterSource.DosDevices, key!.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
                }
            }
        });

        Step(scan, LetterSource.UserNetwork, () =>
        {
            // Unelevated, HKCU is the user's own hive; elevated, the desktop user's hive under HKEY_USERS.
            using var root = networkUserSid is null
                ? RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64)
                : RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            using var key = root.OpenSubKey(networkUserSid is null ? "Network" : networkUserSid + @"\Network");
            foreach (var name in key?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (name.Length == 1)
                {
                    using var sub = key!.OpenSubKey(name);
                    scan.Add(name[0], LetterSource.UserNetwork, sub?.GetValue("RemotePath") as string);
                }
            }
        });

        AppLog.Write("drives", Describe(scan));
        return scan;
    }

    // Letters in use in this process's session, for the hand-over: logical drives plus persistent network drives.
    public static string SessionLetters(LetterScan scan)
    {
        var chars = new System.Text.StringBuilder();
        foreach (var pair in scan.Taken)
        {
            if (pair.Value.Exists(u => u.Source is LetterSource.ThisProcess or LetterSource.UserNetwork))
            {
                chars.Append(pair.Key);
            }
        }

        return chars.ToString();
    }

    public static string Describe(LetterScan scan)
    {
        var parts = new System.Collections.Generic.List<string>();
        foreach (var pair in scan.Taken)
        {
            foreach (var use in pair.Value)
            {
                parts.Add(pair.Key + "=" + use.Source + (use.Detail is null ? "" : " (" + use.Detail + ")"));
            }
        }

        foreach (var failed in scan.Failed)
        {
            parts.Add("FAILED " + failed.Key + ": " + failed.Value);
        }

        return "Taken: " + string.Join("; ", parts) + ". Free: " + new string(scan.Free().ToArray());
    }

    private static string? DosDeviceTarget(string device)
    {
        var buffer = new char[1024];
        var n = Native.QueryDosDevice(device, buffer, (uint)buffer.Length);
        if (n == 0)
        {
            return null;
        }

        // A multi-string; the first entry is the current mapping.
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? (int)n : end);
    }

    private static void Step(LetterScan scan, LetterSource source, Action read)
    {
        try
        {
            read();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            scan.Failed.Add(new System.Collections.Generic.KeyValuePair<LetterSource, string>(source, ex.GetType().Name + ": " + ex.Message));
        }
    }
}
