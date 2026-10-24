using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using Cpfs.Core.Monitor;

namespace Cpfs.Windows;

internal enum WatchState
{
    NotConfigured,
    ConfigUnreadable,
    Missing,
    Failed,
    Watching,

    // Windows ended the watch (folder removed, its disk gone, access lost).
    Stopped,
}

// Watches the shared folder for Monitor's Recent activity. Only watches: it opens the folder for listing, never a file.
// Events arrive on thread-pool threads and only queue; the page drains the queue on its own timer.
internal sealed class FolderWatcher : IDisposable
{
    // Past this many undrained events the rest are dropped and shown as one "may be missing" row.
    private const int MaxPending = 5000;

    // The documented maximum (also the limit for a folder on the network); non-paged memory, 64 KB is cheap.
    private const int BufferSize = 64 * 1024;

    private readonly object _gate = new();
    private readonly Stopwatch _clock;
    private List<FolderChange> _pending = new();
    private FileSystemWatcher? _watcher;
    private WatchState _state;
    private string _error = "";
    private bool _dropped;
    private bool _disposed;

    private FolderWatcher(WatchState state, string root, Stopwatch clock)
    {
        _state = state;
        Root = root;
        _clock = clock;
    }

    public string Root { get; }

    public WatchState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    // Windows' text for why the watch stopped, for the card; empty otherwise.
    public string Error
    {
        get
        {
            lock (_gate)
            {
                return _error;
            }
        }
    }

    // Short, path-free reason for the log.
    public string LogReason { get; private set; } = "";

    public int Received { get; private set; }

    public int Overflows { get; private set; }

    public bool CanRetry => State is WatchState.Missing or WatchState.Failed or WatchState.Stopped;

    // Reads SharePath from config.json and starts watching it. Runs on the thread pool: the folder can be on a slow disk.
    public static FolderWatcher Open(Stopwatch clock)
    {
        switch (ConfigFile.TryRead(out var config, out _))
        {
            case ConfigState.Missing:
                return new FolderWatcher(WatchState.NotConfigured, "", clock);
            case ConfigState.Unreadable:
                return new FolderWatcher(WatchState.ConfigUnreadable, "", clock);
        }

        var root = config?.SharePath?.Trim() ?? "";
        if (root.Length == 0)
        {
            return new FolderWatcher(WatchState.NotConfigured, "", clock);
        }

        var watcher = new FolderWatcher(WatchState.Missing, root, clock);
        watcher.TryStart();
        return watcher;
    }

    // Starts, or after a stop starts again; thread pool.
    public void TryStart()
    {
        FileSystemWatcher? w = null;
        try
        {
            StopWatcher();
            if (!Directory.Exists(Root))
            {
                SetState(WatchState.Missing, "", "folder not found");
                return;
            }

            w = new FileSystemWatcher(Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = BufferSize,
            };
            w.Created += (_, e) => Enqueue(ActivityKind.Added, e.FullPath, null);
            w.Changed += (_, e) => Enqueue(ActivityKind.Changed, e.FullPath, null);
            w.Deleted += (_, e) => Enqueue(ActivityKind.Deleted, e.FullPath, null);
            w.Renamed += (_, e) => Enqueue(ActivityKind.Renamed, e.FullPath, e.OldFullPath);
            w.Error += OnError;
            w.EnableRaisingEvents = true;

            lock (_gate)
            {
                if (_disposed)
                {
                    w.Dispose();
                    return;
                }

                _watcher = w;
                _state = WatchState.Watching;
                _error = "";
            }

            LogReason = "";
            w = null;
        }

        // .NET Framework reports any failure to open the folder (access denied included) as FileNotFoundException.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or SecurityException)
        {
            SetState(WatchState.Failed, "", Describe(ex));
        }
        finally
        {
            w?.Dispose();
        }
    }

    // The changes since the last call, in order; UI thread.
    public List<FolderChange> Drain()
    {
        lock (_gate)
        {
            var changes = _pending;
            _pending = new List<FolderChange>();
            if (_dropped)
            {
                changes.Add(new FolderChange(ActivityKind.Missed, "", null, _clock.Elapsed, DateTimeOffset.Now));
                _dropped = false;
            }

            return changes;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        StopWatcher();
    }

    private void StopWatcher()
    {
        FileSystemWatcher? w;
        lock (_gate)
        {
            w = _watcher;
            _watcher = null;
        }

        if (w is null)
        {
            return;
        }

        try
        {
            w.EnableRaisingEvents = false;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Already stopped by Windows.
        }

        w.Dispose();
    }

    private void Enqueue(ActivityKind kind, string path, string? oldPath)
    {
        var change = new FolderChange(kind, path, oldPath, _clock.Elapsed, DateTimeOffset.Now);
        lock (_gate)
        {
            Received++;
            if (_pending.Count >= MaxPending)
            {
                _dropped = true;
                return;
            }

            _pending.Add(change);
        }
    }

    // Overflow: the watch goes on. Any other error: .NET Framework has already stopped raising events.
    private void OnError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        lock (_gate)
        {
            if (ex is InternalBufferOverflowException)
            {
                Overflows++;
                _pending.Add(new FolderChange(ActivityKind.Missed, "", null, _clock.Elapsed, DateTimeOffset.Now));
                return;
            }

            _state = WatchState.Stopped;
            _error = ex?.Message.Trim() ?? "";
        }

        LogReason = ex is null ? "unknown error" : Describe(ex);
    }

    private void SetState(WatchState state, string error, string logReason)
    {
        lock (_gate)
        {
            _state = state;
            _error = error;
        }

        LogReason = logReason;
    }

    // The type and code only: exception messages from the file system can carry the folder's path.
    private static string Describe(Exception ex) =>
        ex.GetType().Name + " 0x" + (ex is Win32Exception w ? w.NativeErrorCode : ex.HResult).ToString("X8", CultureInfo.InvariantCulture);
}
