using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cpfs.Windows;

internal static class Native
{
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint TokenDuplicate = 0x0002;
    internal const uint TokenImpersonate = 0x0004;
    internal const uint TokenQuery = 0x0008;

    internal const int TokenUserClass = 1;
    internal const int TokenElevationTypeClass = 18;

    internal const int ErrorCancelled = 1223;

    internal static readonly Guid FolderIdDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeKernelHandle OpenProcess(uint access, bool inherit, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(SafeKernelHandle process, uint access, out SafeKernelHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(SafeKernelHandle token, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("shell32.dll")]
    internal static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, SafeKernelHandle token, out IntPtr path);

    // Looks in the caller's own logon-session device map first, then the global one.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint QueryDosDevice(string deviceName, char[] targetPath, uint maxChars);

    [DllImport("kernel32.dll")]
    internal static extern bool CloseHandle(IntPtr handle);

    internal const ushort AfUnspec = 0;

    // Returns a Win32 error code (NO_ERROR, ERROR_NOT_FOUND when the table is empty); the table is freed with FreeMibTable.
    [DllImport("iphlpapi.dll")]
    internal static extern uint GetIpNetTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    internal static extern void FreeMibTable(IntPtr memory);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
