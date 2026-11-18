using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Cpfs.Core.Connections;

namespace Cpfs.Windows;

// This PC's own SMB client side: the connections and saved drive mappings of the calling logon session, and cancelling
// one after the user confirmed. Called only from an unelevated process for the page: an elevated one would see the administrator session's
// connections, not the ones File Explorer uses.
internal static class SmbClient
{
    private const int NerrSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int ErrorNoMoreItems = 259;
    private const uint MaxPreferredLength = 0xFFFFFFFF;

    // winnetwk.h / wnnc.h values (mingw-w64 headers; Learn names the constants without their numbers).
    private const uint ResourceConnected = 0x00000001;
    private const uint ResourceRemembered = 0x00000003;
    private const uint ResourceTypeAny = 0x00000000;
    private const uint ResourceTypeDisk = 0x00000001;
    private const uint WnncNetLanman = 0x00020000;

    // lmuseflg.h: USE_NOFORCE 0, USE_LOTS_OF_FORCE 2 (Learn names them without numbers). USE_FORCE (1) is never used:
    // Learn doesn't say what happens to the open files.
    private const uint UseNoForce = 0;
    private const uint UseLotsOfForce = 2;

    // CONNECT_UPDATE_PROFILE 0x1 and CONNECT_TEMPORARY 0x4 (Learn, WNetAddConnection2W).
    private const uint ConnectUpdateProfile = 0x00000001;
    private const uint ConnectTemporary = 0x00000004;

    // NetUseEnum level 2: https://learn.microsoft.com/en-us/windows/win32/api/lmuse/nf-lmuse-netuseenum
    public static List<SmbUse> ReadUses()
    {
        var result = new List<SmbUse>();
        var resume = 0u;
        var size = Marshal.SizeOf<UseInfo2>();
        while (true)
        {
            var status = NetUseEnum(null, 2, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            try
            {
                if ((status != NerrSuccess && status != ErrorMoreData) || (status == ErrorMoreData && read == 0))
                {
                    throw new Win32Exception(status);
                }

                for (var i = 0; i < read; i++)
                {
                    var info = Marshal.PtrToStructure<UseInfo2>(IntPtr.Add(buffer, i * size));
                    var remote = Marshal.PtrToStringUni(info.Remote);
                    if (remote is null)
                    {
                        continue;
                    }

                    result.Add(new SmbUse(ClientConnections.LocalName(Marshal.PtrToStringUni(info.Local)), remote, info.Status, info.AssignmentType, info.RefCount,
                        Marshal.PtrToStringUni(info.UserName), Marshal.PtrToStringUni(info.DomainName)));
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    _ = NetApiBufferFree(buffer);
                }
            }

            if (status != ErrorMoreData)
            {
                return result;
            }
        }
    }

    // WNetOpenEnumW: https://learn.microsoft.com/en-us/windows/win32/api/winnetwk/nf-winnetwk-wnetopenenumw
    // WNetEnumResourceW: https://learn.microsoft.com/en-us/windows/win32/api/winnetwk/nf-winnetwk-wnetenumresourcew
    // WNetCloseEnum: https://learn.microsoft.com/en-us/windows/win32/api/winnetwk/nf-winnetwk-wnetcloseenum
    public static List<RememberedMapping> ReadRemembered() =>
        Enumerate(ResourceRemembered, ResourceTypeDisk).ConvertAll(r => new RememberedMapping(r.Local, r.Remote, r.Provider));

    // WNetOpenEnum(RESOURCE_CONNECTED, RESOURCETYPE_ANY), for the CI apply test's output only: every provider's
    // connections. Never shown, logged or cancelled by the app.
    public static List<ConnectedResource> ReadConnected() => Enumerate(ResourceConnected, ResourceTypeAny);

    private static List<ConnectedResource> Enumerate(uint scope, uint type)
    {
        var result = new List<ConnectedResource>();
        var status = WNetOpenEnumW(scope, type, 0, IntPtr.Zero, out var handle);
        if (status != 0)
        {
            throw new Win32Exception(status);
        }

        // Learn: the buffer size can't be asked for; 16 KB is typical, and a too-small one returns the size it needs.
        var capacity = 16 * 1024;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var stride = Marshal.SizeOf<NetResource>();
            while (true)
            {
                var count = -1;
                var bytes = capacity;
                status = WNetEnumResourceW(handle, ref count, buffer, ref bytes);
                if (status == ErrorNoMoreItems)
                {
                    return result;
                }

                // Too small for even one entry: bytes holds the size needed.
                if (status == ErrorMoreData && bytes > capacity)
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                    buffer = Marshal.AllocHGlobal(bytes);
                    capacity = bytes;
                    continue;
                }

                if (status != 0)
                {
                    throw new Win32Exception(status);
                }

                for (var i = 0; i < count; i++)
                {
                    var r = Marshal.PtrToStructure<NetResource>(IntPtr.Add(buffer, i * stride));
                    var remote = Marshal.PtrToStringUni(r.RemoteName);
                    if (remote is not null)
                    {
                        result.Add(new ConnectedResource(ClientConnections.LocalName(Marshal.PtrToStringUni(r.LocalName)), remote, Marshal.PtrToStringUni(r.Provider), r.Type));
                    }
                }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            _ = WNetCloseEnum(handle);
        }
    }

    // The SMB provider's name, localized by Windows. WNetGetProviderNameW:
    // https://learn.microsoft.com/en-us/windows/win32/api/winnetwk/nf-winnetwk-wnetgetprovidernamew
    public static string LanmanProviderName()
    {
        var size = 256;
        while (true)
        {
            var name = new char[size];
            var length = size;
            var status = WNetGetProviderNameW(WnncNetLanman, name, ref length);
            if (status == 0)
            {
                var end = Array.IndexOf(name, '\0');
                return new string(name, 0, end < 0 ? name.Length : end);
            }

            if (status != ErrorMoreData || length <= size)
            {
                throw new Win32Exception(status);
            }

            size = length;
        }
    }

    // WNetCancelConnection2W: https://learn.microsoft.com/en-us/windows/win32/api/winnetwk/nf-winnetwk-wnetcancelconnection2w
    // Returns Windows' code (0 is NO_ERROR); the caller judges success by reading the list again.
    public static int Cancel(DisconnectTarget target, bool force)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        return WNetCancelConnection2W(target.Name, target.UpdateProfile ? ConnectUpdateProfile : 0, force);
    }

    // NetUseDel for "End connections to this server": https://learn.microsoft.com/en-us/windows/win32/api/lmuse/nf-lmuse-netusedel
    // It reaches only the SMB client (no other provider), so the SSHFS drive is out of its reach, and the target is a
    // deviceless \\server\share built in Core (B9). Forced only for the one target the user confirmed in the danger
    // dialog (B10). Returns Windows' code.
    public static int Delete(ServerClearTarget target, bool force)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        if (!target.Remote.StartsWith(@"\\", StringComparison.Ordinal) || target.Remote.IndexOf(':') >= 0)
        {
            throw new ArgumentException("Not a deviceless remote name", nameof(target));
        }

        return NetUseDel(null, target.Remote, force ? UseLotsOfForce : UseNoForce);
    }

    // WNetAddConnection2W, for the CI test only (the page never creates a connection):
    // https://learn.microsoft.com/en-us/windows/win32/api/winnetwk/nf-winnetwk-wnetaddconnection2w
    // The password goes to Windows as an unmanaged copy that is zeroed and freed at once; it never becomes a string.
    public static int Add(string? localName, string remoteName, string user, System.Security.SecureString? password, bool remember)
    {
        var resource = new NetResource { Type = ResourceTypeDisk };
        var local = localName is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(localName);
        var remote = Marshal.StringToHGlobalUni(remoteName);
        var secret = password is null ? IntPtr.Zero : Marshal.SecureStringToGlobalAllocUnicode(password);
        try
        {
            resource.LocalName = local;
            resource.RemoteName = remote;
            return WNetAddConnection2W(ref resource, secret, user, remember ? ConnectUpdateProfile : ConnectTemporary);
        }
        finally
        {
            if (secret != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(secret);
            }

            Marshal.FreeHGlobal(remote);
            if (local != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(local);
            }
        }
    }

    // USE_INFO_2: https://learn.microsoft.com/en-us/windows/win32/api/lmuse/ns-lmuse-use_info_2
    // Password stays an IntPtr that is never read: Windows may or may not fill it, and CPFS never looks.
    [StructLayout(LayoutKind.Sequential)]
    private struct UseInfo2
    {
        public IntPtr Local;
        public IntPtr Remote;
        public IntPtr Password;
        public uint Status;
        public uint AssignmentType;
        public uint RefCount;
        public uint UseCount;
        public IntPtr UserName;
        public IntPtr DomainName;
    }

    // NETRESOURCEW: https://learn.microsoft.com/en-us/windows/win32/api/winnetwk/ns-winnetwk-netresourcew
    [StructLayout(LayoutKind.Sequential)]
    private struct NetResource
    {
        public uint Scope;
        public uint Type;
        public uint DisplayType;
        public uint Usage;
        public IntPtr LocalName;
        public IntPtr RemoteName;
        public IntPtr Comment;
        public IntPtr Provider;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUseEnum(string? uncServerName, int level, out IntPtr buffer, uint preferredMaximumSize,
        out int entriesRead, out int totalEntries, ref uint resumeHandle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUseDel(string? uncServerName, string useName, uint forceLevelFlags);

    // https://learn.microsoft.com/en-us/windows/win32/api/lmapibuf/nf-lmapibuf-netapibufferfree
    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetOpenEnumW(uint scope, uint type, uint usage, IntPtr netResource, out IntPtr handle);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetEnumResourceW(IntPtr handle, ref int count, IntPtr buffer, ref int bufferSize);

    [DllImport("mpr.dll")]
    private static extern int WNetCloseEnum(IntPtr handle);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2W(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2W(ref NetResource netResource, IntPtr password, string userName, uint flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetProviderNameW(uint netType, [Out] char[] providerName, ref int bufferSize);
}

// One row of WNetOpenEnum, of any provider. Type is the NETRESOURCE dwType.
internal sealed record ConnectedResource(string? Local, string Remote, string? Provider, uint Type);
