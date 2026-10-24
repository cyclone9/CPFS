using System;
using System.Collections.Generic;
using System.IO;
using Cpfs.Core.Config;
using Cpfs.Core.Drives;

namespace Cpfs.Core.Planning;

// What the password box holds, never the password itself.
public enum PasswordEntry
{
    Empty,
    Valid,
    TooShort,
}

// The Setup form.
public sealed record SetupAnswers
{
    public string SharePath { get; init; } = "";

    public string ShareName { get; init; } = "";

    public string SmbUser { get; init; } = "";

    public bool Strict { get; init; }

    public bool AutoPrivate { get; init; }

    public bool Drive { get; init; }

    public char? DriveLetter { get; init; }

    // The name is used by the drive letter and by the File Explorer navigation-pane entry.
    public string DriveName { get; init; } = "";

    // CPFS's entry in File Explorer's navigation pane, with DriveName as its name.
    public bool ExplorerEntry { get; init; }

    public bool Shortcut { get; init; }

    public string ShortcutName { get; init; } = "";

    // The name devices find this PC by; empty keeps the current (or pending) name.
    public string ComputerName { get; init; } = "";

    public PasswordEntry Password { get; init; }

    // The user's answers to "Ask" items, by item key. An item without an answer takes its default.
    public IReadOnlyDictionary<string, bool> Choices { get; init; } = new Dictionary<string, bool>();

    public bool IsTicked(string key, bool defaultValue = false) =>
        Choices.TryGetValue(key, out var ticked) ? ticked : defaultValue;
}

// The form's starting values: the scripts' defaults, replaced by what an existing install saved.
public static class SetupDefaults
{
    public const string SmbUser = "CyClone";
    public const string SharePath = @"D:\Server\Shared-Server";
    public const string ShareName = "Shared-Server";

    // Quick access: bit 1 drive, bit 2 shortcut.
    public const int Access = 3;

    public static string DriveNameFor(string shareName) => "CPFS - " + shareName;

    public static string ShortcutNameFor(string shareName) => shareName + " (CPFS)";

    public static SetupAnswers From(ConfigDocument? config, LetterScan letters)
    {
        var shareName = config?.ShareName ?? ShareName;
        var access = config is null ? Access : (int)(config.Access ?? Access);
        var oldLetter = config?.DriveLetter is { Length: 1 } l ? l[0] : (char?)null;
        return new SetupAnswers
        {
            SharePath = config?.SharePath ?? SharePath,
            ShareName = shareName,
            SmbUser = config?.SmbUser ?? SmbUser,
            Strict = config?.Strict ?? false,
            AutoPrivate = config?.AutoPrivate ?? false,
            Drive = (access & 1) != 0,
            DriveLetter = letters.DefaultLetter(oldLetter),
            DriveName = config?.DriveLabel is { Length: > 0 } label ? label
                : config?.TrackedExplorerEntry?.Name is { Length: > 0 } entryName ? entryName
                : DriveNameFor(shareName),
            ExplorerEntry = config?.ExplorerEntry ?? true,
            Shortcut = (access & 2) != 0,
            ShortcutName = config?.Shortcut is { Length: > 0 } lnk ? Path.GetFileNameWithoutExtension(lnk.Replace('\\', '/')) : ShortcutNameFor(shareName),
        };
    }

    // A drive or shortcut name that was the default for the old share name follows a new share name, as in the
    // 0.2.3 script; a name the user chose stays.
    public static SetupAnswers FollowShareName(SetupAnswers answers, string oldShareName, string newShareName)
    {
        var result = answers with { ShareName = newShareName };
        if (string.Equals(answers.DriveName, DriveNameFor(oldShareName), StringComparison.OrdinalIgnoreCase))
        {
            result = result with { DriveName = DriveNameFor(newShareName) };
        }

        if (string.Equals(answers.ShortcutName, ShortcutNameFor(oldShareName), StringComparison.OrdinalIgnoreCase))
        {
            result = result with { ShortcutName = ShortcutNameFor(newShareName) };
        }

        return result;
    }
}
