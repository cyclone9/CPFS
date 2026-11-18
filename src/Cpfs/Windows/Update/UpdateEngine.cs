using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cpfs.Core.Updates;

namespace Cpfs.Windows.Update;

internal enum UpdateStep
{
    Download,
    Stage,
    Probe,
    Swap,
}

// SaveAsBytes: the verified exe, when it can't replace the running one (a network drive, a folder CPFS can't write).
internal sealed record UpdateDone(byte[]? SaveAsBytes, bool TrayStopped);

// The update's steps after "Update now" (updater design, section 2), each callable on its own so the window and the CI
// test run the same code. Nothing here is called at start, on a timer or by the tray (U1); the caller refuses an
// administrator window (U2). It touches only the exe it was started from and the three .cpfs-* names beside it (U5),
// and every check passes before the first rename (U4).
internal static class UpdateEngine
{
    // Read once when an update begins and passed along: after the swap Assembly.Location names the new file (U6).
    public static string RunningExe => typeof(UpdateEngine).Assembly.Location;

    public static string? OwnProductName =>
        (Attribute.GetCustomAttribute(typeof(UpdateEngine).Assembly, typeof(AssemblyProductAttribute)) as AssemblyProductAttribute)?.Product;

    // Steps 4 to 8 in order. Cancel works until commit() returns true just before the swap; from there the steps run to
    // their end. commit returns false when the user cancelled first.
    public static UpdateDone DownloadAndSwap(UpdateOffer offer, string exe, UpdatePlace place, Action<UpdateStep, long, long>? progress, Func<bool>? commit,
        CancellationToken cancel)
    {
        // S1: the engine's own lock, whatever its caller checked.
        var elevated = AppLog.IsElevated();
        var elevatedAllowed = false;
#if CPFS_UPDATE_TEST
        elevatedAllowed = UpdateTestHooks.HeadlessOnRunner;
#endif
        if (!UpdateRules.EngineMayRun(place, elevated, elevatedAllowed))
        {
            AppLog.Write("update", "Refused: place " + place + (elevated ? ", elevated process" : ""));
            throw new InvalidOperationException("The updater does not run for place " + place + (elevated ? " or in an elevated process" : ""));
        }

        AppLog.Write("update", "Update to " + offer.Version + " started, exe " + exe + ", place " + place);
        if (place == UpdatePlace.InPlace)
        {
            Prepare(exe);
        }

        progress?.Invoke(UpdateStep.Download, 0, offer.ExeSize);
        var bytes = Fetch(offer, (n, total) => progress?.Invoke(UpdateStep.Download, n, total), cancel);
        if (place != UpdatePlace.InPlace)
        {
            return new UpdateDone(bytes, false);
        }

        Cancelled(cancel);
        progress?.Invoke(UpdateStep.Stage, 0, 0);
        string staged;
        FileStream hold;
        try
        {
            staged = Stage(exe, bytes, offer.Version, out hold);
        }
        catch (UpdateException ex) when (ex.Error == UpdateError.CannotWrite)
        {
            return new UpdateDone(bytes, false);
        }

        // From the staging file to the swap, every way out removes it (S5); Swap takes it over from its first line.
        var swapping = false;
        try
        {
            Cancelled(cancel);
            progress?.Invoke(UpdateStep.Probe, 0, 0);
            Probe(staged, offer.Version);
            Cancelled(cancel);
            if (commit is not null && !commit())
            {
                Cancelled(new CancellationToken(true));
            }

            progress?.Invoke(UpdateStep.Swap, 0, 0);
            var tray = StopTrayFromSameExe(exe);
            swapping = true;
            try
            {
                Swap(exe, staged, UpdateRules.Sha256Hex(bytes), hold);
            }
            catch (UpdateException ex) when (tray && ex.Error == UpdateError.SwapFailed)
            {
                // S9: the previous exe is back under its name, so its tray icon comes back too.
                TrayControl.StartIfMissing("update not finished", TimeSpan.Zero);
                throw;
            }

            return new UpdateDone(null, tray);
        }
        catch when (!swapping)
        {
            hold.Dispose();
            if (File.Exists(staged))
            {
                TryDelete(staged);
            }

            throw;
        }
    }

    // Step 9 after the swap; the caller has released the single-instance mutex. On a rollback the previous version is
    // started again to say so. The old process does nothing else afterwards (U6).
    public static RestartOutcome Restart(string exe, string oldVersion, string newVersion)
    {
        // S6: the swap is done, so a name held by another object must not stop the start; there is then no signal to
        // wait for, and a started process counts as "Keep".
        EventWaitHandle? started = null;
        try
        {
            started = CreateStartedEvent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            AppLog.Write("update", "Could not create the started event: " + ex.GetType().Name + "; starting the new version without waiting for its window");
        }

        RestartOutcome outcome;
        using (started)
        using (var process = StartNew(exe, oldVersion))
        {
            outcome = started is not null ? WaitStarted(process, started)
                : process is null ? UpdateRules.AfterStart(true, false, null) : RestartOutcome.Keep;
        }

        if (outcome == RestartOutcome.RollBack)
        {
            if (!RollBack(exe))
            {
                throw new UpdateException(UpdateError.RollbackFailed, exe);
            }

            if (!StartPrevious(exe, newVersion))
            {
                throw new UpdateException(UpdateError.PreviousNotStarted, newVersion);
            }
        }

        return outcome;
    }

    public static EventWaitHandle CreateStartedEvent()
    {
        var handle = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateRules.StartedEventName, out var created);
        if (!created)
        {
            handle.Reset();
        }

        return handle;
    }

    // The new window's side, once it has rendered.
    public static void SignalStarted()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(UpdateRules.StartedEventName, System.Security.AccessControl.EventWaitHandleRights.Modify, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }

                AppLog.Write("update", "Told the previous version that this window is up");
            }
            else
            {
                AppLog.Write("update", "The previous version is no longer waiting");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            AppLog.Write("update", "Could not signal the previous version: " + ex.GetType().Name);
        }
    }

    // At an unelevated window start. Right after an update the previous process may still be ending, so it tries for a
    // while; otherwise once.
    public static void CleanLeftoversAtStart(bool afterUpdate)
    {
        var exe = RunningExe;
        var until = DateTime.UtcNow + (afterUpdate ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
        while (true)
        {
            CleanLeftovers(exe, afterUpdate ? "after an update" : "at start");
            var left = false;
            foreach (var path in UpdateRules.LeftoverPaths(exe))
            {
                left |= File.Exists(path);
            }

            if (!left || DateTime.UtcNow >= until)
            {
                return;
            }

            Thread.Sleep(2000);
        }
    }

    // The caller removes a staging file it made.
    private static void Cancelled(CancellationToken cancel)
    {
        if (!cancel.IsCancellationRequested)
        {
            return;
        }

        AppLog.Write("update", "Cancelled; nothing was replaced");
        throw new UpdateException(UpdateError.Cancelled);
    }

    // Section 3, decided before anything is downloaded.
    public static UpdatePlace PlaceOf(string exe, bool elevated)
    {
        bool? network = false;
        if (!exe.StartsWith(@"\\", StringComparison.Ordinal))
        {
            try
            {
                var type = new DriveInfo(Path.GetPathRoot(exe)!).DriveType;
                network = type is DriveType.Unknown or DriveType.NoRootDirectory ? null : type == DriveType.Network;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                AppLog.Write("update", "Drive type of " + exe + " unknown: " + ex.GetType().Name);
                network = null;
            }
        }

        var place = UpdateRules.Place(exe, elevated, network, Apply.AutoPrivateCopy.Path);
        AppLog.Write("update", "Exe " + exe + ", drive " + DriveTypeOf(exe) + ", place " + place);
        return place;
    }

    // Before the download: earlier leftovers go now, or the update stops before it changes anything.
    public static void Prepare(string exe)
    {
        CleanLeftovers(exe, "before an update");
        foreach (var path in UpdateRules.LeftoverPaths(exe))
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                throw new UpdateException(UpdateError.Leftover, Path.GetFileName(path));
            }
        }
    }

    // Steps 4 and 5: both files into memory, then every check on the bytes. The exe's verified bytes come back.
    public static byte[] Fetch(UpdateOffer offer, Action<long, long>? progress, CancellationToken cancel)
    {
        AppLog.Write("update", "Downloading " + offer.Version + " (" + offer.ExeSize.ToString(CultureInfo.InvariantCulture) + " bytes)");
        var deadline = DateTime.UtcNow + UpdateRules.TotalLimit;
        try
        {
            var sha = Get(offer.ShaName, offer.ShaUrl, UpdateRules.MaxShaBytes, null, deadline, cancel);
            var exe = Get(offer.ExeName, offer.ExeUrl, UpdateRules.MaxExeBytes, n => progress?.Invoke(n, offer.ExeSize), deadline, cancel);
            var refusal = UpdateRules.Verify(offer, exe, sha, out var computed, out var published);
            AppLog.Write("update", "SHA-256 published " + (published ?? "(unreadable)") + ", API " + (offer.ExeDigest ?? "(none)") + ", computed " + computed
                + ", size " + exe.LongLength.ToString(CultureInfo.InvariantCulture) + ": " + (refusal == UpdateRefusal.None ? "match" : refusal.ToString()));
            if (refusal != UpdateRefusal.None)
            {
                throw new UpdateException(UpdateError.Damaged);
            }

            return exe;
        }
        catch (OperationCanceledException)
        {
            throw new UpdateException(UpdateError.Cancelled);
        }
    }

    // Step 6. Returns the staged path and a read handle that lets no one write, rename or delete the file (S7): the
    // probe runs the very bytes that were checked. The caller closes it before the file is renamed or removed. On any
    // failure after creating the file, it is gone again; a file of that name this call did not create is left (S4).
    public static string Stage(string exe, byte[] bytes, string version, out FileStream hold)
    {
        var staged = UpdateRules.StagedPath(exe);
        var created = false;
        try
        {
            using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            output.Write(bytes, 0, bytes.Length);
            output.Flush(true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            AppLog.Write("update", "Cannot write " + staged + ": " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture)
                + (created ? "" : " (not created)"));
            if (created)
            {
                TryDelete(staged);
            }

            throw new UpdateException(UpdateError.CannotWrite);
        }

        FileStream? held = null;
        try
        {
            held = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
            string back;
            using (var sha = SHA256.Create())
            {
                back = UpdateRules.Hex(sha.ComputeHash(held));
            }

            if (!string.Equals(back, UpdateRules.Sha256Hex(bytes), StringComparison.Ordinal))
            {
                AppLog.Write("update", "Staged file read back as " + back + ": differs");
                throw new UpdateException(UpdateError.Damaged);
            }

            // By path, but nothing can change the file while the handle is open.
            var product = FileVersionInfo.GetVersionInfo(staged).ProductVersion;
            AppLog.Write("update", "Staged " + staged + ", product version " + (product ?? "(none)") + "; held open for reading until the swap");
            if (!UpdateRules.ProductVersionMatches(product, version))
            {
                throw new UpdateException(UpdateError.WrongVersion, version);
            }

            hold = held;
            return staged;
        }
        catch
        {
            held?.Dispose();
            TryDelete(staged);
            throw;
        }
    }

    // Step 7: the staged exe runs its probe once while the working version is still in place.
    public static void Probe(string staged, string version)
    {
        var code = -1;
        try
        {
            var start = new ProcessStartInfo(staged, UpdateRules.ProbeVerb + " " + version)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(staged) ?? "",
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("no process");
            if (!process.WaitForExit((int)UpdateRules.ProbeLimit.TotalMilliseconds))
            {
                AppLog.Write("update", "Probe did not end within 30 s");
                try
                {
                    process.Kill();
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    AppLog.Write("update", "Could not end the probe: " + ex.GetType().Name);
                }

                process.WaitForExit(5000);
            }
            else
            {
                code = process.ExitCode;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Write("update", "Probe could not start: " + ex.GetType().Name + (ex is Win32Exception w ? " " + w.NativeErrorCode.ToString(CultureInfo.InvariantCulture) : ""));
        }

        AppLog.Write("update", "Probe exit code " + code.ToString(CultureInfo.InvariantCulture) + (code == UpdateRules.ProbeOk ? " (ok)" : ""));
        if (code != UpdateRules.ProbeOk)
        {
            throw new UpdateException(UpdateError.ProbeFailed);
        }
    }

    // A tray started from this very file is asked to exit, so it doesn't run the old copy on; one from another file is
    // left alone. True when one was asked.
    public static bool StopTrayFromSameExe(string exe)
    {
        if (!TrayControl.IsRunning())
        {
            return false;
        }

        var self = Process.GetCurrentProcess().Id;
        var same = false;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (process)
            {
                if (process.Id != self && string.Equals(ImagePath(process.Id), exe, StringComparison.OrdinalIgnoreCase))
                {
                    same = true;
                }
            }
        }

        if (!same)
        {
            AppLog.Write("update", "The tray icon runs from another file; left alone");
            return false;
        }

        TrayControl.Stop("update");
        var gone = TrayControl.WaitUntilGone(UpdateRules.TrayStopLimit);
        AppLog.Write("update", "Tray icon " + (gone ? "stopped" : "still running after 3 s; its old file is removed at a later start"));
        return true;
    }

    // Step 8: two renames in the same folder. The first is undone if the second fails. hold is Stage's handle on the
    // staged file, closed here just before that file is renamed or removed.
    public static void Swap(string exe, string staged, string hash, FileStream? hold)
    {
        var old = UpdateRules.OldPath(exe);
        try
        {
            File.Move(exe, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("update", "Rename " + exe + " -> " + old + " failed: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
            hold?.Dispose();
            TryDelete(staged);
            throw new UpdateException(UpdateError.SwapFailed, ex.Message);
        }
        catch
        {
            hold?.Dispose();
            TryDelete(staged);
            throw;
        }

        AppLog.Write("update", "Renamed " + exe + " -> " + old);

        // From here every failure puts the previous exe back (review E1); an antivirus may lock or remove the new file.
        var placed = false;
        string? windowsSaid;
        try
        {
#if CPFS_UPDATE_TEST
            if (UpdateTestHooks.FailSecondRename)
            {
                throw new IOException("test hook: the second rename fails");
            }
#endif
            // A rename needs the file free; the read-back below covers the moment between.
            hold?.Dispose();
            File.Move(staged, exe);
            placed = true;
            AppLog.Write("update", "Renamed " + staged + " -> " + exe);
#if CPFS_UPDATE_TEST
            if (UpdateTestHooks.FailReadBack)
            {
                throw new IOException("test hook: the read-back fails");
            }
#endif
            var now = Sha256Of(exe);
            if (string.Equals(now, hash, StringComparison.Ordinal))
            {
                return;
            }

            AppLog.Write("update", "The file at " + exe + " reads as " + now + " after the swap: putting the previous one back");
            windowsSaid = null;
        }
        catch (Exception ex)
        {
            AppLog.Write("update", (placed ? "Read-back of " + exe : "Rename " + staged + " -> " + exe) + " failed: " + ex.GetType().Name
                + " 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
            windowsSaid = ex.Message;
        }

        hold?.Dispose();
        var back = placed ? RollBack(exe) : Undo(exe, old, "after the second rename failed");
        if (!placed && File.Exists(staged))
        {
            TryDelete(staged);
        }

        if (!back)
        {
            throw new UpdateException(UpdateError.RollbackFailed, exe);
        }

        throw new UpdateException(UpdateError.SwapFailed, windowsSaid);
    }

    // Step 9's start. Null when it could not be started (then the caller rolls back).
    public static Process? StartNew(string exe, string oldVersion)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo(exe, UpdateRules.AfterUpdateOption + " " + oldVersion)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            });
            AppLog.Write("update", "Started " + exe + " " + UpdateRules.AfterUpdateOption + " " + oldVersion + ", process "
                + (process?.Id.ToString(CultureInfo.InvariantCulture) ?? "?"));
            return process;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Write("update", "The new version could not be started: " + ex.GetType().Name + (ex is Win32Exception w ? " " + w.NativeErrorCode.ToString(CultureInfo.InvariantCulture) : ""));
            return null;
        }
    }

    // Waits for the new window's signal or the new process's end, up to 20 s.
    public static RestartOutcome WaitStarted(Process? process, EventWaitHandle started)
    {
        if (process is null)
        {
            return UpdateRules.AfterStart(true, false, null);
        }

        var until = DateTime.UtcNow + UpdateRules.StartedLimit;
        var signalled = false;
        int? code = null;
        while (DateTime.UtcNow < until)
        {
            if (started.WaitOne(100))
            {
                signalled = true;
                break;
            }

            if (process.HasExited)
            {
                // It may have set the event just before it ended.
                signalled = started.WaitOne(0);
                code = process.ExitCode;
                break;
            }
        }

        var outcome = UpdateRules.AfterStart(false, signalled, code);
        AppLog.Write("update", "New version: " + (signalled ? "window shown" : code is { } c ? "ended with code " + c.ToString(CultureInfo.InvariantCulture) : "no answer within 20 s") + "; " + outcome);
        return outcome;
    }

    // The new file goes to .cpfs-failed and the previous one back to its name. True when the previous exe is in place.
    public static bool RollBack(string exe)
    {
        var old = UpdateRules.OldPath(exe);
        var failed = UpdateRules.FailedPath(exe);
        if (!File.Exists(old))
        {
            AppLog.Write("update", "Rollback: " + old + " is missing");
            return false;
        }

        // Through the U5 check (S4): an earlier .cpfs-failed goes only when it is CPFS's own regular file.
        if (!DeleteIfOurs(failed, "rollback"))
        {
            AppLog.Write("update", "Rollback: " + failed + " is in the way");
            return false;
        }

        try
        {
            if (File.Exists(exe))
            {
                File.Move(exe, failed);
                AppLog.Write("update", "Rollback: renamed " + exe + " -> " + failed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("update", "Rollback: could not set the new file aside: " + ex.GetType().Name);
            return false;
        }

        return Undo(exe, old, "rollback");
    }

    // After a rollback: the previous version starts and says what happened. False when it could not be started; the
    // exiting process then says it instead.
    public static bool StartPrevious(string exe, string failedVersion)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, UpdateRules.UpdateFailedOption + " " + failedVersion)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            });
            AppLog.Write("update", "Started the previous version with " + UpdateRules.UpdateFailedOption + " " + failedVersion);
            return process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Write("update", "Could not start the previous version: " + ex.GetType().Name);
            return false;
        }
    }

    // Step 11, at every unelevated window start and before an update: the three names beside the exe, only regular files
    // (no link, no folder) whose product name is CPFS's. One in use (an old tray) waits for a later start.
    public static void CleanLeftovers(string exe, string reason)
    {
        foreach (var path in UpdateRules.LeftoverPaths(exe))
        {
            DeleteIfOurs(path, reason);
        }
    }

    // True when nothing is at path afterwards. A link, a folder or another product's file is left and gives false.
    private static bool DeleteIfOurs(string path, string reason)
    {
        var product = OwnProductName;
        try
        {
            if (!File.Exists(path))
            {
                return !Directory.Exists(path);
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                AppLog.Write("update", "Left " + path + " (" + reason + "): a link, not a file");
                return false;
            }

            var name = FileVersionInfo.GetVersionInfo(path).ProductName;
            if (product is null || !string.Equals(name, product, StringComparison.Ordinal))
            {
                AppLog.Write("update", "Left " + path + " (" + reason + "): not CPFS's file");
                return false;
            }

            File.Delete(path);
            AppLog.Write("update", "Removed " + path + " (" + reason + ")");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Write("update", "Could not remove " + path + " (" + reason + "), in use or denied: " + ex.GetType().Name);
            return false;
        }
    }

    // Save as…: the verified bytes to the user's chosen file, read back and hashed. Never the running exe or the copy.
    public static void SaveVerified(byte[] bytes, string target, string running)
    {
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(running), StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFullPath(target), Path.GetFullPath(Apply.AutoPrivateCopy.Path), StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(UpdateError.SaveFailed, Path.GetFileName(target));
        }

        var hash = UpdateRules.Sha256Hex(bytes);
        try
        {
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
            }

            var back = Sha256Of(target);
            AppLog.Write("update", "Saved the verified file as " + target + ", read back " + (back == hash ? "matching" : "as " + back));
            if (!string.Equals(back, hash, StringComparison.Ordinal))
            {
                TryDelete(target);
                throw new UpdateException(UpdateError.Damaged);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Write("update", "Save as " + target + " failed: " + ex.GetType().Name);
            throw new UpdateException(UpdateError.SaveFailed, ex.Message);
        }
    }

    public static string DownloadsFolder()
    {
        if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out var path) == 0)
        {
            try
            {
                var text = Marshal.PtrToStringUni(path);
                if (!string.IsNullOrEmpty(text))
                {
                    return text!;
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return UpdateRules.Hex(sha.ComputeHash(stream));
    }

    private static byte[] Get(string name, string url, long max, Action<long>? progress, DateTime deadline, CancellationToken cancel)
    {
#if CPFS_UPDATE_TEST
        if (UpdateTestHooks.SourceDir is { } dir)
        {
            return UpdateTestHooks.ReadAsset(dir, name, max, progress);
        }
#endif
        return UpdateNet.Download(url, max, progress, deadline, cancel);
    }

    private static bool Undo(string exe, string old, string why)
    {
        try
        {
            File.Move(old, exe);
            AppLog.Write("update", "Renamed " + old + " -> " + exe + " (" + why + ")");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The README says which file to rename by hand.
            AppLog.Write("update", "COULD NOT put " + old + " back as " + exe + " (" + why + "): " + ex.GetType().Name);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            AppLog.Write("update", "Removed " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("update", "Could not remove " + path + ": " + ex.GetType().Name);
        }
    }

    private static string DriveTypeOf(string exe)
    {
        if (exe.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "UNC";
        }

        try
        {
            return new DriveInfo(Path.GetPathRoot(exe)!).DriveType.ToString();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    private static string? ImagePath(int pid)
    {
        using var handle = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[1024];
        var size = buffer.Length;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    // FOLDERID_Downloads [Learn: KNOWNFOLDERID].
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    // [Learn: QueryFullProcessImageNameW] flags 0 = Win32 path; PROCESS_QUERY_LIMITED_INFORMATION is enough.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(Native.SafeKernelHandle process, int flags, [Out] char[] name, ref int size);
}
