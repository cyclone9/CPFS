using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Cpfs.Windows;

internal enum InstanceOutcome
{
    // This is the only CPFS window in the session.
    First,

    // Another window is open and was asked to come to the front.
    Shown,

    // Another window is open, but this start could not reach it (it runs as administrator, as another account).
    OtherIsAdmin,

    // Another window is open and did not answer.
    OtherNoAnswer,

    // "Continue as administrator" started this instance, and the instance that asked for it did not close in time.
    HandOverTimedOut,
}

// One CPFS window per Windows session: a named mutex, and an event a second start sets to bring the open window to
// the front.
//
// The event is the only thing an unelevated process can do to an elevated CPFS, and it is acceptable under the
// elevation model (no channel that can drive the elevated process): it carries no data, the elevated side reads
// nothing from it, and all it does on being set is show and activate its own window. The same event set a thousand
// times does nothing else. Its label is lowered to Medium so the user's unelevated start may set it; its DACL grants
// only that user, and only the right to set it.
internal sealed class SingleInstance : IDisposable
{
    // Named after the product, not the exe path: a renamed or moved copy (Cpfs-0.5.0-dev7.exe) is the same app.
    private const string MutexName = @"Local\CPFS-CyClonePowerFileShare-Instance";
    private const string ShowEventName = @"Local\CPFS-CyClonePowerFileShare-Show";

    private const uint Synchronize = 0x00100000;
    private const uint EventModifyState = 0x0002;
    private const int ErrorAccessDenied = 5;
    private const int ErrorAlreadyExists = 183;
    private const uint SddlRevision1 = 1;
    private const uint AsfwAny = 0xFFFFFFFF;

    private static readonly TimeSpan HandOverWait = TimeSpan.FromSeconds(5);

    private readonly Mutex _mutex;
    private readonly ShowEvent? _event;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, ShowEvent? showEvent)
    {
        _mutex = mutex;
        _event = showEvent;
    }

    // Null unless the outcome is First; a null instance with First means the mutex could not be used at all, and
    // CPFS starts without the single-instance guard rather than not at all.
    public static SingleInstance? Acquire(bool handOver, out InstanceOutcome outcome)
    {
        Mutex? mutex = null;
        var owned = false;
        var denied = false;
        try
        {
            mutex = new Mutex(true, MutexName, out var createdNew);
            owned = createdNew;
            if (!owned && handOver)
            {
                AppLog.Write("instance", "Waiting up to 5 s for the window that asked for administrator rights to close");
                try
                {
                    owned = mutex.WaitOne(HandOverWait);
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // An elevated instance created it: its default DACL keeps an unelevated process out.
            denied = true;
        }
        catch (Exception ex) when (ex is IOException or WaitHandleCannotBeOpenedException)
        {
            AppLog.Write("instance", "Single-instance mutex unavailable, starting without it: " + ex.GetType().Name + ": " + ex.Message);
            mutex?.Dispose();
            outcome = InstanceOutcome.First;
            return null;
        }

        if (owned)
        {
            outcome = InstanceOutcome.First;
            return new SingleInstance(mutex!, ShowEvent.Create());
        }

        mutex?.Dispose();
        if (handOver)
        {
            AppLog.Write("instance", "The window that asked for administrator rights did not close within 5 s; this instance exits");
            outcome = InstanceOutcome.HandOverTimedOut;
            return null;
        }

        outcome = Signal(out var accessDenied) ? InstanceOutcome.Shown : denied || accessDenied ? InstanceOutcome.OtherIsAdmin : InstanceOutcome.OtherNoAnswer;
        AppLog.Write("instance", "Second start while CPFS is open" + (denied ? " (the open one runs as administrator)" : "") + ": " + outcome);
        return null;
    }

    // Calls show on a thread-pool thread each time a second start sets the event.
    public void Listen(Action show)
    {
        if (_event is null || _wait is not null)
        {
            return;
        }

        _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) => show(), null, Timeout.Infinite, false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _event?.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread: nothing to release.
        }

        _mutex.Dispose();
    }

    private static bool Signal(out bool accessDenied)
    {
        accessDenied = false;

        // The open window creates its event just after the mutex; a start in that moment retries for up to 2 s.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var handle = OpenEvent(EventModifyState, false, ShowEventName);
            if (!handle.IsInvalid)
            {
                // This start was launched by the user and may take the foreground; the open window may only bring
                // itself forward if it is allowed to. The permission lapses at the user's next input.
                AllowSetForegroundWindow(AsfwAny);
                return SetEvent(handle);
            }

            if (Marshal.GetLastWin32Error() == ErrorAccessDenied)
            {
                accessDenied = true;
                return false;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEvent(ref SecurityAttributes attributes, bool manualReset, bool initialState, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle OpenEvent(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(SafeWaitHandle handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    // An auto-reset event wrapped as a WaitHandle for RegisterWaitForSingleObject.
    private sealed class ShowEvent : WaitHandle
    {
        private ShowEvent(SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
        }

        public static ShowEvent? Create()
        {
            string? user;
            using (var identity = WindowsIdentity.GetCurrent())
            {
                user = identity.User?.Value;
            }

            if (user is null)
            {
                return null;
            }

            // D:P only this user, only SYNCHRONIZE and EVENT_MODIFY_STATE; S: Medium label, no write up from Low.
            var sddl = "D:P(A;;0x" + (Synchronize | EventModifyState).ToString("X", System.Globalization.CultureInfo.InvariantCulture) + ";;;" + user + ")S:(ML;;NW;;;ME)";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SddlRevision1, out var descriptor, IntPtr.Zero))
            {
                AppLog.Write("instance", "Show event: security descriptor failed, error " + Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture));
                return null;
            }

            try
            {
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = descriptor };
                var handle = CreateEvent(ref attributes, false, false, ShowEventName);
                var error = Marshal.GetLastWin32Error();
                if (handle.IsInvalid)
                {
                    AppLog.Write("instance", "Show event: not created, error " + error.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    handle.Dispose();
                    return null;
                }

                if (error == ErrorAlreadyExists)
                {
                    AppLog.Write("instance", "Show event already existed; using it");
                }

                return new ShowEvent(handle);
            }
            finally
            {
                LocalFree(descriptor);
            }
        }
    }
}
