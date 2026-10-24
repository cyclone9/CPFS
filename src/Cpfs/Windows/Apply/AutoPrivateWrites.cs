using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Cpfs.Core.Planning;
using Microsoft.Win32;

namespace Cpfs.Windows.Apply;

// The CPFS-AutoPrivate task through the Task Scheduler 2.0 COM API, as RunAsUser does: a SYSTEM task in the root
// folder, started 5 s after event 10000 of the NetworkProfile channel, running the exe copy in Program Files\CPFS with
// --auto-private. Only the two Auto-Private names can be deleted here.
internal static class AutoPrivateTask
{
    private const int CreateOrUpdate = 6;
    private const int StateQueued = 2;
    private const int StateRunning = 4;

    public static ScheduledTaskInfo? Read() => Read(Planner.AutoPrivateTask);

    public static bool Exists(string name) => WithFolder(folder => Find(folder, name) is { } task && Release(task));

    // For the log, before a task of this name is replaced: what it ran.
    public static string ActionText()
    {
        var info = Read();
        if (info is null)
        {
            return "nothing (no task)";
        }

        var parts = new List<string>();
        foreach (var a in info.Actions)
        {
            parts.Add(a.Type == AutoPrivateTaskSpec.ActionExec ? (a.Path ?? "?") + " " + (a.Arguments ?? "") : "action type " + a.Type.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join("; ", parts) + " as " + (info.UserSid ?? "?");
    }

    public static ScheduledTaskInfo? Read(string name) => WithFolder<ScheduledTaskInfo?>(folder =>
    {
        var task = Find(folder, name);
        if (task is null)
        {
            return null;
        }

        object? definition = null;
        try
        {
            definition = Get(task, "Definition")!;
            var principal = Get(definition, "Principal")!;
            var userId = Get(principal, "UserId") as string;
            var logon = ToInt(Get(principal, "LogonType"));
            var level = ToInt(Get(principal, "RunLevel"));
            Release(principal);

            var triggers = new List<TaskTriggerInfo>();
            var triggerList = Get(definition, "Triggers")!;
            for (var i = 1; i <= ToInt(Get(triggerList, "Count")); i++)
            {
                var t = Item(triggerList, i)!;
                var type = ToInt(Get(t, "Type"));
                triggers.Add(type == AutoPrivateTaskSpec.TriggerEvent
                    ? new TaskTriggerInfo(type, Get(t, "Subscription") as string, Get(t, "Delay") as string)
                    : new TaskTriggerInfo(type, null, null));
                Release(t);
            }

            Release(triggerList);

            var actions = new List<TaskActionInfo>();
            var actionList = Get(definition, "Actions")!;
            for (var i = 1; i <= ToInt(Get(actionList, "Count")); i++)
            {
                var a = Item(actionList, i)!;
                var type = ToInt(Get(a, "Type"));
                actions.Add(type == AutoPrivateTaskSpec.ActionExec
                    ? new TaskActionInfo(type, Get(a, "Path") as string, Get(a, "Arguments") as string)
                    : new TaskActionInfo(type, null, null));
                Release(a);
            }

            Release(actionList);

            var settings = Get(definition, "Settings")!;
            var limit = Get(settings, "ExecutionTimeLimit") as string;
            var instances = ToInt(Get(settings, "MultipleInstances"));
            Release(settings);
            return new ScheduledTaskInfo(SidOf(userId), logon, level, triggers, actions, limit, instances);
        }
        finally
        {
            Release(definition);
            Release(task);
        }
    });

    public static void Register(string exePath)
    {
        // Task Scheduler takes an account name; the SID gives the name in this Windows' language.
        var system = new SecurityIdentifier(AutoPrivateTaskSpec.SystemSid).Translate(typeof(NTAccount)).Value;
        WithService((service, folder) =>
        {
            object? definition = null;
            object? registered = null;
            try
            {
                definition = Invoke(service, "NewTask", 0)!;

                var info = Get(definition, "RegistrationInfo")!;
                Set(info, "Description", Planner.ShareDescription + ": marks a network this PC has never seen Private, once (Auto-Private).");
                Release(info);

                var principal = Get(definition, "Principal")!;
                Set(principal, "UserId", system);
                Set(principal, "LogonType", AutoPrivateTaskSpec.LogonServiceAccount);
                Set(principal, "RunLevel", AutoPrivateTaskSpec.RunLevelHighest);
                Release(principal);

                var settings = Get(definition, "Settings")!;
                Set(settings, "DisallowStartIfOnBatteries", false);
                Set(settings, "StopIfGoingOnBatteries", false);
                Set(settings, "AllowDemandStart", true);
                Set(settings, "ExecutionTimeLimit", AutoPrivateTaskSpec.ExecutionTimeLimit);
                Set(settings, "MultipleInstances", AutoPrivateTaskSpec.InstancesIgnoreNew);
                Set(settings, "Hidden", false);
                Release(settings);

                var triggers = Get(definition, "Triggers")!;
                var trigger = Invoke(triggers, "Create", AutoPrivateTaskSpec.TriggerEvent)!;
                Set(trigger, "Subscription", AutoPrivateTaskSpec.Subscription);
                Set(trigger, "Delay", AutoPrivateTaskSpec.Delay);
                Release(trigger);
                Release(triggers);

                var actions = Get(definition, "Actions")!;
                var action = Invoke(actions, "Create", AutoPrivateTaskSpec.ActionExec)!;
                Set(action, "Path", exePath);
                Set(action, "Arguments", AutoPrivateTaskSpec.Arguments);
                Release(action);
                Release(actions);

                // A service account takes no password: an empty VARIANT [Learn: TaskFolder.RegisterTaskDefinition].
                registered = Invoke(folder, "RegisterTaskDefinition", Planner.AutoPrivateTask, definition, CreateOrUpdate, system, null,
                    AutoPrivateTaskSpec.LogonServiceAccount, null);
            }
            finally
            {
                Release(registered);
                Release(definition);
            }

            return true;
        });
    }

    // Deleted by name, in the root folder only; a task that is already gone counts as done.
    public static void Delete(string name)
    {
        if (!string.Equals(name, Planner.AutoPrivateTask, StringComparison.Ordinal) && !string.Equals(name, Planner.LegacyAutoPrivateTask, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CPFS deletes no task named " + name + ".");
        }

        WithFolder(folder =>
        {
            try
            {
                Invoke(folder, "DeleteTask", name, 0);
            }
            catch (TargetInvocationException ex) when (IsNotFound(ex))
            {
            }

            return true;
        });
    }

    // For the CI test only: starts the registered task and waits for it; returns its last result and run time.
    public static (int Result, DateTime LastRun) RunAndWait(TimeSpan limit)
    {
        return WithFolder(folder =>
        {
            var task = Find(folder, Planner.AutoPrivateTask) ?? throw new InvalidOperationException("The task does not exist.");
            try
            {
                var before = (DateTime)Get(task, "LastRunTime")!;
                Release(Invoke(task, "Run", (object?)null));
                var watch = Stopwatch.StartNew();
                var started = false;
                while (watch.Elapsed < limit)
                {
                    Thread.Sleep(200);
                    var state = ToInt(Get(task, "State"));
                    var busy = state is StateRunning or StateQueued;
                    started |= busy || (DateTime)Get(task, "LastRunTime")! != before;
                    if (started && !busy)
                    {
                        break;
                    }
                }

                return (ToInt(Get(task, "LastTaskResult")), (DateTime)Get(task, "LastRunTime")!);
            }
            finally
            {
                Release(task);
            }
        });
    }

    private static string? SidOf(string? userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        try
        {
            return userId!.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(userId).Value
                : ((SecurityIdentifier)new NTAccount(userId).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException or SystemException)
        {
            return null;
        }
    }

    private static object? Find(object folder, string name)
    {
        try
        {
            return Invoke(folder, "GetTask", name);
        }
        catch (TargetInvocationException ex) when (IsNotFound(ex))
        {
            return null;
        }
    }

    private static bool IsNotFound(TargetInvocationException ex) =>
        ex.InnerException is COMException { HResult: unchecked((int)0x80070002) } or FileNotFoundException;

    private static T WithFolder<T>(Func<object, T> work) => WithService((_, folder) => work(folder));

    private static T WithService<T>(Func<object, object, T> work)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        var service = Activator.CreateInstance(type)!;
        object? folder = null;
        try
        {
            Invoke(service, "Connect");
            folder = Invoke(service, "GetFolder", @"\")!;
            return work(service, folder);
        }
        finally
        {
            Release(folder);
            Release(service);
        }
    }

    private static int ToInt(object? value) => Convert.ToInt32(value ?? -1, CultureInfo.InvariantCulture);

    private static object? Invoke(object target, string method, params object?[] args) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    private static object? Get(object target, string property) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);

    // Task Scheduler collections are 1-based.
    private static object? Item(object collection, int index) =>
        collection.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, collection, new object[] { index }, CultureInfo.InvariantCulture);

    private static void Set(object target, string property, object value) =>
        target.GetType().InvokeMember(property, BindingFlags.SetProperty, null, target, new[] { value }, CultureInfo.InvariantCulture);

    private static bool Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.ReleaseComObject(com);
        }

        return true;
    }
}

// The copy of this exe the task runs, in Program Files\CPFS: never the exe the user started, which may sit in a folder
// the user can write to, and not ProgramData\CPFS, which any user can re-create after an admin deletes it by hand
// (ProgramData grants Users "create folders"; Program Files doesn't). Streamed from one open of the source to a temp
// file next to its place while hashing the same bytes, moved in, and the final file hashed again.
internal static class AutoPrivateCopy
{
    private const string ExeName = "Cpfs.exe";

    // The known-folder path, not %ProgramFiles% from this process's environment; the x64 build gets C:\Program Files.
    public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CPFS");

    public static string Path => System.IO.Path.Combine(Folder, ExeName);

    // Where the dev3 test build put the copy.
    public static string OldPath => System.IO.Path.Combine(StateReader.DataFolder, ExeName);

    public static string LegacyScript => System.IO.Path.Combine(StateReader.DataFolder, "set-private.ps1");

    public static string Running => typeof(AutoPrivateCopy).Assembly.Location;

    public static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Hex(sha.ComputeHash(stream));
    }

    private static string Hex(byte[] hash)
    {
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    public static bool IsCurrent() => File.Exists(Path) && string.Equals(Sha256Of(Path), Sha256Of(Running), StringComparison.Ordinal);

    public static AutoPrivateFiles ReadFiles()
    {
        var exists = File.Exists(Path);
        return new AutoPrivateFiles(exists, exists && IsCurrent(), File.Exists(LegacyScript), File.Exists(OldPath));
    }

    public static AutoPrivateFolderInfo ReadFolder()
    {
        if (!Directory.Exists(Folder))
        {
            return new AutoPrivateFolderInfo(false, false, false);
        }

        var others = false;
        foreach (var entry in Directory.EnumerateFileSystemEntries(Folder))
        {
            others |= !string.Equals(System.IO.Path.GetFileName(entry), ExeName, StringComparison.OrdinalIgnoreCase);
        }

        return new AutoPrivateFolderInfo(true, SetupSteps.Locked(Folder), others);
    }

    // expectedSha256: the hash recorded in config.json before this write; the bytes read must be those.
    public static void Write(string expectedSha256)
    {
        var source = Running;
        if (SamePath(source, Path) || SamePath(source, OldPath))
        {
            throw new InvalidOperationException("CPFS runs from its own copy in " + System.IO.Path.GetDirectoryName(source) + "; start the exe you downloaded to update it.");
        }

        NoteSourceFolder(source);
        var temp = Path + ".tmp";
        string read;
        try
        {
            // No FileShare.Write or Delete: the source can't change under the read.
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            using var sha = SHA256.Create();
            var buffer = new byte[81920];
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.TransformBlock(buffer, 0, n, null, 0);
                output.Write(buffer, 0, n);
            }

            sha.TransformFinalBlock(buffer, 0, 0);
            read = Hex(sha.Hash!);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        if (!string.Equals(read, expectedSha256, StringComparison.Ordinal))
        {
            TryDelete(temp);
            throw new IOException("The running program changed while CPFS copied it; nothing was copied to " + Folder + ".");
        }

        Retry(() =>
        {
            if (File.Exists(Path))
            {
                File.Replace(temp, Path, null);
            }
            else
            {
                File.Move(temp, Path);
            }
        });

        if (!string.Equals(Sha256Of(Path), read, StringComparison.Ordinal))
        {
            throw new IOException("The copy of CPFS in " + Folder + " does not match the program that was read.");
        }
    }

    // Informational: whoever can write to the source folder could have swapped the exe before it was started elevated,
    // so the copy adds nothing they don't have already. Logged, never refused.
    private static void NoteSourceFolder(string source)
    {
        var folder = System.IO.Path.GetDirectoryName(source);
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        try
        {
            var security = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            var why = new List<string>();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & FileSystemRights.WriteData) != 0
                    && rule.IdentityReference is SecurityIdentifier sid
                    && (sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid) || sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid) || sid.IsWellKnown(WellKnownSidType.WorldSid)))
                {
                    why.Add(sid.Value + " can write");
                }
            }

            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is not null && !owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                && !string.Equals(owner.Value, DesktopUser.Current.Sid, StringComparison.OrdinalIgnoreCase))
            {
                why.Add("owner " + owner.Value);
            }

            if (why.Count > 0)
            {
                AppLog.Write("apply", "Copied from a folder others can write to: " + folder + " (" + string.Join(", ", why) + ")");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Write("apply", "The source folder's permissions could not be read: " + folder + " (" + ex.GetType().Name + ")");
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("apply", "Could not remove " + path + ": " + ex.Message);
        }
    }

    public static void Remove() => Retry(() => File.Delete(Path));

    public static void RemoveOld() => Retry(() => File.Delete(OldPath));

    // Only when nothing is left in it: CPFS removes no file it didn't put there.
    public static void RemoveFolder() => Retry(() => Directory.Delete(Folder, recursive: false));

    // The task may be running the file at that moment; it finishes within its one-minute limit.
    internal static void Retry(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("apply", "In use, trying again in 2 s: " + ex.Message);
            Thread.Sleep(2000);
            action();
        }
    }
}

// SetComputerNameExW(ComputerNamePhysicalDnsHostname) sets the NetBIOS name and the first label of the DNS name;
// Windows takes it after a restart [Learn: SetComputerNameExW]. Domain and Entra ID membership decide whether CPFS
// may rename at all.
internal static class ComputerRename
{
    private const int ComputerNamePhysicalDnsHostname = 5;
    private const int DsregDeviceJoin = 1;

    public static void Set(string name)
    {
        if (!SetComputerNameExW(ComputerNamePhysicalDnsHostname, name))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetComputerNameEx failed");
        }
    }

    // The domain read must succeed (it throws, and the plan can't offer a rename); the Entra read may fail, and is then
    // null with its HRESULT in the log.
    public static ComputerJoin ReadJoin() => new(PartOfDomain(), TryEntraJoined());

    // Win32_ComputerSystem.PartOfDomain: true for a domain member; false or null otherwise [Learn: Win32_ComputerSystem].
    public static bool PartOfDomain()
    {
        foreach (var cs in StateReader.Query(@"root\cimv2", "SELECT PartOfDomain FROM Win32_ComputerSystem"))
        {
            using (cs)
            {
                return StateReader.Prop(cs, "PartOfDomain") as bool? == true;
            }
        }

        return false;
    }

    // Learn documents only S_OK or "an HRESULT error code", with no code that means "not joined", so every failure is
    // "unknown" (Windows Server 2025 on the CI runner fails here).
    private static bool? TryEntraJoined()
    {
        try
        {
            var hr = NetGetAadJoinInformation(null, out var info);
            if (hr != 0)
            {
                AppLog.Write("plan", "Entra ID join: not known, NetGetAadJoinInformation returned HRESULT 0x" + hr.ToString("X8", CultureInfo.InvariantCulture));
                return null;
            }

            // A null join info means neither a device join nor a work account [Learn: NetGetAadJoinInformation]; joinType
            // is the structure's first field.
            if (info == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return Marshal.ReadInt32(info) == DsregDeviceJoin;
            }
            finally
            {
                NetFreeAadJoinInformation(info);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Write("plan", "Entra ID join: not known (" + ex.GetType().Name + ")");
            return null;
        }
    }

    // Control\ComputerName\ComputerName: the NetBIOS name Windows takes at the next start (logged, not relied on).
    public static string? PendingNetBios()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName");
        return key?.GetValue("ComputerName") as string;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool SetComputerNameExW(int nameType, string buffer);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NetGetAadJoinInformation(string? tenantId, out IntPtr joinInfo);

    [DllImport("netapi32.dll", ExactSpelling = true)]
    private static extern void NetFreeAadJoinInformation(IntPtr joinInfo);
}

// "Restart now" after a rename: InitiateShutdownW with a restart and a planned reconfiguration reason, after the user
// confirmed. Other users' sessions are not forced closed; Windows refuses then and the app says so.
internal static class Restart
{
    private const uint ShutdownRestart = 0x4;
    private const uint ReasonPlannedReconfig = 0x80020004;

    public static void Now()
    {
        // The CI runner must never restart.
        if (string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CPFS doesn't restart a CI runner.");
        }

        using var privileges = Privileges.Enable("SeShutdownPrivilege");
        var error = InitiateShutdownW(null, null, 0, ShutdownRestart, ReasonPlannedReconfig);
        if (error != 0)
        {
            throw new Win32Exception((int)error);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint InitiateShutdownW(string? machineName, string? message, uint gracePeriod, uint flags, uint reason);
}
