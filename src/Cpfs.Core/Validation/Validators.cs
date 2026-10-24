using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Cpfs.Core.Validation;

public enum ValidationError
{
    None,
    Empty,
    TooLong,
    TooShort,
    InvalidCharacters,
    AllDigits,
    SameAsWindowsUser,
    SameAsComputerName,
    NotFullPath,
    DriveRoot,
    ProtectedFolder,
    ReservedName,
    TrailingDotOrSpace,
    ControlCharacters,
    LetterReserved,
    LetterNotFree,
}

public readonly record struct ValidationResult(ValidationError Error, string? Value = null, string? Suggestion = null)
{
    public bool IsValid => Error == ValidationError.None;

    public static ValidationResult Ok(string value) => new(ValidationError.None, value);
}

// Folders that may never be shared, from the machine the app runs on (the app fills it from the environment).
public sealed class ProtectedFolders
{
    // Refused together with everything under them.
    public IList<string> Trees { get; } = new List<string>();

    // Refused only as the folder itself (a subfolder of a profile or of Public may be shared).
    public IList<string> Exact { get; } = new List<string>();
}

// The scripts' input rules (cpfs-setup.bat), plus the folder refusals and name rules of the app design.
// Comparisons are ordinal: these are Windows identifiers, never compared under the UI culture.
public static class Validators
{
    public const int MinPasswordLength = 8;

    // A label longer than this is cut off in Explorer's drive list. [unverified on Windows; NTFS volume labels allow 32]
    public const int MaxDriveNameLength = 32;

    public const int MaxDeviceNameLength = 64;

    private static readonly Regex ComputerNamePattern = new("^[A-Za-z0-9]([A-Za-z0-9-]{0,13}[A-Za-z0-9])?$", RegexOptions.CultureInvariant);
    private static readonly Regex DigitsOnly = new("^[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly Regex UserNamePattern = new("^[A-Za-z0-9._-]{1,20}$", RegexOptions.CultureInvariant);
    private static readonly Regex ShareNamePattern = new("^[A-Za-z0-9._-]{1,80}$", RegexOptions.CultureInvariant);
    private static readonly Regex FullPathPattern = new(@"^[A-Za-z]:\\", RegexOptions.CultureInvariant);

    private static readonly string[] ReservedFileNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] InvalidPathChars = { '/', '*', '?', '"', '<', '>', '|' };

    private static readonly char[] InvalidFileNameChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    public static ValidationResult ComputerName(string? input)
    {
        var name = (input ?? "").Trim();
        if (name.Length == 0)
        {
            return new(ValidationError.Empty);
        }

        if (name.Length > 15)
        {
            return new(ValidationError.TooLong);
        }

        if (!ComputerNamePattern.IsMatch(name))
        {
            return new(ValidationError.InvalidCharacters);
        }

        return DigitsOnly.IsMatch(name) ? new(ValidationError.AllDigits) : ValidationResult.Ok(name);
    }

    // windowsUsers: the signed-in user's name and the desktop user's name (without domain). A clash suggests
    // "<name>-SMB", as the script does.
    public static ValidationResult SmbUserName(string? input, IEnumerable<string?> windowsUsers, string? computerName)
    {
        var name = (input ?? "").Trim();
        if (name.Length == 0)
        {
            return new(ValidationError.Empty);
        }

        if (!UserNamePattern.IsMatch(name))
        {
            return new(name.Length > 20 ? ValidationError.TooLong : ValidationError.InvalidCharacters);
        }

        foreach (var user in windowsUsers ?? Array.Empty<string?>())
        {
            if (user is not null && string.Equals(StripDomain(user), name, StringComparison.OrdinalIgnoreCase))
            {
                var suggestion = name.Length + 4 <= 20 ? name + "-SMB" : null;
                return new(ValidationError.SameAsWindowsUser, null, suggestion);
            }
        }

        if (computerName is not null && string.Equals(computerName.Trim(), name, StringComparison.OrdinalIgnoreCase))
        {
            return new(ValidationError.SameAsComputerName);
        }

        return ValidationResult.Ok(name);
    }

    public static ValidationResult ShareName(string? input)
    {
        var name = (input ?? "").Trim();
        if (name.Length == 0)
        {
            return new(ValidationError.Empty);
        }

        if (!ShareNamePattern.IsMatch(name))
        {
            return new(name.Length > 80 ? ValidationError.TooLong : ValidationError.InvalidCharacters);
        }

        return ValidationResult.Ok(name);
    }

    // A full local path. The trailing backslash is trimmed (except on a root), as the script does; roots and
    // protected folders are refused.
    public static ValidationResult SharePath(string? input, ProtectedFolders protectedFolders)
    {
        var path = (input ?? "").Trim();
        if (path.Length == 0)
        {
            return new(ValidationError.Empty);
        }

        if (!FullPathPattern.IsMatch(path))
        {
            return new(ValidationError.NotFullPath);
        }

        if (path.IndexOfAny(InvalidPathChars) >= 0 || path.IndexOf(':', 2) >= 0 || HasControl(path))
        {
            return new(ValidationError.InvalidCharacters);
        }

        path = path.TrimEnd('\\');
        if (path.Length <= 2)
        {
            return new(ValidationError.DriveRoot);
        }

        foreach (var tree in protectedFolders?.Trees ?? Array.Empty<string>())
        {
            var t = Normalize(tree);
            if (t.Length > 0 && (string.Equals(path, t, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(t + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                return new(ValidationError.ProtectedFolder);
            }
        }

        foreach (var exact in protectedFolders?.Exact ?? Array.Empty<string>())
        {
            if (string.Equals(path, Normalize(exact), StringComparison.OrdinalIgnoreCase))
            {
                return new(ValidationError.ProtectedFolder);
            }
        }

        return ValidationResult.Ok(path);
    }

    // The password itself never enters Core: the page passes only its length.
    public static ValidationError Password(int length)
    {
        if (length == 0)
        {
            return ValidationError.Empty;
        }

        return length < MinPasswordLength ? ValidationError.TooShort : ValidationError.None;
    }

    public static ValidationResult DriveName(string? input)
    {
        var name = (input ?? "").Trim();
        if (name.Length == 0)
        {
            return new(ValidationError.Empty);
        }

        if (HasControl(name))
        {
            return new(ValidationError.ControlCharacters);
        }

        return name.Length > MaxDriveNameLength ? new(ValidationError.TooLong) : ValidationResult.Ok(name);
    }

    // A name the owner gives a connected device in the Monitor; it only lives in the app's own devices.json.
    public static ValidationResult DeviceName(string? input)
    {
        var name = (input ?? "").Trim();
        if (name.Length == 0)
        {
            return new(ValidationError.Empty);
        }

        if (HasControl(name))
        {
            return new(ValidationError.ControlCharacters);
        }

        return name.Length > MaxDeviceNameLength ? new(ValidationError.TooLong) : ValidationResult.Ok(name);
    }

    // The shortcut's file name without ".lnk".
    public static ValidationResult ShortcutName(string? input)
    {
        var name = (input ?? "").Trim();
        if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(0, name.Length - 4).TrimEnd();
        }

        if (name.Length == 0)
        {
            return new(ValidationError.Empty);
        }

        if (HasControl(name))
        {
            return new(ValidationError.ControlCharacters);
        }

        if (name.IndexOfAny(InvalidFileNameChars) >= 0)
        {
            return new(ValidationError.InvalidCharacters);
        }

        if (name.EndsWith(".", StringComparison.Ordinal))
        {
            return new(ValidationError.TrailingDotOrSpace);
        }

        // "NUL.txt" is reserved too: Windows checks the part before the first dot.
        var stem = name.Split('.')[0].TrimEnd();
        foreach (var reserved in ReservedFileNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return new(ValidationError.ReservedName);
            }
        }

        // Path length: the Public Desktop path plus the name must stay under MAX_PATH; 200 leaves room for it.
        return name.Length > 200 ? new(ValidationError.TooLong) : ValidationResult.Ok(name);
    }

    public static ValidationResult DriveLetter(string? input, IReadOnlyCollection<char> free)
    {
        var text = (input ?? "").Trim().TrimEnd('\\').TrimEnd(':');
        if (text.Length == 0)
        {
            return new(ValidationError.Empty);
        }

        if (text.Length != 1 || !IsAsciiLetter(text[0]))
        {
            return new(ValidationError.InvalidCharacters);
        }

        var letter = char.ToUpperInvariant(text[0]);
        if (letter is 'A' or 'B')
        {
            return new(ValidationError.LetterReserved);
        }

        foreach (var f in free ?? Array.Empty<char>())
        {
            if (char.ToUpperInvariant(f) == letter)
            {
                return ValidationResult.Ok(letter.ToString());
            }
        }

        return new(ValidationError.LetterNotFree);
    }

    private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool HasControl(string s)
    {
        foreach (var c in s)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripDomain(string user)
    {
        var i = user.LastIndexOf('\\');
        return (i >= 0 ? user.Substring(i + 1) : user).Trim();
    }

    private static string Normalize(string path) => (path ?? "").Trim().TrimEnd('\\');
}
