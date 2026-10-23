using System;
using System.Collections.Generic;
using System.DirectoryServices.AccountManagement;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;

namespace Cpfs.Windows;

internal enum ReadStatus
{
    Ok,
    Missing,
    NeedsAdmin,
    Failed,
}

// One line of the read-only state view. Labels and words are resource keys, so a language switch re-renders the
// same reading; Value holds data (paths, names, numbers) shown as found, left to right.
internal sealed class StateRow
{
    public string? LabelKey { get; init; }

    public string? Label { get; init; }

    public string? Value { get; init; }

    public string? ValueKey { get; init; }

    public object[] ValueArgs { get; init; } = Array.Empty<object>();

    public ReadStatus Status { get; init; } = ReadStatus.Ok;

    public string? Error { get; init; }

    public static StateRow Data(string labelKey, string? value) =>
        value is null ? Word(labelKey, "State_NotSet") : new StateRow { LabelKey = labelKey, Value = value };

    public static StateRow Word(string labelKey, string valueKey, params object[] args) =>
        new() { LabelKey = labelKey, ValueKey = valueKey, ValueArgs = args };

    public static StateRow YesNo(string labelKey, bool? value) =>
        Word(labelKey, value is null ? "State_Unknown" : value.Value ? "Common_Yes" : "Common_No");

    public static StateRow Failure(string labelKey, Exception ex) => new()
    {
        LabelKey = labelKey,
        Status = State.IsAccessDenied(ex) ? ReadStatus.NeedsAdmin : ReadStatus.Failed,
        Error = ex.GetType().Name + ": " + ex.Message,
    };
}

internal sealed class StateSection
{
    public StateSection(string id)
    {
        Id = id;
    }

    // Also the resource key stem: Setup_<Id>_Title and Setup_<Id>_Desc.
    public string Id { get; }

    public List<StateRow> Rows { get; } = new();

    public ReadStatus Status { get; set; } = ReadStatus.Ok;

    public string? Error { get; set; }

    public void Fail(Exception ex)
    {
        Status = State.IsAccessDenied(ex) ? ReadStatus.NeedsAdmin : ReadStatus.Failed;
        Error = ex.GetType().Name + ": " + ex.Message;
    }
}

internal static class State
{
    private const int EAccessDenied = unchecked((int)0x80070005);
    private const int WbemAccessDenied = unchecked((int)0x80041003);

    public static bool IsAccessDenied(Exception? ex) => ex switch
    {
        null => false,
        UnauthorizedAccessException or SecurityException => true,
        ManagementException m => m.ErrorCode == ManagementStatus.AccessDenied,
        COMException c => c.HResult is EAccessDenied or WbemAccessDenied,
        TargetInvocationException t => IsAccessDenied(t.InnerException),
        PrincipalOperationException p => p.ErrorCode == EAccessDenied || IsAccessDenied(p.InnerException),
        IOException io => io.HResult == EAccessDenied,
        _ => false,
    };

    // The exceptions a read of Windows state can end in; anything else is a bug and goes to the crash handler.
    public static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException
        or ManagementException or COMException or TargetInvocationException or PrincipalException or InvalidOperationException
        or ArgumentException or InvalidCastException or NotSupportedException or System.ComponentModel.Win32Exception;
}
