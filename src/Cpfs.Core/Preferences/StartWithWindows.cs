using System;
using System.Text;

namespace Cpfs.Core.Preferences;

// A value named CPFS under HKCU\...\Run as found: Text is the data when it is a REG_SZ, null for any other type.
public sealed record RunValue(string? Text);

public enum StartState
{
    // No value named CPFS.
    Off,

    // CPFS's format, starting this exe.
    On,

    // CPFS's format, starting another file that exists.
    OtherCopy,

    // CPFS's format, starting a file that is gone (Windows skips it silently).
    TargetMissing,

    // A value named CPFS that is not CPFS's format: never changed unless the user presses Use this copy.
    Foreign,
}

// Path is the exe the value starts (CPFS's format only); Data is the value as found, for showing a foreign one.
public sealed record StartReading(StartState State, string? Path, string? Data);

public enum CommandRefusal
{
    None,
    NotFullPath,
    Quote,
    Percent,
    TooLong,
}

// Start with Windows: HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value CPFS (REG_SZ), data
// "<full path>" --tray [Learn: Run and RunOnce Registry Keys]. The value is the only state; nothing in
// preferences.json or config.json.
public static class StartWithWindows
{
    public const string ValueName = "CPFS";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Undocumented; read for a hint, never written.
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    // Must match the verb Program.Main checks (dev3).
    public const string TrayVerb = "--tray";

    // Learn: "Command lines ... can be no more than 260 characters".
    public const int MaxCommandLine = 260;

    // Always quoted and never with an environment variable: a REG_SZ that Windows runs exactly as written.
    public static string? Command(string? exe, out CommandRefusal refusal)
    {
        refusal = Refusal(exe);
        if (refusal != CommandRefusal.None)
        {
            return null;
        }

        var line = Format(exe!);
        if (line.Length > MaxCommandLine)
        {
            refusal = CommandRefusal.TooLong;
            return null;
        }

        return line;
    }

    // The exe a value in CPFS's own format starts; null for anything else.
    public static string? TargetOf(string? data)
    {
        const string tail = "\" " + TrayVerb;
        if (data is null || data.Length > MaxCommandLine || data.Length < 2 + tail.Length || data[0] != '"'
            || !data.EndsWith(tail, StringComparison.Ordinal))
        {
            return null;
        }

        var path = data.Substring(1, data.Length - 1 - tail.Length);
        return Refusal(path) == CommandRefusal.None ? path : null;
    }

    public static StartReading Decide(RunValue? value, string thisExe, Func<string, bool> exists)
    {
        if (exists is null)
        {
            throw new ArgumentNullException(nameof(exists));
        }

        if (value is null)
        {
            return new StartReading(StartState.Off, null, null);
        }

        if (TargetOf(value.Text) is not { } path)
        {
            return new StartReading(StartState.Foreign, null, value.Text);
        }

        if (SamePath(path, thisExe))
        {
            return new StartReading(StartState.On, path, value.Text);
        }

        return new StartReading(exists(path) ? StartState.OtherCopy : StartState.TargetMissing, path, value.Text);
    }

    // StartupApproved\Run\CPFS as seen in the wild: binary, first byte 02 enabled, 03 disabled. Undocumented, so only
    // an odd first byte counts as "switched off"; anything else (or no value) gives no hint.
    public static bool DisabledInSettings(byte[]? approved) => approved is { Length: > 0 } && (approved[0] & 1) == 1;

    // For the log: "02 00 00 ...", or "absent".
    public static string Hex(byte[]? bytes)
    {
        if (bytes is null)
        {
            return "absent";
        }

        var text = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return text.Length == 0 ? "empty" : text.ToString();
    }

    private static string Format(string exe) => "\"" + exe + "\" " + TrayVerb;

    private static CommandRefusal Refusal(string? exe)
    {
        if (exe is null || !IsFullPath(exe))
        {
            return CommandRefusal.NotFullPath;
        }

        if (exe.IndexOf('"') >= 0)
        {
            return CommandRefusal.Quote;
        }

        return exe.IndexOf('%') >= 0 ? CommandRefusal.Percent : CommandRefusal.None;
    }

    // C:\... or \\server\share\...; never relative, never a device path.
    private static bool IsFullPath(string p) =>
        (p.Length >= 4 && IsLetter(p[0]) && p[1] == ':' && p[2] == '\\' && p[3] != '\\')
        || (p.Length >= 5 && p[0] == '\\' && p[1] == '\\' && p[2] != '\\' && p[2] != '?' && p[2] != '.' && p.IndexOf('\\', 2) > 2);

    private static bool IsLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
