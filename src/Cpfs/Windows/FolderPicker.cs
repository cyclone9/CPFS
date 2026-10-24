using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Cpfs.Windows;

// The Windows folder picker (the Common Item Dialog with FOS_PICKFOLDERS, Vista and later, Home included). .NET
// Framework's WPF has no folder dialog, and WinForms' FolderBrowserDialog is the old tree view. The declarations
// follow the WPF source (MS.Internal.AppModel ShellProvider and ComGuids); the vtable order must match the header.
internal static class FolderPicker
{
    private const uint FosNoChangeDir = 0x8;
    private const uint FosPickFolders = 0x20;
    private const uint FosForceFileSystem = 0x40;
    private const uint FosPathMustExist = 0x800;
    private const uint SigdnFileSysPath = 0x80058000;
    private const int ErrorCancelledHResult = unchecked((int)0x800704C7);

    private static readonly Guid ShellItemIid = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    // The picked folder's file system path, or null when the user cancelled. Must run on the UI (STA) thread.
    public static string? Pick(Window? owner, string title, string? start)
    {
        var dialog = (IFileDialog)new FileOpenDialogCoClass();
        try
        {
            dialog.SetOptions(dialog.GetOptions() | FosPickFolders | FosForceFileSystem | FosPathMustExist | FosNoChangeDir);
            dialog.SetTitle(title);
            if (StartItem(start) is { } folder)
            {
                dialog.SetFolder(folder);
                Marshal.ReleaseComObject(folder);
            }

            var hwnd = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
            var hr = dialog.Show(hwnd);
            if (hr == ErrorCancelledHResult)
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(hr);
            var result = dialog.GetResult();
            try
            {
                return result.GetDisplayName(SigdnFileSysPath);
            }
            finally
            {
                Marshal.ReleaseComObject(result);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    // The folder the dialog opens in: the typed path when it exists, else its nearest existing parent.
    private static IShellItem? StartItem(string? path)
    {
        var dir = (path ?? "").Trim();
        while (dir.Length > 0)
        {
            if (System.IO.Directory.Exists(dir))
            {
                var iid = ShellItemIid;
                return SHCreateItemFromParsingName(dir, IntPtr.Zero, ref iid, out var item) == 0 ? item : null;
            }

            dir = System.IO.Path.GetDirectoryName(dir) ?? "";
        }

        return null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItem item);

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    [ClassInterface(ClassInterfaceType.None)]
    private class FileOpenDialogCoClass
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    private interface IShellItem
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid);

        IShellItem GetParent();

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetDisplayName(uint sigdn);

        uint GetAttributes(uint mask);

        int Compare(IShellItem other, uint hint);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    private interface IFileDialog
    {
        // IModalWindow
        [PreserveSig]
        int Show(IntPtr parent);

        void SetFileTypes(uint count, IntPtr filterSpec);

        void SetFileTypeIndex(uint index);

        uint GetFileTypeIndex();

        uint Advise(IntPtr events);

        void Unadvise(uint cookie);

        void SetOptions(uint options);

        uint GetOptions();

        void SetDefaultFolder(IShellItem folder);

        void SetFolder(IShellItem folder);

        IShellItem GetFolder();

        IShellItem GetCurrentSelection();

        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetFileName();

        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);

        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);

        IShellItem GetResult();
    }
}
