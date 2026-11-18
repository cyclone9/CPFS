using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Cpfs;

// Main must not touch a WPF, WPF-UI or Cpfs.Core type: they live in embedded DLLs, and Main is JIT-compiled before
// the resolver below exists.
internal static class Program
{
    internal static readonly Stopwatch SinceMain = Stopwatch.StartNew();

    private const string Prefix = "cpfs.lib.";
    private static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    [STAThread]
    private static int Main(string[] args)
    {
        // The resolver comes first: the log's first line formats the version with Cpfs.Core.
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

        // Run by the CPFS-RunAsUser task in the user's session: no log, no window, only its exit code.
        if (args.Length > 0 && string.Equals(args[0], "--user-session", StringComparison.Ordinal))
        {
            return RunUserSession(args);
        }

        // Run by the CPFS-AutoPrivate task as SYSTEM: no window, no log in a profile, only its own run log.
        if (args.Length > 0 && string.Equals(args[0], "--auto-private", StringComparison.Ordinal))
        {
            return RunAutoPrivate();
        }

        // Run by the previous version on a staged update (updater design, step 7): no log, no window, only its code.
        if (args.Length == 2 && string.Equals(args[0], Core.Updates.UpdateRules.ProbeVerb, StringComparison.Ordinal))
        {
            return RunUpdateProbe(args[1]);
        }

        // The tray icon (design 2.2): matched exactly, and kept apart from the window so no WPF type is loaded for it.
        if (args.Length == 1 && string.Equals(args[0], Windows.TrayControl.Verb, StringComparison.Ordinal))
        {
            return RunTray();
        }

        AppLog.Start();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Write("unhandled", "AppDomain, terminating " + e.IsTerminating + ": " + (e.ExceptionObject?.ToString() ?? "null"));
        return Run(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        Core.Text.TextKey.English = Loc.Instance.EnglishText;
        if (Array.IndexOf(args, SelfTest.Verb) >= 0)
        {
            return SelfTest.Run();
        }

        if (Array.IndexOf(args, CiApplyTest.Verb) >= 0)
        {
            return CiApplyTest.Run();
        }

#if CPFS_UPDATE_TEST
        if (Array.IndexOf(args, Windows.Update.UpdateTestHooks.CiVerb) >= 0)
        {
            return Windows.Update.UpdateTestHooks.RunCi(args);
        }

        if (Array.IndexOf(args, Windows.Update.UpdateTestHooks.CiRealVerb) >= 0)
        {
            return Windows.Update.UpdateTestHooks.RunReal();
        }

        args = Windows.Update.UpdateTestHooks.TakeSource(args);
#endif
        Startup.Init(args);
#if CPFS_UPDATE_TEST
        if (Windows.Update.UpdateTestHooks.Headless && (Startup.AfterUpdateFrom is not null || Startup.UpdateFailedVersion is not null))
        {
            return Windows.Update.UpdateTestHooks.HeadlessStart();
        }
#endif

        // --selftest and --ci-apply-test returned above: headless runs never wait for or block on another window. A start
        // by the updater waits for the previous version's window like a hand-over.
        using var instance = Windows.SingleInstance.Acquire(Startup.IsHandOver || Startup.IsUpdateRestart, Startup.IsReopen, out var outcome);
        if (outcome != Windows.InstanceOutcome.First)
        {
            App.ShowSecondStartMessage(outcome);
            return 0;
        }

        Instance = instance;

        // Files a previous update left beside this exe (updater design, step 11); never from an elevated window.
        if (!Startup.IsElevated)
        {
            System.Threading.Tasks.Task.Run(() => Windows.Update.UpdateEngine.CleanLeftoversAtStart(Startup.AfterUpdateFrom is not null));
        }

        // So the icon comes back after the user chose Exit in its menu. Never from an elevated window (design B2), and
        // not after Uninstall: only while CPFS is set up here.
        if (Windows.TrayControl.AutoStartAllowed())
        {
            Windows.TrayControl.StartIfMissing("window start", TimeSpan.Zero);
        }

        var app = new App();
        app.InitializeComponent();
        app.LogButtonStyle();
        instance?.Listen(() => app.Dispatcher.BeginInvoke(new Action(App.BringToFront)));
        return app.Run();
    }

    // The window's single-instance guard, released by the updater just before it starts the new version.
    internal static Windows.SingleInstance? Instance { get; private set; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunUpdateProbe(string expected) => Windows.Update.UpdateProbe.Run(expected);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunUserSession(string[] args) => UserSession.Run(args);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunTray()
    {
        Core.Text.TextKey.English = Loc.Instance.EnglishText;
        return TrayVerb.Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunAutoPrivate()
    {
        // Its run log keeps CPFS's own sentences in English, as the app's log does.
        Core.Text.TextKey.English = Loc.Instance.EnglishText;
        return AutoPrivateVerb.Run();
    }

    // Matches by simple name and ignores the requested version, which stands in for binding redirects.
    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name);
        if (name.Name is null || name.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        lock (Gate)
        {
            if (Loaded.TryGetValue(name.Name, out var cached))
            {
                return cached;
            }

            using var stream = typeof(Program).Assembly.GetManifestResourceStream(Prefix + name.Name + ".dll");
            if (stream is null)
            {
                return null;
            }

            var bytes = new byte[stream.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = stream.Read(bytes, read, bytes.Length - read);
                if (n == 0)
                {
                    throw new EndOfStreamException(name.Name);
                }

                read += n;
            }

            var assembly = Assembly.Load(bytes);
            Loaded[name.Name] = assembly;
            return assembly;
        }
    }

    internal static IReadOnlyCollection<string> EmbeddedLibraries()
    {
        var list = new List<string>();
        foreach (var r in typeof(Program).Assembly.GetManifestResourceNames())
        {
            if (r.StartsWith(Prefix, StringComparison.Ordinal))
            {
                list.Add(r.Substring(Prefix.Length));
            }
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    internal static IReadOnlyCollection<string> LoadedFromBytes()
    {
        lock (Gate)
        {
            var list = new List<string>(Loaded.Keys);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }
}
