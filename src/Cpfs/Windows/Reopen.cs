using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Cpfs.Core.Apply;
using Cpfs.Core.Elevation;
using Cpfs.Windows.Apply;

namespace Cpfs.Windows;

// "Reopen without administrator rights" on the Connections note of an elevated window: this same exe, started as the
// desktop user (limited token) through the temporary CPFS-RunAsUser task, with --reopened --page Connections and
// nothing else. The direction is de-elevation, so nothing it passes can gain rights. The new instance waits for this
// window to close and release the single-instance mutex (SingleInstance.Acquire).
internal static class Reopen
{
    private const uint AsfwAny = 0xFFFFFFFF;

    // Throws StepUnsupportedException (with a text for the user) or a COM/IO exception when it could not start it.
    public static void AsDesktopUser()
    {
        // The elevated instance's own finding (explorer's token), never a value from the command line.
        var sid = DesktopUser.Current.Sid ?? throw new StepUnsupportedException("Connections_Reopen_NoUser");
        var account = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        var exe = typeof(Reopen).Assembly.Location;
        var arguments = RelaunchArgs.ToCommandLine(RelaunchArgs.BuildReopen());
        AppLog.Write("connections", "Reopening without administrator rights as " + account + ": " + exe + " " + arguments);

        // This window has the foreground (the user just clicked); the new one may take it until the user's next input.
        _ = AllowSetForegroundWindow(AsfwAny);
        RunAsUser.Start(account, exe, arguments);
        AppLog.Write("connections", "The unelevated instance started; this window closes");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
