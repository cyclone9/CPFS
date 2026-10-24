using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cpfs.Windows;

// Files and folders looked at and written without following a link. An elevated or SYSTEM write through a junction,
// symbolic link or hard link that a standard user planted would land on whatever it points at.
internal static class PlainFiles
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint ShareRead = 0x1;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint AttributeReparsePoint = 0x400;

    // The entry's own attributes: GetFileAttributes reports a link itself, not its target. False when it doesn't exist.
    public static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    // Not a reparse point, and for a file only one name (a hard link shares its owner and permissions with every other
    // name of the file). Throws when the entry can't be opened.
    public static bool IsPlain(string path, bool directory)
    {
        using var handle = CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting,
            FlagOpenReparsePoint | (directory ? FlagBackupSemantics : 0), IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return Plain(handle, directory);
    }

    // A file opened for writing (created when absent), refused when it is a link or has more than one name.
    public static FileStream OpenWrite(string path, bool append)
    {
        var handle = CreateFileW(path, GenericWrite, ShareRead, IntPtr.Zero, OpenAlways, FlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(path + ": " + new Win32Exception(error).Message);
        }

        try
        {
            if (!Plain(handle, directory: false))
            {
                throw new IOException(path + " is a link or has more than one name; CPFS doesn't write to it.");
            }

            var stream = new FileStream(handle, FileAccess.Write);
            if (append)
            {
                stream.Seek(0, SeekOrigin.End);
            }
            else
            {
                stream.SetLength(0);
            }

            return stream;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    // The whole file, read without following a link; refused when it is a link or has more than one name.
    public static byte[] ReadAll(string path)
    {
        using var handle = CreateFileW(path, GenericRead, ShareRead, IntPtr.Zero, OpenExisting, FlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException(path + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        if (!Plain(handle, directory: false))
        {
            throw new IOException(path + " is a link or has more than one name; CPFS doesn't read it.");
        }

        using var stream = new FileStream(handle, FileAccess.Read);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static bool Plain(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return (info.FileAttributes & AttributeReparsePoint) == 0 && (directory || info.NumberOfLinks == 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
