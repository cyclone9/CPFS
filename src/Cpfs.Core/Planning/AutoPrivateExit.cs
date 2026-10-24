using System;
using System.Collections.Generic;
using System.Globalization;

namespace Cpfs.Core.Planning;

// The exit codes of "Cpfs.exe --auto-private". Task Scheduler keeps the last one as the task's LastTaskResult, which
// the Advanced page reads back and names (Describe). Refusals start at 10, clear of Windows' small error numbers (2,
// "file not found", is what a task whose program is missing can report), and a run that switched networks returns
// 100 plus their number, so neither can be taken for the other.
//
//   0        ran; no network needed switching
//   10       refused: not run from the copy in Program Files\CPFS
//   11       refused: not running with administrator rights
//   12       refused: Program Files\CPFS is not locked to SYSTEM and Administrators
//   13       refused: ProgramData\CPFS is not locked to SYSTEM and Administrators
//   14       nothing done: config.json could not be read
//   15       nothing done: Auto-Private is off in config.json
//   16       a switch to Private was tried and Windows kept the network Public
//   17       stopped by an unexpected error (the run log has it)
//   101-199  switched 1 to 99 networks to Private
public static class AutoPrivateExit
{
    public const int NothingToDo = 0;
    public const int NotFromCopy = 10;
    public const int NotElevated = 11;
    public const int CopyFolderNotLocked = 12;
    public const int DataFolderNotLocked = 13;
    public const int ConfigUnreadable = 14;
    public const int Off = 15;
    public const int NotTaken = 16;
    public const int Crashed = 17;
    public const int SwitchedBase = 100;
    public const int MaxCounted = 99;

    // Task Scheduler's own values [Learn: Task Scheduler Error and Success Constants]; ERROR_FILE_NOT_FOUND as an
    // HRESULT when the program could not be started.
    public const int TaskRunning = 0x00041301;
    public const int TaskHasNotRun = 0x00041303;
    public const int TaskTerminated = 0x00041306;
    public const int TaskAlreadyRunning = unchecked((int)0x8004131F);
    public const int ProgramNotFound = unchecked((int)0x80070002);

    // Every key Describe can give; Cpfs.Tests checks each has an English string.
    public static readonly IReadOnlyList<string> Keys = new[]
    {
        "AutoResult_SwitchedOne", "AutoResult_SwitchedMany", "AutoResult_NothingToDo", "AutoResult_NotFromCopy",
        "AutoResult_NotElevated", "AutoResult_CopyFolderNotLocked", "AutoResult_DataFolderNotLocked",
        "AutoResult_ConfigUnreadable", "AutoResult_Off", "AutoResult_NotTaken", "AutoResult_Crashed",
        "AutoResult_HasNotRun", "AutoResult_Running", "AutoResult_Terminated", "AutoResult_AlreadyRunning",
        "AutoResult_ProgramNotFound", "AutoResult_Other",
    };

    // Task Scheduler reports a task that never ran by its result code; its LastRunTime then holds no real date
    // [Learn documents neither value for that case, so both are checked].
    public static bool HasRun(DateTime? lastRun, int lastResult) =>
        lastResult != TaskHasNotRun && lastRun is { } time && time.Year >= 2000;

    public static int Switched(int count) => count <= 0 ? NothingToDo : SwitchedBase + Math.Min(count, MaxCounted);

    // The resource key of the words for a LastTaskResult, and the number it carries ({0}), if any.
    public static (string Key, string? Arg) Describe(int code)
    {
        if (code > SwitchedBase && code <= SwitchedBase + MaxCounted)
        {
            var n = code - SwitchedBase;
            return (n == 1 ? "AutoResult_SwitchedOne" : "AutoResult_SwitchedMany", n.ToString(CultureInfo.InvariantCulture));
        }

        return code switch
        {
            NothingToDo => ("AutoResult_NothingToDo", null),
            NotFromCopy => ("AutoResult_NotFromCopy", null),
            NotElevated => ("AutoResult_NotElevated", null),
            CopyFolderNotLocked => ("AutoResult_CopyFolderNotLocked", null),
            DataFolderNotLocked => ("AutoResult_DataFolderNotLocked", null),
            ConfigUnreadable => ("AutoResult_ConfigUnreadable", null),
            Off => ("AutoResult_Off", null),
            NotTaken => ("AutoResult_NotTaken", null),
            Crashed => ("AutoResult_Crashed", null),
            TaskHasNotRun => ("AutoResult_HasNotRun", null),
            TaskRunning => ("AutoResult_Running", null),
            TaskTerminated => ("AutoResult_Terminated", null),
            TaskAlreadyRunning => ("AutoResult_AlreadyRunning", null),
            ProgramNotFound => ("AutoResult_ProgramNotFound", null),
            _ => ("AutoResult_Other", "0x" + code.ToString("X8", CultureInfo.InvariantCulture)),
        };
    }
}
