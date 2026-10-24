using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Cpfs.Core.Planning;
using Cpfs.Windows;

namespace Cpfs;

// "Cpfs.exe --selftest": a headless run for CI. It loads every embedded library through the resolver, the string
// tables, reads the Windows state with the Setup page's readers and builds the plan from the saved answers, then
// writes a report to stdout. Nothing is written to Windows and no window opens. Exit 0 when it ran to the end
// (read failures such as "needs administrator" are reported, not errors), 1 on an unexpected exception.
internal static class SelfTest
{
    public const string Verb = "--selftest";

    public static int Run()
    {
        AttachToParentConsole();
        var output = Console.Out;
        try
        {
            output.WriteLine("CPFS self-test " + AppLog.VersionText());
            output.WriteLine("Windows " + AppLog.WindowsBuild() + ", .NET Framework " + AppLog.FrameworkRelease() + ", elevated " + AppLog.IsElevated());
            LoadLibraries(output);
            Strings(output);
            Startup.Init(Array.Empty<string>());
            var letters = DriveLetters.Scan(null);
            var reader = new StateReader();
            ReadSections(output, reader, letters);
            BuildPlan(output, reader, letters);
            output.WriteLine("Result: ran to the end");
            AppLog.Write("selftest", "Ran to the end");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine("Result: unexpected exception");
            output.WriteLine(ex.ToString());
            AppLog.Exception("selftest", ex);
            return 1;
        }
        finally
        {
            output.Flush();
        }
    }

    private static void LoadLibraries(TextWriter output)
    {
        var names = Program.EmbeddedLibraries();
        foreach (var file in names)
        {
            var name = file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - 4) : file;
            var assembly = Assembly.Load(new AssemblyName(name));
            output.WriteLine("  library " + assembly.GetName().Name + " " + assembly.GetName().Version);
        }

        output.WriteLine("Libraries: " + names.Count.ToString(CultureInfo.InvariantCulture) + " embedded, "
            + Program.LoadedFromBytes().Count.ToString(CultureInfo.InvariantCulture) + " loaded from the exe");
    }

    private static void Strings(TextWriter output)
    {
        foreach (var language in Loc.Languages)
        {
            var culture = CultureInfo.GetCultureInfo(language);
            Loc.Instance.SetCulture(culture);
            var title = Loc.Instance["Setup_Plan_Title"];
            if (title.StartsWith("!", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("String table " + language + " lacks Setup_Plan_Title");
            }

            output.WriteLine("  strings " + language + ": translated " + Loc.Instance.IsTranslated(culture));
        }

        Loc.Instance.SetCulture(CultureInfo.GetCultureInfo("en"));
    }

    private static void ReadSections(TextWriter output, StateReader reader, Cpfs.Core.Drives.LetterScan letters)
    {
        var sections = new StateSection?[StateReader.SectionIds.Count];
        reader.ReadAllAsync(Task.FromResult(letters), (index, section) => sections[index] = section).GetAwaiter().GetResult();
        foreach (var section in sections)
        {
            if (section is null)
            {
                continue;
            }

            var needsAdmin = section.Rows.FindAll(r => r.Status == ReadStatus.NeedsAdmin).Count;
            var failed = section.Rows.FindAll(r => r.Status == ReadStatus.Failed).Count;
            output.WriteLine("  section " + section.Id + ": " + section.Status + ", " + section.Rows.Count.ToString(CultureInfo.InvariantCulture)
                + " rows, " + needsAdmin.ToString(CultureInfo.InvariantCulture) + " need administrator, "
                + failed.ToString(CultureInfo.InvariantCulture) + " failed" + (section.Error is null ? "" : " (" + section.Error + ")"));
        }
    }

    private static void BuildPlan(TextWriter output, StateReader reader, Cpfs.Core.Drives.LetterScan letters)
    {
        var answers = SetupDefaults.From(reader.Config, letters);
        var snapshot = PlanReader.ReadStatic(letters, Startup.IsElevated ? DesktopUser.Current.Sid : null);
        snapshot = PlanReader.ReadAnswerParts(snapshot, answers.SmbUser, answers.SharePath, answers.ShareName);
        var plan = Planner.Setup(snapshot, answers, reader.Config, PlanReader.ProtectedFolders());
        var counts = new List<string>();
        foreach (PlanAction action in Enum.GetValues(typeof(PlanAction)))
        {
            counts.Add(action + " " + plan.Count(action).ToString(CultureInfo.InvariantCulture));
        }

        output.WriteLine("Plan (" + (reader.Config is null ? "no config.json" : "config.json " + reader.Config.Version) + "): " + string.Join(", ", counts));
        foreach (var item in plan.Items)
        {
            if (item.Action == PlanAction.Keep)
            {
                continue;
            }

            output.WriteLine("  " + item.Action + (item.Cause == PlanCause.None ? "" : "/" + item.Cause) + " " + item.Key + ": "
                + item.ReasonKey + (item.ReasonArgs.Count == 0 ? "" : " [" + string.Join("; ", item.ReasonArgs) + "]"));
        }
    }

    // A GUI-subsystem exe gets no console. With stdout redirected (CI) the handle is already there; started from a
    // console without redirection, attaching to the parent's console makes the report visible.
    internal static void AttachToParentConsole()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            _ = AttachConsole(AttachParentProcess);
        }
    }

    private const int StdOutputHandle = -11;
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
