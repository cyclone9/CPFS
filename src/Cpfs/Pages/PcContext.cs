using System;
using System.Collections.Generic;
using System.Diagnostics;
using Cpfs.Core.Drives;
using Cpfs.Core.Elevation;
using Cpfs.Windows;

namespace Cpfs.Pages;

// The desktop user and the drive letters in use, which Setup plans with and Advanced shows. Elevated, the desktop
// user comes from explorer's token; what the unelevated instance handed over is only compared with it.
internal static class PcContext
{
    private static readonly object Gate = new();
    private static (List<StateRow> Rows, string Letters)? _handOver;

    // Rows for the hand-over card; null when not elevated.
    public static List<StateRow>? HandOverRows
    {
        get
        {
            lock (Gate)
            {
                return _handOver?.Rows;
            }
        }
    }

    public static bool IdentityMismatch =>
        Startup.IsHandOver && Identity.Compare(Startup.Arguments, DesktopUser.Current.Sid, DesktopUser.Current.Desktop) == IdentityCheck.Mismatch;

    // Runs off the UI thread. The hand-over is checked once per run; the letters are scanned every time.
    public static LetterScan ReadLetters()
    {
        var watch = Stopwatch.StartNew();
        string? networkSid = null;
        var received = "";
        if (Startup.IsElevated)
        {
            var user = DesktopUser.Current;
            networkSid = user.Sid;
            lock (Gate)
            {
                if (_handOver is null)
                {
                    AppLog.Write("elevation", "Desktop user: " + user.Describe());
                    var rows = BuildHandOver(user, out var accepted);
                    _handOver = (rows, accepted);
                }

                received = _handOver.Value.Letters;
            }
        }

        var letters = DriveLetters.Scan(networkSid);
        if (received.Length > 0)
        {
            letters.AddAll(received, LetterSource.UserSessionReceived);
        }

        AppLog.Write("state", "Desktop user and drive letters in " + StateReader.Ms(watch));
        return letters;
    }

    private static List<StateRow> BuildHandOver(DesktopUser user, out string acceptedLetters)
    {
        var rows = new List<StateRow>();
        var args = Startup.Arguments;
        acceptedLetters = "";
        if (!Startup.IsHandOver)
        {
            rows.Add(StateRow.Word("Setup_HandOver_State", "Setup_HandOver_NotPassed"));
            rows.Add(StateRow.Word("Setup_HandOver_Letters", "Setup_HandOver_LettersNotChecked"));
            rows.Add(StateRow.Data("Setup_HandOver_DerivedSid", user.Sid));
            return rows;
        }

        var check = Identity.Compare(args, user.Sid, user.Desktop);
        AppLog.Write("elevation", "Identity check " + check + "; passed SID " + (args.DesktopSid ?? "none") + ", derived " + (user.Sid ?? "none"));
        rows.Add(StateRow.Word("Setup_HandOver_State", "Identity_" + check));
        rows.Add(StateRow.Data("Setup_HandOver_PassedSid", args.DesktopSid));
        rows.Add(StateRow.Data("Setup_HandOver_DerivedSid", user.Sid));
        rows.Add(StateRow.Data("Setup_HandOver_PassedDesktop", args.DesktopPath));
        rows.Add(StateRow.Data("Setup_HandOver_DerivedDesktop", user.Desktop));
        if (user.Error is not null)
        {
            rows.Add(new StateRow { LabelKey = "Setup_HandOver_DerivedSid", Status = ReadStatus.Failed, Error = user.Error });
        }

        // A forged hand-over is refused whole, letters included.
        if (check == IdentityCheck.Mismatch)
        {
            rows.Add(StateRow.Word("Setup_HandOver_Letters", "Setup_HandOver_LettersRefused"));
        }
        else if (!args.LettersPassed)
        {
            rows.Add(StateRow.Word("Setup_HandOver_Letters", "Setup_HandOver_LettersNotChecked"));
        }
        else
        {
            acceptedLetters = args.Letters;
            rows.Add(StateRow.Data("Setup_HandOver_Letters", args.Letters.Length == 0 ? "-" : string.Join(" ", args.Letters.ToCharArray())));
        }

        if (args.Rejected.Count > 0)
        {
            rows.Add(StateRow.Data("Setup_HandOver_Rejected", string.Join(" ", args.Rejected)));
        }

        return rows;
    }
}
