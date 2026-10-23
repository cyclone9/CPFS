using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Cpfs.Windows;

internal sealed class LocalUser
{
    public LocalUser(SecurityIdentifier sid, uint flags)
    {
        Sid = sid;
        Flags = flags;
    }

    public SecurityIdentifier Sid { get; }

    public uint Flags { get; }

    public bool Enabled => (Flags & LocalAccounts.UfAccountDisable) == 0;

    public bool PasswordNeverExpires => (Flags & LocalAccounts.UfDontExpirePasswd) != 0;

    public bool Locked => (Flags & LocalAccounts.UfLockout) != 0;
}

// The local SAM through netapi32, which answers from this machine alone. System.DirectoryServices.AccountManagement
// took about 21 s here on a workgroup laptop (owner's 0.3.0-dev1 log), most likely resolving group members through
// a domain that doesn't exist.
internal static class LocalAccounts
{
    internal const uint UfAccountDisable = 0x2;
    internal const uint UfLockout = 0x10;
    internal const uint UfDontExpirePasswd = 0x10000;

    private const int NerrSuccess = 0;
    private const int NerrUserNotFound = 2221;
    private const int NerrGroupNotFound = 2220;
    private const int ErrorNoSuchAlias = 1376;
    private const uint MaxPreferredLength = 0xFFFFFFFF;

    // Null when the account doesn't exist; any other failure (access denied included) throws Win32Exception.
    public static LocalUser? GetUser(string name)
    {
        var status = NetUserGetInfo(null, name, 23, out var buffer);
        try
        {
            if (status == NerrUserNotFound)
            {
                return null;
            }

            if (status != NerrSuccess)
            {
                throw new Win32Exception(status);
            }

            var info = Marshal.PtrToStructure<UserInfo23>(buffer);
            return new LocalUser(new SecurityIdentifier(info.Sid), info.Flags);
        }
        finally
        {
            Free(buffer);
        }
    }

    // Direct membership, compared by SID. The group's name is only needed as the API's key and is looked up from the
    // well-known SID, so a localized Windows (Persian "Administrators") works. Null when the group doesn't exist.
    public static bool? IsMember(SecurityIdentifier user, string groupSid)
    {
        var account = new SecurityIdentifier(groupSid).Translate(typeof(NTAccount)).Value;
        var groupName = account.Substring(account.LastIndexOf('\\') + 1);

        var resume = IntPtr.Zero;
        var status = NetLocalGroupGetMembers(null, groupName, 0, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
        try
        {
            if (status is NerrGroupNotFound or ErrorNoSuchAlias)
            {
                return null;
            }

            if (status != NerrSuccess)
            {
                throw new Win32Exception(status);
            }

            for (var i = 0; i < read; i++)
            {
                var sid = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                if (sid != IntPtr.Zero && new SecurityIdentifier(sid).Equals(user))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            Free(buffer);
        }
    }

    private static void Free(IntPtr buffer)
    {
        if (buffer != IntPtr.Zero)
        {
            _ = NetApiBufferFree(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo23
    {
        public IntPtr Name;
        public IntPtr FullName;
        public IntPtr Comment;
        public uint Flags;
        public IntPtr Sid;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? serverName, string userName, int level, out IntPtr buffer);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? serverName, string localGroupName, int level, out IntPtr buffer,
        uint preferredMaxLength, out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
