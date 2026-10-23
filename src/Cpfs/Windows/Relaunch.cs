using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Cpfs.Core.Elevation;

namespace Cpfs.Windows;

internal enum RelaunchResult
{
    Started,
    Cancelled,
    Failed,
}

// "Continue as administrator": starts this same exe with the "runas" verb and hands over only non-secret,
// format-checked values. Nothing flows back: the elevated instance takes no commands.
internal static class Relaunch
{
    public static RelaunchResult Start(string page, string sessionLetters, out string? error)
    {
        error = null;
        string? sid;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            sid = identity.User?.Value;
        }

        var request = new RelaunchRequest
        {
            Page = page,
            DesktopSid = sid,
            DesktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Letters = sessionLetters,
            Theme = Theming.Choice.ToString(),
            Language = Loc.Instance.Culture.TwoLetterISOLanguageName,
            Font = Theming.CurrentFont.ToString(),
        };

        var exe = typeof(Relaunch).Assembly.Location;
        var arguments = RelaunchArgs.ToCommandLine(RelaunchArgs.Build(request));
        AppLog.Write("elevation", "Starting elevated: " + exe + " " + arguments);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            });
            AppLog.Write("elevation", "Elevated instance started");
            return RelaunchResult.Started;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Native.ErrorCancelled)
        {
            AppLog.Write("elevation", "UAC prompt declined (ERROR_CANCELLED)");
            return RelaunchResult.Cancelled;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Write("elevation", "Could not start the elevated instance: " + ex.GetType().Name + ": " + ex.Message);
            error = ex.Message;
            return RelaunchResult.Failed;
        }
    }
}
