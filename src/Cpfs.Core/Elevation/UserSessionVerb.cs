using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Cpfs.Core.Config;

namespace Cpfs.Core.Elevation;

public enum UserSessionAction
{
    MapDrive,
    UnmapDrive,
}

// The exit code of "Cpfs.exe --user-session ...", which the CPFS-RunAsUser task reports back as its last result.
// Kept clear of 1 and 2 (any failed process) and of the Task Scheduler's own 0x41300-range codes.
public enum UserSessionResult
{
    Done = 0,

    // Map: the letter already points at the folder in this session.
    AlreadyMapped = 10,

    // Unmap: the letter is not defined in this session.
    NothingToRemove = 11,

    // The letter points somewhere else in this session; left alone.
    PointsElsewhere = 12,

    // Unmap: only the mapping Windows made at start from the DOS Devices value is left; it goes at the next restart.
    BootMapping = 13,

    // The command line was not one the verb accepts.
    Refused = 20,

    // config.json is missing or unreadable.
    NoConfig = 21,

    // The letter or the folder is not what config.json records.
    NotRecorded = 22,

    // DefineDosDevice failed.
    Failed = 30,
}

public sealed record UserSessionRequest(UserSessionAction Action, char Letter, string Path)
{
    public string Device => Letter + ":";
}

// The app's own verb, run as the desktop user (limited token) by the temporary CPFS-RunAsUser task, to map or unmap
// the drive in that user's session, where an elevated process can't reach (UAC keeps the two device maps apart). It
// trusts nothing on its command line: the letter and the folder must be the ones config.json records, which only
// SYSTEM and Administrators can write.
public static class UserSessionVerb
{
    public const string Verb = "--user-session";
    public const string MapDrive = "map-drive";
    public const string UnmapDrive = "unmap-drive";

    private static readonly Regex DevicePattern = new("^[A-Za-z]:$", RegexOptions.CultureInvariant);

    // A local folder: a drive letter, a colon, a backslash and at least one more character.
    private static readonly Regex PathPattern = new(@"^[A-Za-z]:\\[^\x00-\x1F""<>|*?]+$", RegexOptions.CultureInvariant);

    // Exactly: --user-session map-drive|unmap-drive X: <folder>.
    public static UserSessionRequest? Parse(IReadOnlyList<string> args)
    {
        if (args is null || args.Count != 4 || !string.Equals(args[0], Verb, StringComparison.Ordinal))
        {
            return null;
        }

        UserSessionAction action;
        if (string.Equals(args[1], MapDrive, StringComparison.Ordinal))
        {
            action = UserSessionAction.MapDrive;
        }
        else if (string.Equals(args[1], UnmapDrive, StringComparison.Ordinal))
        {
            action = UserSessionAction.UnmapDrive;
        }
        else
        {
            return null;
        }

        if (!DevicePattern.IsMatch(args[2]))
        {
            return null;
        }

        var path = args[3];
        if (path.Length > 260 || !PathPattern.IsMatch(path) || path.EndsWith("\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0)
        {
            return null;
        }

        var letter = char.ToUpperInvariant(args[2][0]);
        return letter is 'A' or 'B' ? null : new UserSessionRequest(action, letter, path);
    }

    // The request is carried out only for the letter config.json records as CPFS's drive, and only for the folder
    // that drive points to (Tracked.Created.DrivePath) or the shared folder (SharePath).
    public static UserSessionResult Check(UserSessionRequest request, ConfigDocument? config)
    {
        if (request is null)
        {
            return UserSessionResult.Refused;
        }

        if (config is null)
        {
            return UserSessionResult.NoConfig;
        }

        var recorded = config.DriveLetter is { Length: 1 } l ? char.ToUpperInvariant(l[0]) : '\0';
        if (recorded != request.Letter)
        {
            return UserSessionResult.NotRecorded;
        }

        return SamePath(request.Path, config.TrackedDrivePath) || SamePath(request.Path, config.SharePath)
            ? UserSessionResult.Done
            : UserSessionResult.NotRecorded;
    }

    // The task action's arguments. The folder is quoted; Parse refuses a folder that would break the quoting.
    public static string Arguments(UserSessionAction action, char letter, string path)
    {
        var request = new UserSessionRequest(action, char.ToUpperInvariant(letter), path);
        var args = new[] { Verb, action == UserSessionAction.MapDrive ? MapDrive : UnmapDrive, request.Device, path };
        if (Parse(args) is null)
        {
            throw new ArgumentException("Not a drive and folder the user-session verb accepts.", nameof(path));
        }

        return Verb + " " + args[1] + " " + request.Device + " \"" + path + "\"";
    }

    public static bool IsSuccess(int code, UserSessionAction action) => (UserSessionResult)code switch
    {
        UserSessionResult.Done => true,
        UserSessionResult.AlreadyMapped => action == UserSessionAction.MapDrive,
        UserSessionResult.NothingToRemove or UserSessionResult.BootMapping => action == UserSessionAction.UnmapDrive,
        _ => false,
    };

    private static bool SamePath(string a, string? b) =>
        b is not null && string.Equals(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
