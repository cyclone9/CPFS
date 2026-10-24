using System;
using Cpfs.Core.Apply;
using Cpfs.Core.Text;

namespace Cpfs;

// What the screen shows for a failure: CPFS's own sentence in the app's language, or Windows' own words as they came.
// The log keeps StepErrors.ForLog, in English.
internal static class UiErrors
{
    public static string ForUser(Exception ex) => StepErrors.TextOf(ex) is { } text ? Loc.Instance.Text(text) : StepErrors.ForUser(ex);

    public static string Of(string? error, TextKey? text) => text is not null ? Loc.Instance.Text(text) : error ?? "";

    public static string Of(StepReport report) => Of(report.Error, report.Text);
}
