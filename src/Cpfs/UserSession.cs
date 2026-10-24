using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Cpfs.Core.Config;
using Cpfs.Core.Elevation;
using Cpfs.Core.Planning;

namespace Cpfs;

// "Cpfs.exe --user-session map-drive|unmap-drive X: <folder>", started by the CPFS-RunAsUser task as the desktop user
// with a limited token. DefineDosDevice from a caller that is not LocalSystem works in that caller's own device map
// [Learn: DefineDosDeviceW], which is what Explorer in that session sees. It checks its arguments against config.json,
// changes nothing but that one mapping, writes no log and no file, shows no window, and answers with its exit code.
internal static class UserSession
{
    private const uint DddRawTargetPath = 0x1;
    private const uint DddRemoveDefinition = 0x2;
    private const uint DddExactMatchOnRemove = 0x4;

    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;

    public static int Run(string[] args)
    {
        var request = UserSessionVerb.Parse(args);
        if (request is null)
        {
            return (int)UserSessionResult.Refused;
        }

        ConfigDocument config;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CPFS", "config.json");
            config = ConfigReader.Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ConfigFormatException)
        {
            return (int)UserSessionResult.NoConfig;
        }

        var check = UserSessionVerb.Check(request, config);
        if (check != UserSessionResult.Done)
        {
            return (int)check;
        }

        // The same NT path the DOS Devices value holds, given raw, so the definition and its removal match exactly.
        var target = Planner.DosTarget(request.Path);
        var current = Query(request.Device);
        if (request.Action == UserSessionAction.MapDrive)
        {
            if (current is not null)
            {
                return (int)(Same(current, target) ? UserSessionResult.AlreadyMapped : UserSessionResult.PointsElsewhere);
            }

            if (!DefineDosDevice(DddRawTargetPath, request.Device, target))
            {
                return (int)UserSessionResult.Failed;
            }

            return (int)(Same(Query(request.Device), target) ? UserSessionResult.Done : UserSessionResult.Failed);
        }

        if (current is null)
        {
            return (int)UserSessionResult.NothingToRemove;
        }

        if (!Same(current, target))
        {
            return (int)UserSessionResult.PointsElsewhere;
        }

        // Only an exact match is removed, so a mapping CPFS didn't make is never touched.
        if (!DefineDosDevice(DddRawTargetPath | DddRemoveDefinition | DddExactMatchOnRemove, request.Device, current))
        {
            return Marshal.GetLastWin32Error() switch
            {
                ErrorFileNotFound => (int)UserSessionResult.NothingToRemove,

                // What is left is the global mapping Windows made at start from the DOS Devices value; a limited
                // token can't remove it, and it goes at the next restart.
                ErrorAccessDenied => (int)UserSessionResult.BootMapping,
                _ => (int)UserSessionResult.Failed,
            };
        }

        return (int)UserSessionResult.Done;
    }

    private static bool Same(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // The current mapping: the first string of what QueryDosDevice returns (this session's map first, then the global
    // one); null when the name is not defined.
    private static string? Query(string device)
    {
        var buffer = new char[1024];
        var n = QueryDosDevice(device, buffer, (uint)buffer.Length);
        if (n == 0)
        {
            return null;
        }

        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? (int)n : end);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DefineDosDevice(uint flags, string deviceName, string? targetPath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, char[] targetPath, uint maxChars);
}
