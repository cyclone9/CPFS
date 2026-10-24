using System;
using System.ComponentModel;
using System.Reflection;

namespace Cpfs.Core.Apply;

// What a failed step shows and what the log keeps. The user sees Windows' own words without the exception's type or
// the API that failed; the log keeps both.
public static class StepErrors
{
    public static string ForUser(Exception ex)
    {
        var inner = Unwrap(ex);

        // The message of a Win32Exception thrown with its own text names the call ("NetUserSetInfo 1003: ..."); the
        // system's text for its code is Windows' words alone, in Windows' language.
        return inner is Win32Exception win32 ? new Win32Exception(win32.NativeErrorCode).Message : inner.Message;
    }

    public static string ForLog(Exception ex)
    {
        var inner = Unwrap(ex);
        var text = inner.GetType().Name + ": " + inner.Message;
        if (inner is Win32Exception win32)
        {
            text += " (code " + win32.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        }

        if (inner.InnerException is { } cause)
        {
            text += " <- " + cause.GetType().Name + ": " + cause.Message;
        }

        return text;
    }

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } i } ? i : ex;
}
