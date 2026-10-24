using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Cpfs.Core.Apply;
using Cpfs.Core.Elevation;
using Microsoft.Win32;

namespace Cpfs.Windows.Apply;

// The drive letter, its name in Explorer, the desktop shortcut and the temporary CPFS-RunAsUser task. Like Writes.cs,
// called only by the executor's steps (SetupSteps, UninstallSteps) after the user pressed Apply and confirmed.

// HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\DOS Devices: "X:" = "\??\D:\Folder" (REG_SZ), which Windows
// turns into a drive for every session when it starts, as the scripts did.
internal static class DosDrives
{
    private const string Key = @"SYSTEM\CurrentControlSet\Control\Session Manager\DOS Devices";

    public static string? Read(char letter)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(Key);
        return key?.GetValue(Name(letter), null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public static void Set(char letter, string target)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(Key, writable: true) ?? throw new IOException("Could not open HKLM\\" + Key);
        key.SetValue(Name(letter), target, RegistryValueKind.String);
    }

    public static void Delete(char letter)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(Key, writable: true);
        key?.DeleteValue(Name(letter), throwOnMissingValue: false);
    }

    private static string Name(char letter) => char.ToUpperInvariant(letter) + ":";
}

// HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\<L>\DefaultLabel and \DefaultIcon, each with
// its (Default) value [Learn: "Assign a Custom Icon and Label to a Drive Letter"]. For a drive mapped to a folder, the
// label of the folder's volume wins over DefaultLabel when that volume has one (not documented by Microsoft; reported
// for subst drives), so the drive name may not show.
internal static class DriveIconKeys
{
    private const string Icons = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons";

    public static string? Read(char letter, string subkey)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(Icons + "\\" + Letter(letter) + "\\" + subkey);
        return key?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public static void Set(char letter, string subkey, string value)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.CreateSubKey(Icons + "\\" + Letter(letter) + "\\" + subkey, writable: true)
            ?? throw new IOException("Could not open the DriveIcons key");
        key.SetValue("", value, RegistryValueKind.String);
    }

    // Removes the subkey, and the letter's key only when nothing else is in it (an icon or name set by others stays).
    public static void Delete(char letter, string subkey)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var icons = hklm.OpenSubKey(Icons, writable: true);
        if (icons is null)
        {
            return;
        }

        using (var key = icons.OpenSubKey(Letter(letter), writable: true))
        {
            if (key is null)
            {
                return;
            }

            key.DeleteSubKey(subkey, throwOnMissingSubKey: false);
            if (key.SubKeyCount > 0 || key.ValueCount > 0)
            {
                return;
            }
        }

        icons.DeleteSubKey(Letter(letter), throwOnMissingSubKey: false);
    }

    private static string Letter(char letter) => char.ToUpperInvariant(letter).ToString();
}

internal static class DriveNames
{
    public static string? Read(char letter) => DriveIconKeys.Read(letter, "DefaultLabel");

    public static void Set(char letter, string label) => DriveIconKeys.Set(letter, "DefaultLabel", label);

    public static void Delete(char letter) => DriveIconKeys.Delete(letter, "DefaultLabel");

    // The label of the volume that holds the folder, which File Explorer shows instead of DefaultLabel; null when none.
    public static string? VolumeLabelOf(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(folder);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var label = new DriveInfo(root).VolumeLabel;
            return string.IsNullOrEmpty(label) ? null : label;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

internal static class DriveIcons
{
    public static string? Read(char letter) => DriveIconKeys.Read(letter, "DefaultIcon");

    public static void Set(char letter, string icon)
    {
        DriveIconKeys.Set(letter, "DefaultIcon", icon);
        ShellIcons.Refresh();
    }

    public static void Delete(char letter)
    {
        DriveIconKeys.Delete(letter, "DefaultIcon");
        ShellIcons.Refresh();
    }
}

// The CPFS icon as a file of its own, ProgramData\CPFS\cpfs.ico (SYSTEM and Administrators write, Users read), so the
// drive and the shortcut keep it when the exe moves. It goes with the data folder on Uninstall.
internal static class CpfsIconFile
{
    public static string Path => System.IO.Path.Combine(StateReader.DataFolder, "cpfs.ico");

    public static void Write()
    {
        var bytes = Bytes();
        if (File.Exists(Path) && SameBytes(File.ReadAllBytes(Path), bytes))
        {
            return;
        }

        Directory.CreateDirectory(StateReader.DataFolder);
        File.WriteAllBytes(Path, bytes);
    }

    private static byte[] Bytes()
    {
        using var stream = typeof(CpfsIconFile).Assembly.GetManifestResourceStream("Cpfs.Assets.cpfs.ico")
            ?? throw new IOException("The CPFS icon is missing from the program file.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }
}

// Tells the shell to drop its icon cache, as Microsoft asks after a drive icon changes [Learn: SHChangeNotify,
// SHCNE_ASSOCCHANGED]. Explorer may still show the old icon until it restarts.
internal static class ShellIcons
{
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    public static void Refresh() => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}

// A .lnk on the Public Desktop through the shell's own ShellLink object (IShellLinkW and IPersistFile).
internal static class Shortcuts
{
    // Creates a new file; refuses when one is already there, so nothing is ever overwritten.
    public static void Create(string file, string target, string description, string icon) => OnSta(() =>
    {
        if (File.Exists(file))
        {
            throw new IOException("A file named " + Path.GetFileName(file) + " is already on the Public Desktop; it is left alone.");
        }

        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetDescription(description);
            link.SetIconLocation(icon, 0);
            ((IPersistFile)link).Save(file, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }

        return true;
    });

    // Points a shortcut CPFS made at another folder, in place.
    public static void Retarget(string file, string target, string description) => OnSta(() =>
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(file, StgmReadWrite);
            link.SetPath(target);
            link.SetDescription(description);
            ((IPersistFile)link).Save(file, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }

        return true;
    });

    // Gives a shortcut CPFS made the CPFS icon, in place.
    public static void SetIcon(string file, string icon) => OnSta(() =>
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(file, StgmReadWrite);
            link.SetIconLocation(icon, 0);
            ((IPersistFile)link).Save(file, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }

        return true;
    });

    // The icon file a shortcut names ("" for none); null when the file is not a shortcut the shell can read.
    public static string? IconOf(string file) => OnSta(() => File.Exists(file) ? ReadIcon(file) : null);

    // The icon file of every shortcut in a folder, by file name, on one thread; unreadable ones are left out.
    public static System.Collections.Generic.Dictionary<string, string> IconsOf(string folder) => OnSta(() =>
    {
        var icons = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return icons;
        }

        foreach (var file in Directory.GetFiles(folder, "*.lnk"))
        {
            if (ReadIcon(file) is { } icon)
            {
                icons[Path.GetFileName(file)] = icon;
            }
        }

        return icons;
    });

    private static string? ReadIcon(string file)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(file, StgmRead);
            var path = new StringBuilder(1024);
            link.GetIconLocation(path, path.Capacity, out _);
            return path.ToString();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    // The folder a shortcut points to; null when the file is not a shortcut the shell can read.
    public static string? TargetOf(string file) => OnSta(() =>
    {
        if (!File.Exists(file))
        {
            return null;
        }

        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(file, StgmRead);
            var path = new StringBuilder(1024);
            link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            return path.ToString();
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    });

    public static void Delete(string file) => File.Delete(file);

    private const int StgmRead = 0;
    private const int StgmReadWrite = 2;

    // The shell's objects are used from a thread of their own in a single-threaded apartment, as the shell expects;
    // the executor's worker thread is not one.
    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new IOException(error.Message, error);
        }

        return result;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    // The method order is the vtable order of IShellLinkW in shobjidl_core.h.
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);

        void GetIDList(out IntPtr idList);

        void SetIDList(IntPtr idList);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int size, out int icon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);

        void Resolve(IntPtr hwnd, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}

// Maps or unmaps the drive in the desktop user's own session, which an elevated process can't reach: the app's own
// "--user-session" verb, run as that user (interactive token, limited) by the temporary CPFS-RunAsUser task. The
// registry mapping is the permanent part; when this fails, the change shows after the next restart instead.
internal static class SessionDrive
{
    public const string NoDesktopUser = "No signed-in desktop user was found (no explorer.exe in this session)";

    private const string MapAfter = "the drive appears after a restart";
    private const string UnmapAfter = "the drive goes at the next restart";

    // No session to reach is not a refusal by Windows: the resource key of what the step shows instead, or null.
    public static string? NoDesktopUserText(string? error) =>
        error is null || !error.StartsWith(NoDesktopUser, StringComparison.Ordinal) ? null
        : error.EndsWith(MapAfter + ".", StringComparison.Ordinal) ? "Apply_NoSessionMap" : "Apply_NoSessionUnmap";

    public static void Map(char letter, string path) => Run(UserSessionAction.MapDrive, letter, path);

    public static void Unmap(char letter, string path) => Run(UserSessionAction.UnmapDrive, letter, path);

    private static void Run(UserSessionAction action, char letter, string path)
    {
        var after = action == UserSessionAction.MapDrive ? MapAfter : UnmapAfter;

        // The elevated instance's own finding (explorer's token), never a value from the command line.
        var sid = DesktopUser.Current.Sid ?? throw new StepUnsupportedException(NoDesktopUser + "; " + after + ".");
        string account;
        try
        {
            account = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException ex)
        {
            throw new StepUnsupportedException("The desktop user's account name could not be found; " + after + ".", ex);
        }

        var arguments = UserSessionVerb.Arguments(action, letter, path);
        int code;
        try
        {
            code = RunAsUser.Run(account, typeof(SessionDrive).Assembly.Location, arguments);
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
            throw new StepUnsupportedException("The task that changes the drive in your session failed (" + inner.Message + "); " + after + ".", inner);
        }

        var name = Enum.IsDefined(typeof(UserSessionResult), code) ? ((UserSessionResult)code).ToString() : "0x" + code.ToString("X8", CultureInfo.InvariantCulture);
        AppLog.Write("apply", "User session " + (action == UserSessionAction.MapDrive ? "map" : "unmap") + " " + letter + ": " + name);
        if (!UserSessionVerb.IsSuccess(code, action))
        {
            throw new StepUnsupportedException("The drive could not be changed in your session (" + name + "); " + after + ".");
        }
    }
}

// The temporary CPFS-RunAsUser task through the Task Scheduler 2.0 COM API: registered for the desktop user with an
// interactive token at the limited run level (no password: it runs only in that user's existing session), started,
// waited for, and always deleted again. It replaces any stale task of that name, which only CPFS creates.
internal static class RunAsUser
{
    private const int LogonInteractiveToken = 3;
    private const int RunLevelLua = 0;
    private const int CreateOrUpdate = 6;
    private const int ActionExec = 0;
    private const int StateQueued = 2;
    private const int StateRunning = 4;

    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(30);

    // Returns the task's last result: the exit code of the program it ran.
    public static int Run(string account, string exe, string arguments)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        var service = Activator.CreateInstance(type)!;
        object? folder = null;
        object? definition = null;
        object? registered = null;
        var name = Core.Planning.Planner.RunAsUserTask;
        try
        {
            Invoke(service, "Connect");
            folder = Invoke(service, "GetFolder", @"\")!;
            definition = Invoke(service, "NewTask", 0)!;

            var principal = Get(definition, "Principal")!;
            Set(principal, "UserId", account);
            Set(principal, "LogonType", LogonInteractiveToken);
            Set(principal, "RunLevel", RunLevelLua);
            Release(principal);

            var settings = Get(definition, "Settings")!;
            Set(settings, "DisallowStartIfOnBatteries", false);
            Set(settings, "StopIfGoingOnBatteries", false);
            Set(settings, "AllowDemandStart", true);
            Set(settings, "ExecutionTimeLimit", "PT1M");
            Set(settings, "Hidden", true);
            Release(settings);

            var actions = Get(definition, "Actions")!;
            var action = Invoke(actions, "Create", ActionExec)!;
            Set(action, "Path", exe);
            Set(action, "Arguments", arguments);
            Release(action);
            Release(actions);

            registered = Invoke(folder, "RegisterTaskDefinition", name, definition, CreateOrUpdate, account, null, LogonInteractiveToken, null)!;
            var before = (DateTime)Get(registered, "LastRunTime")!;
            var running = Invoke(registered, "Run", (object?)null);
            Release(running);

            var watch = Stopwatch.StartNew();
            var started = false;
            while (true)
            {
                Thread.Sleep(100);
                var state = Convert.ToInt32(Get(registered, "State"), CultureInfo.InvariantCulture);
                var busy = state is StateRunning or StateQueued;
                started |= busy || (DateTime)Get(registered, "LastRunTime")! != before;
                if (started && !busy)
                {
                    break;
                }

                if ((!started && watch.Elapsed > StartLimit) || watch.Elapsed > RunLimit)
                {
                    throw new StepUnsupportedException(started
                        ? "The task in your session did not finish in time."
                        : "The task did not start in your session (is the desktop user signed in?).");
                }
            }

            return Convert.ToInt32(Get(registered, "LastTaskResult"), CultureInfo.InvariantCulture);
        }
        finally
        {
            if (folder is not null)
            {
                try
                {
                    Invoke(folder, "DeleteTask", name, 0);
                }
                catch (TargetInvocationException ex) when (ex.InnerException is COMException or FileNotFoundException)
                {
                    AppLog.Write("apply", name + " could not be deleted: " + ex.InnerException.Message);
                }
            }

            Release(registered);
            Release(definition);
            Release(folder);
            Release(service);
        }
    }

    // A leftover task of exactly this name in the root folder, and only there.
    public static bool StaleExists() => PlanReader.Tasks()[Core.Planning.Planner.RunAsUserTask];

    public static void DeleteStale()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        var service = Activator.CreateInstance(type)!;
        object? folder = null;
        try
        {
            Invoke(service, "Connect");
            folder = Invoke(service, "GetFolder", @"")!;
            Invoke(folder, "DeleteTask", Core.Planning.Planner.RunAsUserTask, 0);
        }
        finally
        {
            Release(folder);
            Release(service);
        }
    }

    private static object? Invoke(object target, string method, params object?[] args) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    private static object? Get(object target, string property) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);

    private static void Set(object target, string property, object value) =>
        target.GetType().InvokeMember(property, BindingFlags.SetProperty, null, target, new[] { value }, CultureInfo.InvariantCulture);

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.ReleaseComObject(com);
        }
    }
}
