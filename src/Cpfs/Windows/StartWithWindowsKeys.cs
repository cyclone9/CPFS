using System.Security.Principal;
using Cpfs.Core.Preferences;
using Cpfs.Core.Text;
using Microsoft.Win32;

namespace Cpfs.Windows;

// The Run value named CPFS and its StartupApproved entry. The window writes only its own HKCU, and only unelevated
// (B2); Uninstall removes a CPFS-format value from the desktop user's hive (HKEY_USERS\<SID>), as ExplorerEntryKeys
// reaches it. StartupApproved is never written.
internal static class StartWithWindowsKeys
{
    public static RunValue? ReadCurrentUser()
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        return ReadFrom(hkcu);
    }

    public static RunValue? Read(string sid)
    {
        using var hive = OpenHive(sid, writable: false);
        return ReadFrom(hive);
    }

    public static void WriteCurrentUser(string command)
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var run = hkcu.CreateSubKey(StartWithWindows.RunKey, writable: true)
            ?? throw new CpfsException("Err_RegistryCreate", StartWithWindows.RunKey);
        run.SetValue(StartWithWindows.ValueName, command, RegistryValueKind.String);
    }

    public static void DeleteCurrentUser()
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var run = hkcu.OpenSubKey(StartWithWindows.RunKey, writable: true);
        run?.DeleteValue(StartWithWindows.ValueName, throwOnMissingValue: false);
    }

    public static void Delete(string sid)
    {
        using var hive = OpenHive(sid, writable: false);
        using var run = hive.OpenSubKey(StartWithWindows.RunKey, writable: true);
        run?.DeleteValue(StartWithWindows.ValueName, throwOnMissingValue: false);
    }

    // The StartupApproved\Run bytes for CPFS: null when absent or not REG_BINARY.
    public static byte[]? ApprovedCurrentUser()
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hkcu.OpenSubKey(StartWithWindows.ApprovedKey);
        if (key is null || key.GetValue(StartWithWindows.ValueName) is null)
        {
            return null;
        }

        return key.GetValueKind(StartWithWindows.ValueName) == RegistryValueKind.Binary ? key.GetValue(StartWithWindows.ValueName) as byte[] : null;
    }

    private static RunValue? ReadFrom(RegistryKey root)
    {
        using var run = root.OpenSubKey(StartWithWindows.RunKey);
        if (run is null || run.GetValue(StartWithWindows.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } data)
        {
            return null;
        }

        return new RunValue(run.GetValueKind(StartWithWindows.ValueName) == RegistryValueKind.String ? data as string : null);
    }

    // A SID is only ever a SID, never a path into another key.
    private static RegistryKey OpenHive(string sid, bool writable)
    {
        var checkedSid = new SecurityIdentifier(sid).Value;
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        return users.OpenSubKey(checkedSid, writable) ?? throw new CpfsException("Err_RunHiveNotLoaded");
    }
}
