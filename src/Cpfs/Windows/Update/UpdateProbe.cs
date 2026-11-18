using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cpfs.Core.Updates;

namespace Cpfs.Windows.Update;

// "Cpfs.exe --update-probe X.Y.Z", run by the previous version on the staged file before anything is replaced: it
// loads every embedded library, one WPF and one WPF-UI type, and answers whether it is version X.Y.Z. No log, no
// window. release.yml runs it on every release build, so no release ships without it.
internal static class UpdateProbe
{
    public static int Run(string expected)
    {
        try
        {
#if CPFS_UPDATE_TEST
            if (UpdateTestHooks.FailProbe)
            {
                return UpdateRules.ProbeFailed;
            }
#endif
            foreach (var file in Program.EmbeddedLibraries())
            {
                var name = file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - 4) : file;
                Assembly.Load(new AssemblyName(name));
            }

            // FluentWindow is WPF-UI's and derives from WPF's Window, so both libraries load and initialise.
            RuntimeHelpers.RunClassConstructor(typeof(Wpf.Ui.Controls.FluentWindow).TypeHandle);
            var own = (Attribute.GetCustomAttribute(typeof(UpdateProbe).Assembly, typeof(AssemblyInformationalVersionAttribute))
                as AssemblyInformationalVersionAttribute)?.InformationalVersion;
            return UpdateRules.ProbeCode(own, expected);
        }
        catch (Exception)
        {
            return UpdateRules.ProbeFailed;
        }
    }
}
