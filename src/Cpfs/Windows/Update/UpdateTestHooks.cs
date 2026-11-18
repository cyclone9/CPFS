#if CPFS_UPDATE_TEST
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Cpfs.Core.Updates;

namespace Cpfs.Windows.Update;

// Compiled only with -p:CpfsUpdateTest=true, which release.yml never sets and --selftest reports (U8). A folder stands
// in for the GitHub release (latest.json and the two assets), and environment variables make single steps fail. None
// of this exists in a normal build.
internal static class UpdateTestHooks
{
    public const string CiVerb = "--ci-update-test";
    public const string CiRealVerb = "--ci-update-real";
    public const string SourceOption = "--update-test-source";
    public const string LatestFile = "latest.json";

    private const int Skipped = 78;

    public static string? SourceDir { get; private set; }

    public static bool FailProbe => Env("CPFS_UPDATE_TEST_FAIL_PROBE");

    public static bool FailSecondRename => Env("CPFS_UPDATE_TEST_FAIL_SECOND_RENAME");

    public static bool FailStart => Env("CPFS_UPDATE_TEST_FAIL_START");

    // The new exe answers --after-update / --update-failed without a window: the runner has no one to look at it.
    public static bool Headless => Env("CPFS_UPDATE_TEST_HEADLESS");

    // "--update-test-source <folder>" for a laptop test: removed from the arguments, the folder must exist.
    public static string[] TakeSource(string[] args)
    {
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], SourceOption, StringComparison.Ordinal) && i + 1 < args.Length)
            {
                var dir = args[++i];
                if (Path.IsPathRooted(dir) && Directory.Exists(dir))
                {
                    SourceDir = Path.GetFullPath(dir);
                }

                continue;
            }

            rest.Add(args[i]);
        }

        return rest.ToArray();
    }

    public static byte[] ReadAsset(string dir, string name, long max, Action<long>? progress)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path))
        {
            AppLog.Write("update", "Test source: " + name + " is missing");
            throw new UpdateException(UpdateError.Missing);
        }

        if (new FileInfo(path).Length > max)
        {
            throw new UpdateException(UpdateError.TooLarge);
        }

        var bytes = File.ReadAllBytes(path);
        progress?.Invoke(bytes.LongLength);
        AppLog.Write("update", "Test source: read " + name + ", " + bytes.LongLength.ToString(CultureInfo.InvariantCulture) + " bytes");
        return bytes;
    }

    // The new exe's side of the restart, headless: fail on request, else say "started" and clean up.
    public static int HeadlessStart()
    {
        if (Startup.UpdateFailedVersion is not null)
        {
            AppLog.Write("update", "Test: started after a rollback of " + Startup.UpdateFailedVersion);
            return 0;
        }

        if (FailStart)
        {
            AppLog.Write("update", "Test: failing at start on purpose");
            return 3;
        }

        AppLog.Write("update", "Test: started after update from " + Startup.AfterUpdateFrom);
        UpdateEngine.SignalStarted();
        UpdateEngine.CleanLeftoversAtStart(afterUpdate: true);
        return 0;
    }

    // "--ci-update-test <folder>": the whole update of this running exe from the folder, headless, on a GitHub runner
    // only. It prints one "OUTCOME:" line for the workflow to judge. Exit 0 when it ran, 1 on a surprise, 2 refused.
    public static int RunCi(string[] args)
    {
        SelfTest.AttachToParentConsole();
        var o = Console.Out;
        try
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal))
            {
                o.WriteLine("Refused: GITHUB_ACTIONS is not true");
                return 2;
            }

            var at = Array.IndexOf(args, CiVerb);
            if (at < 0 || at + 1 >= args.Length || !Directory.Exists(args[at + 1]))
            {
                o.WriteLine("Refused: no source folder");
                return 2;
            }

            SourceDir = Path.GetFullPath(args[at + 1]);
            o.WriteLine("CPFS update test " + AppLog.VersionText() + ", source " + SourceDir);
            var check = UpdateClient.CheckAsync("ci").GetAwaiter().GetResult();
            o.WriteLine("CHECK: " + check.Status + " " + (check.Version ?? "") + ", " + check.Refusal);
            if (check.Offer is not { } offer)
            {
                o.WriteLine("OUTCOME: NoOffer:" + check.Refusal);
                return 0;
            }

            var exe = UpdateEngine.RunningExe;

            // The runner is elevated; an administrator window never gets this far in a real run (U2).
            var place = UpdateEngine.PlaceOf(exe, elevated: false);
            if (place != UpdatePlace.InPlace)
            {
                o.WriteLine("OUTCOME: Place:" + place);
                return 0;
            }

            var result = UpdateEngine.DownloadAndSwap(offer, exe, place, null, null, CancellationToken.None);
            if (result.SaveAsBytes is { } bytes)
            {
                var target = Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath(), "cpfs-saved-" + offer.ExeName);
                UpdateEngine.SaveVerified(bytes, target, exe);
                o.WriteLine("SAVED: " + target + " " + UpdateRules.Sha256Hex(bytes));
                o.WriteLine("OUTCOME: SaveAs");
                return 0;
            }

            var outcome = UpdateEngine.Restart(exe, AppLog.VersionNumber(), offer.Version);
            o.WriteLine("OUTCOME: Restart:" + outcome);
            return 0;
        }
        catch (UpdateException ex)
        {
            o.WriteLine("OUTCOME: Error:" + ex.Error);
            return 0;
        }
        catch (Exception ex)
        {
            o.WriteLine("Result: unexpected exception");
            o.WriteLine(ex.ToString());
            AppLog.Exception("ci-update", ex);
            return 1;
        }
        finally
        {
            o.Flush();
        }
    }

    // "--ci-update-real": the real latest release through the real request and redirect code, checked, never installed.
    // 0 PASS, 78 SKIP (network or rate limit, with the reason), 1 FAIL.
    public static int RunReal()
    {
        SelfTest.AttachToParentConsole();
        var o = Console.Out;
        try
        {
            string body;
            try
            {
                var request = UpdateNet.NewRequest(new Uri(UpdateCheck.ApiUrl), UpdateCheck.Accept, UpdateRules.HeadersTimeoutMs, UpdateRules.ReadTimeoutMs);
                using var response = (HttpWebResponse)request.GetResponse();
                using var reader = new StreamReader(response.GetResponseStream()!, Encoding.UTF8);
                body = reader.ReadToEnd();
            }
            catch (WebException ex)
            {
                var status = ex.Response is HttpWebResponse r ? (int)r.StatusCode : 0;
                o.WriteLine("SKIP: the releases API did not answer (" + ex.Status + (status == 0 ? "" : ", HTTP " + status.ToString(CultureInfo.InvariantCulture)) + ")");
                return status is 0 or 403 or 429 or >= 500 ? Skipped : 1;
            }

            // Any published release is "newer" than 0.0.1, so the strict rules and the download run on the real one.
            var offer = UpdateRules.ReadOffer(body, "0.0.1", out var refusal);
            if (offer is null)
            {
                o.WriteLine("FAIL: the latest release is not installable: " + refusal);
                return 1;
            }

            var bytes = UpdateEngine.Fetch(offer, null, CancellationToken.None);
            o.WriteLine("PASS: " + offer.ExeName + ", " + bytes.LongLength.ToString(CultureInfo.InvariantCulture) + " bytes, SHA-256 " + UpdateRules.Sha256Hex(bytes)
                + " matches its .sha256" + (offer.ExeDigest is null ? "" : " and the API digest"));
            return 0;
        }
        catch (UpdateException ex) when (ex.Error is UpdateError.Network or UpdateError.RateLimited)
        {
            o.WriteLine("SKIP: " + ex.Error);
            return Skipped;
        }
        catch (Exception ex)
        {
            o.WriteLine("FAIL: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
        finally
        {
            o.Flush();
        }
    }

    private static bool Env(string name) => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);
}
#endif
