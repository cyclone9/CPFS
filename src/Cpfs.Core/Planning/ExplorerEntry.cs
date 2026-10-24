using System;

namespace Cpfs.Core.Planning;

// CPFS's entry at the root of File Explorer's navigation pane, registered per user as Microsoft documents for cloud
// storage providers [Learn: "Integrate a Cloud Storage Provider", steps 1-12, Windows 10 and later]. The keys live in
// the desktop user's hive; every value below is the documented one.
public static class ExplorerEntry
{
    // CPFS's own class identifier, fixed for every install: a key under it is CPFS's.
    public const string Clsid = "{07C33AB4-B371-467B-9DC1-1AA530E251C6}";

    // The shell's folder instance object (step 6).
    public const string InstanceClsid = "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}";

    public const string InProcServer = @"%systemroot%\system32\shell32.dll";

    public const int Pinned = 1;
    public const int SortOrderIndex = 0x42;

    // FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_READONLY (step 7).
    public const int BagAttributes = 0x11;

    public const int FolderValueFlags = 0x28;

    // The SFGAO flags of step 10, as the signed DWORD the registry returns.
    public const int ShellAttributes = unchecked((int)0xF080004D);

    public const int HiddenOnDesktop = 1;

    // Below HKEY_USERS\<SID> (or HKCU).
    public const string ClassKey = @"Software\Classes\CLSID\" + Clsid;
    public const string NameSpaceKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\" + Clsid;
    public const string HideDesktopIconsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";

    // TargetFolderPath is REG_EXPAND_SZ (step 8), and environment-variable expansion has no escape for '%'.
    public static bool CanHold(string folder) => folder.IndexOf('%') < 0;

    // Every value is there with its documented data, apart from the name, the icon and the folder.
    public static bool IsComplete(ExplorerEntryInfo e) =>
        e.Pinned == Pinned && e.SortOrder == SortOrderIndex
        && string.Equals(e.InProcServer, InProcServer, StringComparison.OrdinalIgnoreCase)
        && string.Equals(e.InstanceClsid, InstanceClsid, StringComparison.OrdinalIgnoreCase)
        && e.BagAttributes == BagAttributes && e.FolderValueFlags == FolderValueFlags && e.ShellAttributes == ShellAttributes
        && e.HiddenOnDesktop == HiddenOnDesktop;

    public static bool IsAtTarget(ExplorerEntryInfo e, string name, string folder, string icon) =>
        IsComplete(e)
        && string.Equals(e.Name, name, StringComparison.Ordinal)
        && string.Equals(e.NameSpaceName, name, StringComparison.Ordinal)
        && SameFolder(e.TargetFolderPath, folder)
        && Planner.IsOwnIcon(e.Icon, icon);

    public static bool SameFolder(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}

// The values found under CPFS's class key and its two Explorer entries, unexpanded; null where a value is missing.
public sealed record ExplorerEntryInfo
{
    public string? Name { get; init; }

    public string? NameSpaceName { get; init; }

    public string? Icon { get; init; }

    public string? TargetFolderPath { get; init; }

    public int? Pinned { get; init; }

    public int? SortOrder { get; init; }

    public string? InProcServer { get; init; }

    public string? InstanceClsid { get; init; }

    public int? BagAttributes { get; init; }

    public int? FolderValueFlags { get; init; }

    public int? ShellAttributes { get; init; }

    public int? HiddenOnDesktop { get; init; }
}

// The hive the entry was read from: the desktop user's (HKEY_USERS\<Sid>), or the user's own HKCU when not elevated.
// Loaded is false when that user's hive is not in HKEY_USERS (the user is not signed in). Entry is null when CPFS's
// class key does not exist there.
public sealed record ExplorerHive(string? Sid, bool Loaded, ExplorerEntryInfo? Entry);
