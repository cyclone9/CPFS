using System;
using System.Collections.Generic;
using Cpfs.Core.Drives;

namespace Cpfs.Core.Planning;

public enum ReadState
{
    Ok,
    NeedsAdmin,
    Failed,
}

// One value read from Windows, or why it could not be read. The planner never guesses an unread value.
public sealed class Reading<T>
{
    internal Reading(ReadState state, T? value, string? error)
    {
        State = state;
        Value = value;
        Error = error;
    }

    public ReadState State { get; }

    public T? Value { get; }

    public string? Error { get; }

    public bool IsOk => State == ReadState.Ok;
}

public static class Reading
{
    public static Reading<T> Ok<T>(T value) => new(ReadState.Ok, value, null);

    public static Reading<T> NeedsAdmin<T>(string? error = null) => new(ReadState.NeedsAdmin, default, error);

    public static Reading<T> Failed<T>(string? error) => new(ReadState.Failed, default, error);
}

public sealed record ShareInfo(
    string Name,
    string? Path,
    string? Description,
    bool? EncryptData,
    int? FolderEnumerationMode,
    int? CachingMode,
    bool Special);

public sealed record SmbServerInfo(bool? Smb1, bool? Smb2, bool? RejectUnencrypted);

// One entry of a share's permissions, as MSFT_SmbShareAccessControlEntry reports it. Sid is null when the account
// name could not be resolved. ControlType: 0 Allow, 1 Deny. Right: 0 Full, 1 Change, 2 Read, 3 Custom.
public sealed record ShareAccessEntry(string Account, string? Sid, int ControlType, int Right);

// Tcpip\Parameters: Active is "Hostname", Pending is "NV Hostname" when a rename waits for a restart.
public sealed record ComputerNameInfo(string? Active, string? Pending);

// Whether a rename is CPFS's to make: never on a domain member; only after asking on a device joined to Entra ID
// (NetGetAadJoinInformation's device join; a work account the user added doesn't count). EntraJoined is null when
// the Entra read failed: a rename is then asked, unticked, instead of blocking the plan.
public sealed record ComputerJoin(bool PartOfDomain, bool? EntraJoined);

public sealed record LockoutInfo(uint Threshold, uint DurationSeconds, uint WindowSeconds);

public sealed record AuditSetting(bool Success, bool Failure);

// A rule CPFS names itself ("CPFS ..." or the older "CyShare ..."), as the firewall API reports it.
public sealed record FirewallRuleInfo(
    string Name,
    bool Enabled,
    int Direction,
    int Action,
    int Protocol,
    string? LocalPorts,
    int Profiles,
    string? RemoteAddresses,
    IReadOnlyList<string> Interfaces);

// A built-in File and Printer Sharing rule. LocalPorts null means the port could not be read.
public sealed record BuiltInRuleInfo(string Id, string? DisplayName, bool Enabled, int Direction, int Profiles, IReadOnlyList<string>? LocalPorts);

// NetworkCategory: 0 Public, 1 Private, 2 Domain.
public sealed record NetworkInfo(string Name, string? InterfaceAlias, int Category);

public sealed record VpnAdapterInfo(string Name, string Address);

public sealed record DataFolderInfo(bool Exists, bool OwnerIsAdministrators, bool Protected, bool OthersCanWrite);

public sealed record UserInfo(string Sid, bool Enabled, bool PasswordNeverExpires, bool Locked, bool? IsAdmin, bool? InUsers, int? HiddenValue);

public sealed record FolderInfo(bool Exists, bool UserHasModify);

// Everything the planner compares with. Plain data, so a reading from a real machine can become a test fixture.
// The answer-dependent parts (User for the typed account name, Folder for the typed path, ShareAccess for the typed
// share name) are re-read by the app when those fields change.
public sealed record SystemSnapshot
{
    private static readonly IReadOnlyDictionary<string, Reading<int?>> NoHardening = new Dictionary<string, Reading<int?>>();

    public bool Elevated { get; init; }

    // Names the SMB account must not take: the signed-in and the desktop user (without domain).
    public IReadOnlyList<string> WindowsUserNames { get; init; } = Array.Empty<string>();

    public string? ComputerName { get; init; }

    public Reading<ComputerNameInfo> ComputerNames { get; init; } = Reading.Failed<ComputerNameInfo>(null);

    public Reading<ComputerJoin> Join { get; init; } = Reading.Failed<ComputerJoin>(null);

    public Reading<IReadOnlyList<ShareInfo>> Shares { get; init; } = Reading.Failed<IReadOnlyList<ShareInfo>>(null);

    public Reading<SmbServerInfo> SmbServer { get; init; } = Reading.Failed<SmbServerInfo>(null);

    // LanmanServer "Start": 2 automatic, 3 manual, 4 disabled.
    public Reading<int?> ServerStart { get; init; } = Reading.Failed<int?>(null);

    // By value name (Planner.Hardening); a null value means the value does not exist.
    public IReadOnlyDictionary<string, Reading<int?>> Hardening { get; init; } = NoHardening;

    public Reading<LockoutInfo> Lockout { get; init; } = Reading.Failed<LockoutInfo>(null);

    public Reading<AuditSetting> AuditLogon { get; init; } = Reading.Failed<AuditSetting>(null);

    // Win32_OptionalFeature.InstallState of SMB1Protocol: 1 enabled, 2 disabled, 3 absent; null when not listed.
    public Reading<int?> Smb1Feature { get; init; } = Reading.Failed<int?>(null);

    // Null when the machine has no account with RID 501.
    public Reading<bool?> GuestEnabled { get; init; } = Reading.Failed<bool?>(null);

    public Reading<IReadOnlyList<FirewallRuleInfo>> OwnRules { get; init; } = Reading.Failed<IReadOnlyList<FirewallRuleInfo>>(null);

    public Reading<IReadOnlyList<BuiltInRuleInfo>> BuiltInRules { get; init; } = Reading.Failed<IReadOnlyList<BuiltInRuleInfo>>(null);

    public Reading<IReadOnlyList<NetworkInfo>> Networks { get; init; } = Reading.Failed<IReadOnlyList<NetworkInfo>>(null);

    // Adapters that hold an address in 100.64.0.0/10 or fd7a:115c:a1e0::/48.
    public IReadOnlyList<VpnAdapterInfo> VpnAdapters { get; init; } = Array.Empty<VpnAdapterInfo>();

    // Task name to "exists".
    public Reading<IReadOnlyDictionary<string, bool>> Tasks { get; init; } = Reading.Failed<IReadOnlyDictionary<string, bool>>(null);

    // The CPFS-AutoPrivate task's definition; a null value means there is no such task.
    public Reading<ScheduledTaskInfo?> AutoPrivateTask { get; init; } = Reading.Failed<ScheduledTaskInfo?>(null);

    public Reading<AutoPrivateFiles> AutoPrivateFiles { get; init; } = Reading.Failed<AutoPrivateFiles>(null);

    // Where Setup puts the exe copy the task runs, and the folder that holds it.
    public string AutoPrivateExe { get; init; } = @"C:\Program Files\CPFS\Cpfs.exe";

    public string AutoPrivateFolderPath { get; init; } = @"C:\Program Files\CPFS";

    public Reading<AutoPrivateFolderInfo> AutoPrivateFolder { get; init; } = Reading.Failed<AutoPrivateFolderInfo>(null);

    // Where the dev3 test build put the copy, inside ProgramData\CPFS.
    public string AutoPrivateOldExe { get; init; } = @"C:\ProgramData\CPFS\Cpfs.exe";

    public Reading<DataFolderInfo> DataFolder { get; init; } = Reading.Failed<DataFolderInfo>(null);

    // DOS Devices values ("X" to "\??\D:\Shared") and Explorer drive labels, by letter.
    public Reading<IReadOnlyDictionary<char, string>> DosDevices { get; init; } = Reading.Failed<IReadOnlyDictionary<char, string>>(null);

    public Reading<IReadOnlyDictionary<char, string>> DriveLabels { get; init; } = Reading.Failed<IReadOnlyDictionary<char, string>>(null);

    // Explorer drive icons (DriveIcons\<L>\DefaultIcon), by letter.
    public Reading<IReadOnlyDictionary<char, string>> DriveIcons { get; init; } = Reading.Failed<IReadOnlyDictionary<char, string>>(null);

    // The CPFS icon copied into the data folder, used by the drive and the shortcut; a value naming it is CPFS's.
    public string CpfsIcon { get; init; } = @"C:\ProgramData\CPFS\cpfs.ico";

    // CPFS's File Explorer navigation-pane entry in the desktop user's hive.
    public Reading<ExplorerHive> ExplorerEntry { get; init; } = Reading.Failed<ExplorerHive>(null);

    public LetterScan Letters { get; init; } = new();

    public string CommonDesktop { get; init; } = @"C:\Users\Public\Desktop";

    // File names on the Public Desktop.
    public Reading<IReadOnlyList<string>> PublicDesktopFiles { get; init; } = Reading.Failed<IReadOnlyList<string>>(null);

    // The icon file each shortcut (.lnk) on the Public Desktop names, by file name; empty when it names none.
    public Reading<IReadOnlyDictionary<string, string>> ShortcutIcons { get; init; } = Reading.Failed<IReadOnlyDictionary<string, string>>(null);

    // The account named in the answers; a null value means it does not exist.
    public Reading<UserInfo?> User { get; init; } = Reading.Failed<UserInfo?>(null);

    // The folder named in the answers, with the account's access to it.
    public Reading<FolderInfo> Folder { get; init; } = Reading.Failed<FolderInfo>(null);

    // The permissions of the share named in the answers; a null value means the share does not exist.
    public Reading<IReadOnlyList<ShareAccessEntry>?> ShareAccess { get; init; } = Reading.Failed<IReadOnlyList<ShareAccessEntry>?>(null);
}
