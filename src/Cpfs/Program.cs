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

        AppLog.Start();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Write("unhandled", "AppDomain, terminating " + e.IsTerminating + ": " + (e.ExceptionObject?.ToString() ?? "null"));
        return Run(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        if (Array.IndexOf(args, SelfTest.Verb) >= 0)
        {
            return SelfTest.Run();
        }

        if (Array.IndexOf(args, CiApplyTest.Verb) >= 0)
        {
            return CiApplyTest.Run();
        }

        Startup.Init(args);

        // --selftest and --ci-apply-test returned above: headless runs never wait for or block on another window.
        using var instance = Windows.SingleInstance.Acquire(Startup.IsHandOver, out var outcome);
        if (outcome != Windows.InstanceOutcome.First)
        {
            App.ShowSecondStartMessage(outcome);
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        app.LogButtonStyle();
        instance?.Listen(() => app.Dispatcher.BeginInvoke(new Action(App.BringToFront)));
        return app.Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunUserSession(string[] args) => UserSession.Run(args);

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
