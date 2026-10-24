namespace Cpfs.Core.Planning;

// Whether config.json may be believed. C:\ProgramData lets any user create a folder, so before the first Setup a
// standard user can make ProgramData\CPFS and plant a config.json whose Backup and Tracked would steer Uninstall.
// The file counts only when both it and its folder are owned by SYSTEM, Administrators, TrustedInstaller or a member
// of Administrators (an administrator who put the file back by hand), and the folder is not a link.
public static class ConfigTrust
{
    // What Setup renames an ignored config.json to, in the same folder; Uninstall copies these out before the folder goes.
    public static string IgnoredFileName(System.DateTime localTime) =>
        IgnoredPrefix + localTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".json";

    public static bool IsIgnoredFileName(string name) =>
        name is not null && name.StartsWith(IgnoredPrefix, System.StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase);

    private const string IgnoredPrefix = "config-ignored-";

    public static bool IsTrustedOwner(OwnerKind owner) =>
        owner is OwnerKind.System or OwnerKind.Administrators or OwnerKind.TrustedInstaller or OwnerKind.AdminMember;

    // True when a config.json exists and must be ignored. False when there is none, or the folder could not be read
    // (then the plan blocks on the unread folder anyway).
    public static bool Ignored(Reading<DataFolderInfo> folder)
    {
        if (folder is null || !folder.IsOk || folder.Value is not { Exists: true } d || d.ConfigOwner is not { } file)
        {
            return false;
        }

        var folderTrusted = d.OwnerIsAdministrators || IsTrustedOwner(d.Owner);
        return d.IsLink || !folderTrusted || !IsTrustedOwner(file);
    }

    // The config to plan from: the one read, or none when it must be ignored.
    public static Config.ConfigDocument? Usable(Config.ConfigDocument? config, Reading<DataFolderInfo> folder) =>
        config is not null && Ignored(folder) ? null : config;
}
