using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Cpfs;

// Plain-text log for the owner to send back. No WPF or Cpfs.Core types: it starts in Main before the resolver runs.
// Never write a password or any other secret into it.
internal static class AppLog
{
    public const string FileName = "cpfs-log.txt";
    private const string OldFileName = "cpfs-log.old.txt";
    private const long RotateAt = 4 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Encoding Utf8Bom = new UTF8Encoding(true);

    // This run's lines, so Copy report works even when the file can't be written.
    private static readonly StringBuilder Session = new();
    private const int SessionLimit = 2 * 1024 * 1024;

    public static string? FilePath { get; private set; }

    public static string? StartError { get; private set; }

    public static long FailedWrites { get; private set; }

    public static string? LastWriteError { get; private set; }

    // %LOCALAPPDATA%\CPFS, or beside the exe when that fails. Appends rather than truncates, so the
    // elevated instance can't wipe what the unelevated one wrote.
    public static void Start()
    {
        string? firstError = null;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local) || !TryCreate(Path.Combine(local, "CPFS"), ref firstError))
        {
            TryCreate(AppDomain.CurrentDomain.BaseDirectory, ref firstError);
        }

        StartError = firstError;

        Write("start", "==== CPFS " + typeof(AppLog).Assembly.GetName().Version + ", log " + (FilePath ?? "(no file)")
            + ", process " + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
        Write("start", "Local time " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        Write("start", "Exe " + typeof(AppLog).Assembly.Location);
        Write("start", "Command line " + Environment.CommandLine);
        if (firstError is not null)
        {
            Write("start", "Log not in the first place tried: " + firstError);
        }

        Write("start", "Windows " + WindowsBuild() + "; 64-bit process " + Environment.Is64BitProcess);
        Write("start", ".NET Framework " + FrameworkRelease() + "; CLR " + Environment.Version);
        Write("start", "UI culture " + CultureInfo.CurrentUICulture.Name + ", culture " + CultureInfo.CurrentCulture.Name);
        Write("start", "Elevated " + IsElevated() + "; process user " + ProcessUserSid());
    }

    public static void Write(string category, string message)
    {
        var line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " [" + category + "] " + message + Environment.NewLine;
        lock (Gate)
        {
            if (Session.Length < SessionLimit)
            {
                Session.Append(line);
            }

            if (FilePath is null)
            {
                return;
            }

            // Open, append, flush to disk and close per entry, so a copy taken while the app runs has every line.
            // Share flags let Notepad hold the file open; a short retry covers antivirus or the indexer.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(line);
                    using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    if (attempt < 2 && ex is IOException)
                    {
                        System.Threading.Thread.Sleep(15);
                        continue;
                    }

                    // Logging must never take the app down; About shows the count and the last error.
                    FailedWrites++;
                    LastWriteError = ex.GetType().Name + ": " + ex.Message;
                    return;
                }
            }
        }
    }

    public static void Exception(string category, Exception ex) => Write(category, ex.ToString());

    public static string SessionText()
    {
        lock (Gate)
        {
            return Session.ToString();
        }
    }

    public static long? FileSize()
    {
        try
        {
            return FilePath is null ? null : new FileInfo(FilePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    public static string WindowsBuild()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = key?.GetValue("CurrentBuildNumber") as string ?? "?";
            var ubr = key?.GetValue("UBR") is int u ? u.ToString(CultureInfo.InvariantCulture) : "?";
            var display = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? "?";
            var edition = key?.GetValue("EditionID") as string ?? "?";
            return "build " + build + "." + ubr + " (" + display + ", " + edition + ")";
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return ex.GetType().Name;
        }
    }

    // Release values from Microsoft's "Determine which .NET Framework versions are installed".
    public static string FrameworkRelease()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
            if (key?.GetValue("Release") is not int release)
            {
                return "Release value missing";
            }

            var name = release >= 533320 ? "4.8.1 or later" : release >= 528040 ? "4.8" : "older than 4.8";
            return name + " (Release " + release.ToString(CultureInfo.InvariantCulture) + ")";
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return ex.GetType().Name;
        }
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string ProcessUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? "?";
    }

    private static bool TryCreate(string directory, ref string? firstError)
    {
        var path = Path.Combine(directory, FileName);
        try
        {
            Directory.CreateDirectory(directory);
            Rotate(directory, path);

            // The BOM goes in only when the file is new, so Notepad reads Persian text as UTF-8.
            using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length == 0)
                {
                    var bom = Utf8Bom.GetPreamble();
                    fs.Write(bom, 0, bom.Length);
                    fs.Flush(true);
                }
            }

            FilePath = path;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or ArgumentException)
        {
            firstError ??= path + ": " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    // Keeps the log from growing without end: past the limit the file becomes cpfs-log.old.txt (replacing the older one).
    private static void Rotate(string directory, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < RotateAt)
        {
            return;
        }

        var old = Path.Combine(directory, OldFileName);
        if (File.Exists(old))
        {
            File.Delete(old);
        }

        File.Move(path, old);
    }
}
