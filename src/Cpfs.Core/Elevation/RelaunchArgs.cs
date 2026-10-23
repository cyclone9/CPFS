using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Cpfs.Core.Elevation;

// The hand-over from the unelevated instance to the one started with "runas". Only non-secret, format-checked values
// travel. The elevated instance takes the letters (which can only add taken letters) and the UI preferences as given;
// the desktop user's SID and Desktop path it derives itself and only compares with what was passed.
public sealed record RelaunchRequest
{
    public string Page { get; init; } = Pages.Setup;

    public string? DesktopSid { get; init; }

    public string? DesktopPath { get; init; }

    public string Letters { get; init; } = "";

    public string? Theme { get; init; }

    public string? Language { get; init; }

    public string? Font { get; init; }
}

public static class Pages
{
    public const string Setup = "Setup";
    public const string Monitor = "Monitor";
    public const string Uninstall = "Uninstall";

    public static readonly IReadOnlyList<string> Elevated = new[] { Setup, Monitor, Uninstall };
}

public sealed class RelaunchArguments
{
    public bool Elevated { get; internal set; }

    public string? Page { get; internal set; }

    public string? DesktopSid { get; internal set; }

    public string? DesktopPath { get; internal set; }

    // Letters seen in use in the user's own session, upper case, de-duplicated, sorted.
    public string Letters { get; internal set; } = "";

    public bool LettersPassed { get; internal set; }

    public string? Theme { get; internal set; }

    public string? Language { get; internal set; }

    public string? Font { get; internal set; }

    // Values that failed their format check; the value itself is dropped.
    public IList<string> Rejected { get; } = new List<string>();

    // A malformed identity value means the command line was not written by CPFS: the hand-over is refused.
    public bool IdentityRejected => Rejected.Contains(RelaunchArgs.DesktopSidOption) || Rejected.Contains(RelaunchArgs.DesktopOption);
}

public static class RelaunchArgs
{
    public const string ElevatedOption = "--elevated";
    public const string PageOption = "--page";
    public const string DesktopSidOption = "--desktop-sid";
    public const string DesktopOption = "--desktop";
    public const string LettersOption = "--letters";
    public const string ThemeOption = "--theme";
    public const string LanguageOption = "--lang";
    public const string FontOption = "--font";

    private const int MaxPathLength = 260;

    private static readonly char[] QuoteTriggers = { ' ', '\t', '\n', '\v', '"' };
    private static readonly char[] BadPathChars = { '"', '<', '>', '|', '*', '?', '\0' };

    private static readonly Regex SidPattern = new(@"^S-1-[0-9]{1,13}(-[0-9]{1,10}){1,15}$", RegexOptions.CultureInvariant);
    private static readonly Regex LettersPattern = new("^[A-Z]{0,26}$", RegexOptions.CultureInvariant);
    private static readonly Regex NamePattern = new("^[A-Za-z][A-Za-z0-9]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex LanguagePattern = new("^[a-z]{2}$", RegexOptions.CultureInvariant);
    private static readonly Regex RootedPathPattern = new(@"^[A-Za-z]:\\", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Build(RelaunchRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var args = new List<string> { ElevatedOption, PageOption, request.Page };
        Add(args, DesktopSidOption, request.DesktopSid);
        Add(args, DesktopOption, request.DesktopPath);
        args.Add(LettersOption);
        args.Add(NormalizeLetters(request.Letters));
        Add(args, ThemeOption, request.Theme);
        Add(args, LanguageOption, request.Language);
        Add(args, FontOption, request.Font);
        return args;
    }

    public static RelaunchArguments Parse(IReadOnlyList<string> args)
    {
        var result = new RelaunchArguments();
        if (args is null)
        {
            return result;
        }

        for (var i = 0; i < args.Count; i++)
        {
            var option = args[i];
            if (string.Equals(option, ElevatedOption, StringComparison.Ordinal))
            {
                result.Elevated = true;
                continue;
            }

            if (!IsValueOption(option))
            {
                result.Rejected.Add(option);
                continue;
            }

            if (i + 1 >= args.Count)
            {
                result.Rejected.Add(option);
                break;
            }

            var value = args[++i];
            switch (option)
            {
                case PageOption:
                    result.Page = Accept(result, option, value, v => Pages.Elevated.Contains(v, StringComparer.Ordinal));
                    break;
                case DesktopSidOption:
                    result.DesktopSid = Accept(result, option, value, v => SidPattern.IsMatch(v));
                    break;
                case DesktopOption:
                    result.DesktopPath = Accept(result, option, value, IsRootedPath);
                    break;
                case LettersOption:
                    var letters = Accept(result, option, value, v => LettersPattern.IsMatch(v));
                    result.Letters = letters is null ? "" : NormalizeLetters(letters);
                    result.LettersPassed = letters is not null;
                    break;
                case ThemeOption:
                    result.Theme = Accept(result, option, value, v => NamePattern.IsMatch(v));
                    break;
                case LanguageOption:
                    result.Language = Accept(result, option, value, v => LanguagePattern.IsMatch(v));
                    break;
                case FontOption:
                    result.Font = Accept(result, option, value, v => NamePattern.IsMatch(v));
                    break;
            }
        }

        return result;
    }

    // One command line for ShellExecuteEx, quoted so CommandLineToArgvW (and the CLR's Main(string[])) gives back
    // the same list.
    public static string ToCommandLine(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        foreach (var arg in args ?? Array.Empty<string>())
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            Quote(sb, arg ?? "");
        }

        return sb.ToString();
    }

    public static string NormalizeLetters(string? letters)
    {
        var set = new SortedSet<char>();
        foreach (var c in letters ?? "")
        {
            var u = char.ToUpperInvariant(c);
            if (u is >= 'A' and <= 'Z')
            {
                set.Add(u);
            }
        }

        return new string(set.ToArray());
    }

    private static void Quote(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(QuoteTriggers) < 0)
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(c);
            }

            backslashes = 0;
        }

        // Backslashes before the closing quote are doubled so they don't escape it.
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    private static bool IsValueOption(string option) => option is PageOption or DesktopSidOption or DesktopOption
        or LettersOption or ThemeOption or LanguageOption or FontOption;

    private static bool IsRootedPath(string value) =>
        value.Length <= MaxPathLength && RootedPathPattern.IsMatch(value) && value.IndexOfAny(BadPathChars) < 0;

    private static string? Accept(RelaunchArguments result, string option, string value, Func<string, bool> valid)
    {
        if (valid(value))
        {
            return value;
        }

        result.Rejected.Add(option);
        return null;
    }

    private static void Add(List<string> args, string option, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            args.Add(option);
            args.Add(value!);
        }
    }
}

public enum IdentityCheck
{
    // The elevated instance found the desktop user and it matches what was passed.
    Match,

    // Started elevated by hand, without a hand-over.
    NotPassed,

    // The elevated instance could not find the desktop user itself, so the passed values can't be confirmed.
    Unconfirmed,

    // Passed and derived values differ, or a passed value was malformed: the hand-over is refused.
    Mismatch,
}

public static class Identity
{
    public static IdentityCheck Compare(RelaunchArguments passed, string? derivedSid, string? derivedDesktop)
    {
        if (passed is null)
        {
            throw new ArgumentNullException(nameof(passed));
        }

        if (passed.IdentityRejected)
        {
            return IdentityCheck.Mismatch;
        }

        if (passed.DesktopSid is null && passed.DesktopPath is null)
        {
            return IdentityCheck.NotPassed;
        }

        if (derivedSid is null)
        {
            return IdentityCheck.Unconfirmed;
        }

        if (passed.DesktopSid is not null && !string.Equals(passed.DesktopSid, derivedSid, StringComparison.OrdinalIgnoreCase))
        {
            return IdentityCheck.Mismatch;
        }

        if (passed.DesktopPath is not null)
        {
            if (derivedDesktop is null)
            {
                return IdentityCheck.Unconfirmed;
            }

            if (!string.Equals(passed.DesktopPath.TrimEnd('\\'), derivedDesktop.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return IdentityCheck.Mismatch;
            }
        }

        return IdentityCheck.Match;
    }
}
