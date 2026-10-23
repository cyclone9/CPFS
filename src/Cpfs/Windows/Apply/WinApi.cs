using System;
using System.Runtime.InteropServices;

namespace Cpfs.Windows.Apply;

// The Win32 calls that write: netapi32 (accounts, groups, lockout), the audit policy, the service manager and the
// process token's privileges.
internal static class WinApi
{
    internal const int NerrSuccess = 0;
    internal const int NerrUserNotFound = 2221;
    internal const int ErrorMemberNotInAlias = 1377;
    internal const int ErrorMemberInAlias = 1378;
    internal const int UserPrivUser = 1;

    internal const uint UfScript = 0x0001;
    internal const uint UfAccountDisable = 0x0002;
    internal const uint UfLockout = 0x0010;
    internal const uint UfPasswdCantChange = 0x0040;
    internal const uint UfNormalAccount = 0x0200;
    internal const uint UfDontExpirePasswd = 0x10000;

    internal const uint ScManagerConnect = 0x0001;
    internal const uint ServiceChangeConfig = 0x0002;
    internal const uint ServiceQueryConfig = 0x0001;
    internal const uint ServiceNoChange = 0xFFFFFFFF;

    internal const uint TokenAdjustPrivileges = 0x0020;
    internal const uint TokenQuery = 0x0008;
    internal const uint SePrivilegeEnabled = 0x00000002;
    internal const int ErrorNotAllAssigned = 1300;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct UserInfo1
    {
        public string Name;
        public IntPtr Password;
        public uint PasswordAge;
        public uint Priv;
        public string? HomeDir;
        public string? Comment;
        public uint Flags;
        public string? ScriptPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UserInfo1003
    {
        public IntPtr Password;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UserInfo1008
    {
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LocalGroupMembersInfo0
    {
        public IntPtr Sid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UserModalsInfo3
    {
        public uint Duration;
        public uint ObservationWindow;
        public uint Threshold;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AuditPolicyInformation
    {
        public Guid SubCategory;
        public uint AuditingInformation;
        public Guid Category;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetUserAdd(string? serverName, int level, ref UserInfo1 buffer, out int parmError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetUserSetInfo(string? serverName, string userName, int level, ref UserInfo1003 buffer, out int parmError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetUserSetInfo(string? serverName, string userName, int level, ref UserInfo1008 buffer, out int parmError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetUserDel(string? serverName, string userName);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetLocalGroupAddMembers(string? serverName, string groupName, int level, ref LocalGroupMembersInfo0 buffer, int totalEntries);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetLocalGroupDelMembers(string? serverName, string groupName, int level, ref LocalGroupMembersInfo0 buffer, int totalEntries);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetUserModalsSet(string? serverName, int level, ref UserModalsInfo3 buffer, out int parmError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetUserEnum(string? serverName, int level, int filter, out IntPtr buffer, uint preferredMaxLength,
        out int entriesRead, out int totalEntries, ref uint resumeHandle);

    [DllImport("netapi32.dll")]
    internal static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool AuditSetSystemPolicy(ref AuditPolicyInformation policy, uint count);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType, uint errorControl, string? binaryPath,
        string? loadOrderGroup, IntPtr tagId, string? dependencies, string? serviceStartName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, int bufferLength,
        out TokenPrivileges previousState, out int returnLength);
}
