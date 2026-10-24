using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Cpfs.Core.Planning;
using Microsoft.Win32;

namespace Cpfs.Windows.Apply;

// The CPFS-AutoPrivate task through the Task Scheduler 2.0 COM API, as RunAsUser does: a SYSTEM task in the root
// folder, started 5 s after event 10000 of the NetworkProfile channel, running the exe copy in ProgramData\CPFS with
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

// The copy of this exe the task runs, in the locked ProgramData\CPFS: never the exe the user started, which may sit in a
// folder the user can write to. Written next to its place and moved in, then checked against the running exe's hash.
internal static class AutoPrivateCopy
{
    public static string Path => System.IO.Path.Combine(StateReader.DataFolder, "Cpfs.exe");

    public static string LegacyScript => System.IO.Path.Combine(StateReader.DataFolder, "set-private.ps1");

    public static string Running => typeof(AutoPrivateCopy).Assembly.Location;

    public static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var hash = sha.ComputeHash(stream);
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
        return new AutoPrivateFiles(exists, exists && IsCurrent(), File.Exists(LegacyScript));
    }

    public static void Write()
    {
        var source = Running;
        if (string.Equals(System.IO.Path.GetFullPath(source), System.IO.Path.GetFullPath(Path), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("CPFS runs from its own copy in ProgramData\\CPFS; start the exe you downloaded to update it.");
        }

        var temp = Path + ".tmp";
        File.Copy(source, temp, overwrite: true);
        if (!string.Equals(Sha256Of(temp), Sha256Of(source), StringComparison.Ordinal))
        {
            File.Delete(temp);
            throw new IOException("The copy of CPFS in ProgramData\\CPFS does not match the running program.");
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
    }

    public static void Remove() => Retry(() => File.Delete(Path));

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

    public static ComputerJoin ReadJoin() => new(PartOfDomain(), EntraJoined());

    // Win32_ComputerSystem.PartOfDomain: true for a domain member; false or null otherwise [Learn: Win32_ComputerSystem].
    private static bool PartOfDomain()
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

    // A null join info means neither a device join nor a work account [Learn: NetGetAadJoinInformation]; joinType is
    // the structure's first field.
    private static bool EntraJoined()
    {
        var hr = NetGetAadJoinInformation(null, out var info);
        if (hr != 0)
        {
            throw new Win32Exception(hr, "NetGetAadJoinInformation failed");
        }

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
