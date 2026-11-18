using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Cpfs.Core.Connections;
using Microsoft.Win32;

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
    private const uint ResourceRemembered = 0x00000003;
    private const uint ResourceTypeDisk = 0x00000001;
    private const uint WnncNetLanman = 0x00020000;

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

                    result.Add(new SmbUse(Marshal.PtrToStringUni(info.Local), remote, info.Status, info.AssignmentType, info.RefCount,
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
    public static List<RememberedMapping> ReadRemembered()
    {
        var result = new List<RememberedMapping>();
        var status = WNetOpenEnumW(ResourceRemembered, ResourceTypeDisk, 0, IntPtr.Zero, out var handle);
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
                        result.Add(new RememberedMapping(Marshal.PtrToStringUni(r.LocalName), remote, Marshal.PtrToStringUni(r.Provider)));
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

    // ---- dev1 probes: written to the log only, each failure a logged line. ----

    // NetSessionEnum level 10 (the server side): whether an unelevated process may read it. Code and count only.
    // https://learn.microsoft.com/en-us/windows/win32/api/lmshare/nf-lmshare-netsessionenum
    // https://learn.microsoft.com/en-us/windows/win32/api/lmshare/ns-lmshare-session_info_10
    public static string ProbeSessionEnum()
    {
        var resume = 0u;
        var status = NetSessionEnum(null, null, null, 10, out var buffer, MaxPreferredLength, out var read, out var total, ref resume);
        if (buffer != IntPtr.Zero)
        {
            _ = NetApiBufferFree(buffer);
        }

        return "status " + status.ToString(CultureInfo.InvariantCulture) + ", read " + read.ToString(CultureInfo.InvariantCulture)
            + ", total " + total.ToString(CultureInfo.InvariantCulture);
    }

    // MSFT_SmbConnection: https://learn.microsoft.com/en-us/previous-versions/windows/desktop/smb/msft-smbconnection
    public static string ProbeSmbConnectionCim()
    {
        var rows = new List<string>();
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\Microsoft\Windows\SMB"),
            new ObjectQuery("SELECT UserName, Dialect FROM MSFT_SmbConnection"));
        using var results = searcher.Get();
        foreach (ManagementBaseObject o in results)
        {
            using (o)
            {
                rows.Add(Convert.ToString(o["UserName"], CultureInfo.InvariantCulture) + " / " + Convert.ToString(o["Dialect"], CultureInfo.InvariantCulture));
            }
        }

        return rows.Count.ToString(CultureInfo.InvariantCulture) + " rows" + (rows.Count == 0 ? "" : ": " + string.Join("; ", rows));
    }

    // Undocumented key (lookup C): value names and the first bytes as found, for the dev4 design.
    public static string ProbeStartupApproved()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", false);
        if (key is null)
        {
            return "key absent";
        }

        var parts = new List<string>();
        foreach (var name in key.GetValueNames())
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var text = value is byte[] bytes
                ? BitConverter.ToString(bytes, 0, Math.Min(4, bytes.Length)) + " (" + bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes)"
                : key.GetValueKind(name).ToString();
            parts.Add((name.Length == 0 ? "(default)" : name) + " = " + text);
        }

        return parts.Count.ToString(CultureInfo.InvariantCulture) + " values" + (parts.Count == 0 ? "" : ": " + string.Join("; ", parts));
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
    private static extern int NetSessionEnum(string? serverName, string? uncClientName, string? userName, int level, out IntPtr buffer,
        uint preferredMaximumLength, out int entriesRead, out int totalEntries, ref uint resumeHandle);

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
