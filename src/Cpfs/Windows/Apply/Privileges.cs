using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Cpfs.Windows.Apply;

// Enables privileges an elevated administrator holds but has switched off (SeSecurityPrivilege for the audit policy,
// SeTakeOwnershipPrivilege and SeRestorePrivilege for setting an owner) for one Apply, and puts them back after.
internal sealed class Privileges : IDisposable
{
    private readonly IntPtr _token;
    private readonly List<WinApi.TokenPrivileges> _previous = new();

    private Privileges(IntPtr token)
    {
        _token = token;
    }

    public static Privileges Enable(params string[] names)
    {
        IntPtr token;
        using (var self = Process.GetCurrentProcess())
        {
            if (!WinApi.OpenProcessToken(self.Handle, WinApi.TokenAdjustPrivileges | WinApi.TokenQuery, out token))
            {
                AppLog.Write("apply", "OpenProcessToken: " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
                token = IntPtr.Zero;
            }
        }

        var result = new Privileges(token);
        if (token == IntPtr.Zero)
        {
            return result;
        }

        foreach (var name in names)
        {
            if (!WinApi.LookupPrivilegeValue(null, name, out var luid))
            {
                continue;
            }

            var wanted = new WinApi.TokenPrivileges { Count = 1, Luid = luid, Attributes = WinApi.SePrivilegeEnabled };
            var ok = WinApi.AdjustTokenPrivileges(token, false, ref wanted, Marshal.SizeOf<WinApi.TokenPrivileges>(), out var previous, out _);
            var error = Marshal.GetLastWin32Error();
            if (!ok || error == WinApi.ErrorNotAllAssigned)
            {
                AppLog.Write("apply", name + " not enabled: " + error.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            if (previous.Count > 0)
            {
                result._previous.Add(previous);
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (_token == IntPtr.Zero)
        {
            return;
        }

        foreach (var p in _previous)
        {
            var restore = p;
            _ = WinApi.AdjustTokenPrivileges(_token, false, ref restore, Marshal.SizeOf<WinApi.TokenPrivileges>(), out _, out _);
        }

        _ = Native.CloseHandle(_token);
    }
}
