using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Cpfs.Windows;

// The user who owns the desktop of this session, found through the explorer.exe that runs in it. An elevated process
// can belong to another account (over-the-shoulder elevation) or to a separate logon of the same account, so its own
// identity says nothing about whose letters and Desktop matter. The elevated instance trusts only this, never the
// --desktop-sid / --desktop values it was handed.
internal sealed class DesktopUser
{
    private static readonly Lazy<DesktopUser> Cached = new(Find);

    private DesktopUser()
    {
    }

    public static DesktopUser Current => Cached.Value;

    public string? Sid { get; private set; }

    public string? ElevationType { get; private set; }

    public string? Desktop { get; private set; }

    // Where Uninstall leaves a copy of the backup when the settings are not restored.
    public string? Documents { get; private set; }

    public int ExplorerPid { get; private set; }

    public string? Error { get; private set; }

    public string Describe() => "explorer pid " + ExplorerPid.ToString(CultureInfo.InvariantCulture)
        + ", SID " + (Sid ?? "unknown") + ", token " + (ElevationType ?? "unknown")
        + ", Desktop " + (Desktop ?? "unknown") + (Error is null ? "" : ", error " + Error);

    private static DesktopUser Find()
    {
        var result = new DesktopUser();
        int session;
        using (var self = Process.GetCurrentProcess())
        {
            session = self.SessionId;
        }

        var explorers = Process.GetProcessesByName("explorer");
        try
        {
            string? lastError = null;
            foreach (var p in explorers)
            {
                try
                {
                    if (p.SessionId != session)
                    {
                        continue;
                    }

                    result.Read(p.Id);
                    result.ExplorerPid = p.Id;
                    return result;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
                {
                    lastError = ex.GetType().Name + ": " + ex.Message;
                }
            }

            result.Error = lastError ?? "no explorer.exe in session " + session.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            foreach (var p in explorers)
            {
                p.Dispose();
            }
        }

        return result;
    }

    private void Read(int pid)
    {
        using var process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess(explorer " + pid.ToString(CultureInfo.InvariantCulture) + ")");
        }

        // SHGetKnownFolderPath needs TOKEN_QUERY | TOKEN_IMPERSONATE, sometimes TOKEN_DUPLICATE (Microsoft Learn).
        if (!Native.OpenProcessToken(process, Native.TokenQuery | Native.TokenImpersonate | Native.TokenDuplicate, out var token)
            && !Native.OpenProcessToken(process, Native.TokenQuery, out token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken");
        }

        using (token)
        {
            Sid = ReadTokenUser(token);
            ElevationType = ReadElevationType(token);
            Desktop = ReadFolder(token, Native.FolderIdDesktop);
            Documents = ReadFolder(token, Native.FolderIdDocuments);
        }
    }

    private static string ReadTokenUser(Native.SafeKernelHandle token)
    {
        var buffer = QueryToken(token, Native.TokenUserClass);
        try
        {
            // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the SID pointer.
            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ReadElevationType(Native.SafeKernelHandle token)
    {
        var buffer = QueryToken(token, Native.TokenElevationTypeClass);
        try
        {
            return Marshal.ReadInt32(buffer) switch
            {
                1 => "Default",
                2 => "Full",
                3 => "Limited",
                var other => other.ToString(CultureInfo.InvariantCulture),
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ReadFolder(Native.SafeKernelHandle token, Guid folder)
    {
        if (Native.SHGetKnownFolderPath(folder, 0, token, out var path) != 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    private static IntPtr QueryToken(Native.SafeKernelHandle token, int infoClass)
    {
        Native.GetTokenInformation(token, infoClass, IntPtr.Zero, 0, out var length);
        if (length <= 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation size");
        }

        var buffer = Marshal.AllocHGlobal(length);
        if (!Native.GetTokenInformation(token, infoClass, buffer, length, out _))
        {
            var error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(buffer);
            throw new Win32Exception(error, "GetTokenInformation");
        }

        return buffer;
    }
}
