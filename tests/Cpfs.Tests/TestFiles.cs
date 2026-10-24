using System;
using System.IO;

namespace Cpfs.Tests;

internal static class TestFiles
{
    public static string ConfigDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "config");

    public static string Config(string name) => Path.Combine(ConfigDir, name);

    public static byte[] ConfigBytes(string name) => File.ReadAllBytes(Config(name));

    // The repository root, found from the test output folder.
    public static string RepoRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Cpfs.sln")))
                {
                    return dir.FullName;
                }
            }

            throw new InvalidOperationException("Cpfs.sln not found above " + AppContext.BaseDirectory);
        }
    }
}
