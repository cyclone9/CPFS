using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cpfs.Core.Config;
using Cpfs.Core.Drives;
using Cpfs.Core.Planning;
using Cpfs.Core.Text;
using Cpfs.Core.Validation;
using ValidationError = Cpfs.Core.Validation.ValidationError;
using Cpfs.Windows;

namespace Cpfs.Pages;

public sealed class PlanItemView
{
    public string Key { get; init; } = "";

    public string Badge { get; init; } = "";

    // The PlanAction name: picks the badge colour.
    public string BadgeKind { get; init; } = "";

    public string Subject { get; init; } = "";

    public bool SubjectIsData { get; init; }

    public bool SubjectIsText => !SubjectIsData;

    public string Detail { get; init; } = "";

    public bool HasDetail => Detail.Length > 0;

    public string Reason { get; init; } = "";

    public string BackupNote { get; init; } = "";

    public bool HasBackupNote => BackupNote.Length > 0;

    public bool IsAsk { get; init; }

    public bool Ticked { get; init; }

    public bool AskEnabled { get; init; } = true;

    public string LaterNote { get; init; } = "";

    public bool HasLaterNote => LaterNote.Length > 0;

    public string AskName { get; init; } = "";

    public string HelpText { get; init; } = "";
}

public sealed class PlanGroupView
{
    public string Title { get; init; } = "";

    public string Summary { get; init; } = "";

    public List<PlanItemView> Items { get; } = new();
}

// The Plan view: the form with the saved install as defaults, and what Setup would change. Planning writes nothing;
// Apply (SetupPage.Apply.cs) does. The password never leaves the two PasswordBoxes until Apply hands a copy to the
// account step; the plan only learns whether they are empty, valid, too short or different.
public partial class SetupPage
{
    private readonly Dictionary<string, bool> _choices = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _planTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private PasswordReveal[] _reveals = Array.Empty<PasswordReveal>();
    private Window? _window;
    private ConfigDocument? _config;
    private LetterScan _letters = new();
    private ProtectedFolders _protected = new();
    private SystemSnapshot? _snapshot;
    private string? _snapshotFor;
    private Plan? _plan;
    private bool _formReady;
    private bool _suppressForm;
    private int _planVersion;
    private string _lastShareName = "";
    private string? _lastLogged;

    private void InitPlan()
    {
        _planTimer.Tick += async (_, _) =>
        {
            _planTimer.Stop();
            await RecomputeAsync();
        };

        _reveals = new[]
        {
            new PasswordReveal(PasswordBox1, PasswordPlain1, PasswordEye1),
            new PasswordReveal(PasswordBox2, PasswordPlain2, PasswordEye2),
        };
        Loaded += (_, _) =>
        {
            if (_window is not null)
            {
                _window.Deactivated -= OnWindowDeactivated;
            }

            _window = Window.GetWindow(this);
            if (_window is not null)
            {
                _window.Deactivated += OnWindowDeactivated;
            }
        };
        Unloaded += (_, _) =>
        {
            HidePasswords();
            if (_window is not null)
            {
                _window.Deactivated -= OnWindowDeactivated;
                _window = null;
            }
        };
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => HidePasswords();

    private void HidePasswords()
    {
        foreach (var reveal in _reveals)
        {
            reveal.Hide();
        }
    }

    // Called after each read of the state: the form takes the saved install's values only the first time, so a
    // Refresh keeps what the user typed.
    private async Task PreparePlanAsync(ConfigDocument? config, LetterScan letters)
    {
        _config = config;
        _letters = letters;
        var desktopSid = Startup.IsElevated ? DesktopUser.Current.Sid : null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (snapshot, protectedFolders) = await Task.Run(() => (PlanReader.ReadStatic(letters, desktopSid), PlanReader.ProtectedFolders()));
        _protected = protectedFolders;
        if (!_formReady)
        {
            FillForm(SetupDefaults.From(config, letters));
            _formReady = true;
        }
        else
        {
            FillLetters(SelectedLetter());
        }

        _snapshot = snapshot;
        _snapshotFor = null;
        AppLog.Write("plan", "Plan state ready in " + StateReader.Ms(watch) + (Startup.IsElevated ? " (elevated)" : " (not elevated)"));
        await RecomputeAsync();
    }

    private void FillForm(SetupAnswers a)
    {
        _suppressForm = true;
        try
        {
            SharePathBox.Text = a.SharePath;
            ShareNameBox.Text = a.ShareName;
            _lastShareName = a.ShareName;
            SmbUserBox.Text = a.SmbUser;
            StrictSwitch.IsChecked = a.Strict;
            AutoPrivateSwitch.IsChecked = a.AutoPrivate;
            DriveSwitch.IsChecked = a.Drive;
            DriveNameBox.Text = a.DriveName;
            ShortcutSwitch.IsChecked = a.Shortcut;
            ShortcutNameBox.Text = a.ShortcutName;
            FillLetters(a.DriveLetter);
        }
        finally
        {
            _suppressForm = false;
        }
    }

    private void FillLetters(char? selected)
    {
        var own = _config?.DriveLetter is { Length: 1 } l ? l[0] : (char?)null;
        var wasSuppressed = _suppressForm;
        _suppressForm = true;
        try
        {
            DriveLetterBox.Items.Clear();
            foreach (var letter in _letters.Free(own))
            {
                DriveLetterBox.Items.Add(new ComboBoxItem { Content = letter + ":", Tag = letter });
                if (letter == selected)
                {
                    DriveLetterBox.SelectedIndex = DriveLetterBox.Items.Count - 1;
                }
            }
        }
        finally
        {
            _suppressForm = wasSuppressed;
        }
    }

    private char? SelectedLetter() => (DriveLetterBox.SelectedItem as ComboBoxItem)?.Tag as char?;

    private void OnFormChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressForm || !_formReady)
        {
            return;
        }

        if (ReferenceEquals(sender, ShareNameBox))
        {
            FollowShareName();
        }

        SchedulePlan();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_formReady)
        {
            SchedulePlan();
        }
    }

    private void OnPlanTickChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string key } box && box.IsChecked is { } ticked)
        {
            _choices[key] = ticked;
            SchedulePlan();
        }
    }

    // A drive or shortcut name that was the default for the old share name follows the new one.
    private void FollowShareName()
    {
        var newName = ShareNameBox.Text.Trim();
        var followed = SetupDefaults.FollowShareName(ReadForm(), _lastShareName, newName);
        _suppressForm = true;
        try
        {
            if (!string.Equals(DriveNameBox.Text, followed.DriveName, StringComparison.Ordinal))
            {
                DriveNameBox.Text = followed.DriveName;
            }

            if (!string.Equals(ShortcutNameBox.Text, followed.ShortcutName, StringComparison.Ordinal))
            {
                ShortcutNameBox.Text = followed.ShortcutName;
            }
        }
        finally
        {
            _suppressForm = false;
        }

        _lastShareName = newName;
    }

    private void SchedulePlan()
    {
        _planTimer.Stop();
        _planTimer.Start();
    }

    private SetupAnswers ReadForm() => new()
    {
        SharePath = SharePathBox.Text,
        ShareName = ShareNameBox.Text,
        SmbUser = SmbUserBox.Text,
        Strict = StrictSwitch.IsChecked == true,
        AutoPrivate = AutoPrivateSwitch.IsChecked == true,
        Drive = DriveSwitch.IsChecked == true,
        DriveLetter = SelectedLetter(),
        DriveName = DriveNameBox.Text,
        Shortcut = ShortcutSwitch.IsChecked == true,
        ShortcutName = ShortcutNameBox.Text,
        Password = PasswordState(),
        Choices = new Dictionary<string, bool>(_choices, StringComparer.Ordinal),
    };

    // Only the lengths and whether the two match leave the boxes; the comparison runs on unmanaged copies that are
    // zeroed at once, so no managed string holds the password unless the user shows it (PasswordReveal).
    private PasswordEntry PasswordState()
    {
        using var first = PasswordBox1.SecurePassword;
        using var second = PasswordBox2.SecurePassword;
        var error = Validators.Password(first.Length, first.Length == second.Length && SameSecret(first, second));
        return error switch
        {
            ValidationError.Empty when second.Length == 0 => PasswordEntry.Empty,
            ValidationError.None => PasswordEntry.Valid,
            ValidationError.TooShort => PasswordEntry.TooShort,
            _ => PasswordEntry.Mismatch,
        };
    }

    private static bool SameSecret(SecureString a, SecureString b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var pa = IntPtr.Zero;
        var pb = IntPtr.Zero;
        try
        {
            pa = Marshal.SecureStringToGlobalAllocUnicode(a);
            pb = Marshal.SecureStringToGlobalAllocUnicode(b);
            var diff = 0;
            for (var i = 0; i < a.Length; i++)
            {
                diff |= Marshal.ReadInt16(pa, i * 2) ^ Marshal.ReadInt16(pb, i * 2);
            }

            return diff == 0;
        }
        finally
        {
            if (pa != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(pa);
            }

            if (pb != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(pb);
            }
        }
    }

    // The account and folder parts of the state depend on what is typed; they are re-read off the UI thread when
    // those two fields change, everything else is planned from the reading already made.
    private async Task RecomputeAsync()
    {
        if (_snapshot is null || !_formReady)
        {
            return;
        }

        var version = ++_planVersion;
        var answers = ReadForm();
        var key = answers.SmbUser.Trim() + "|" + answers.SharePath.Trim() + "|" + answers.ShareName.Trim();
        var snapshot = _snapshot;
        if (!string.Equals(key, _snapshotFor, StringComparison.OrdinalIgnoreCase))
        {
            snapshot = await Task.Run(() => PlanReader.ReadAnswerParts(snapshot, answers.SmbUser, answers.SharePath, answers.ShareName));
            if (version != _planVersion)
            {
                return;
            }

            _snapshot = snapshot;
            _snapshotFor = key;
        }

        _plan = Planner.Setup(snapshot, answers, _config, _protected);
        ShowFieldErrors(answers, snapshot);
        RenderPlan();
        AppLog.Write("plan", "Plan: " + Counts(_plan));
        LogItems(_plan);
    }

    // The items that are not Keep, written again only when they differ from the last plan's.
    private void LogItems(Plan plan)
    {
        var lines = plan.LogLines();
        var joined = string.Join("\n", lines);
        if (string.Equals(joined, _lastLogged, StringComparison.Ordinal))
        {
            return;
        }

        _lastLogged = joined;
        foreach (var line in lines)
        {
            AppLog.Write("plan", "  " + line);
        }
    }

    private static string Counts(Plan plan)
    {
        var parts = new List<string>();
        foreach (PlanAction action in Enum.GetValues(typeof(PlanAction)))
        {
            parts.Add(action + " " + plan.Count(action).ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(", ", parts);
    }

    private void ShowFieldErrors(SetupAnswers a, SystemSnapshot s)
    {
        SetError(SharePathError, Validators.SharePath(a.SharePath, _protected).Error);
        SetError(ShareNameError, Validators.ShareName(a.ShareName).Error);
        SetError(SmbUserError, Validators.SmbUserName(a.SmbUser, s.WindowsUserNames, s.ComputerName).Error);
        SetError(DriveNameError, a.Drive ? Validators.DriveName(a.DriveName).Error : ValidationError.None);
        SetError(ShortcutNameError, a.Shortcut ? Validators.ShortcutName(a.ShortcutName).Error : ValidationError.None);
        if (a.Drive && DriveLetterBox.Items.Count == 0)
        {
            SetText(DriveError, Loc.Instance["Setup_Form_NoFreeLetter"]);
        }
        else
        {
            SetError(DriveError, a.Drive && a.DriveLetter is null ? ValidationError.Empty : ValidationError.None);
        }

        SetText(PasswordError, a.Password switch
        {
            PasswordEntry.TooShort => Loc.Instance.Format("Setup_Form_PasswordShort", Validators.MinPasswordLength),
            PasswordEntry.Mismatch => Loc.Instance["Setup_Form_PasswordMismatch"],
            _ => "",
        });
    }

    private static void SetError(TextBlock target, ValidationError error) =>
        SetText(target, error == ValidationError.None ? "" : Loc.Instance["Setup_Form_Error_" + error]);

    private static void SetText(TextBlock target, string text)
    {
        target.Text = text;
        target.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderPlan()
    {
        if (_plan is null)
        {
            return;
        }

        var loc = Loc.Instance;
        PlanSummary.Text = loc.Format("Setup_Plan_Summary",
            Formats.Count(_plan.Count(PlanAction.Keep), loc.Culture),
            Formats.Count(_plan.Count(PlanAction.Change), loc.Culture),
            Formats.Count(_plan.Count(PlanAction.Create), loc.Culture),
            Formats.Count(_plan.Count(PlanAction.RemoveIfOurs), loc.Culture),
            Formats.Count(_plan.Count(PlanAction.Ask), loc.Culture),
            Formats.Count(_plan.Count(PlanAction.CannotPlan), loc.Culture));

        PlanGroups.ItemsSource = Groups(_plan, out var needsAdmin);
        PlanNotice.IsOpen = needsAdmin;
        PlanNotice.Title = loc["Setup_Plan_NotElevatedTitle"];
        PlanNotice.Message = loc["Setup_Plan_NotElevated"];
        UpdateApplyState();
    }

    // The plan's items by section, in the sections' order, each group with its count of keep and other items.
    internal static List<PlanGroupView> Groups(Plan plan, out bool needsAdmin)
    {
        var loc = Loc.Instance;
        needsAdmin = false;
        var groups = new List<PlanGroupView>();
        foreach (PlanSection section in Enum.GetValues(typeof(PlanSection)))
        {
            var views = new List<PlanItemView>();
            int keep = 0, other = 0;
            foreach (var item in plan.Items)
            {
                if (item.Section != section)
                {
                    continue;
                }

                needsAdmin |= item.Cause == PlanCause.NeedsAdmin;
                if (item.Action == PlanAction.Keep)
                {
                    keep++;
                }
                else
                {
                    other++;
                }

                views.Add(ToView(item));
            }

            if (views.Count == 0)
            {
                continue;
            }

            var group = new PlanGroupView
            {
                Title = loc["Plan_Section_" + section],
                Summary = loc.Format("Setup_Plan_GroupSummary", Formats.Count(keep, loc.Culture), Formats.Count(other, loc.Culture)),
            };
            group.Items.AddRange(views);
            groups.Add(group);
        }

        return groups;
    }

    internal static PlanItemView ToView(PlanItem item)
    {
        var loc = Loc.Instance;
        var badge = item.Action == PlanAction.CannotPlan ? loc["Plan_Cause_" + item.Cause] : loc["Plan_Badge_" + item.Action];
        var subject = Value(item.Subject);
        var current = item.Current is null ? "" : Value(item.Current);
        var target = item.Target is null ? "" : Value(item.Target);
        var detail = current.Length > 0 && target.Length > 0 ? loc.Format("Setup_Plan_Arrow", current, target)
            : current.Length > 0 ? current
            : target;
        var args = new object[item.ReasonArgs.Count];
        for (var i = 0; i < args.Length; i++)
        {
            // An invalid answer names its form field; the field's label is shown instead of the internal name.
            var arg = item.ReasonArgs[i];
            args[i] = item.ReasonKey == "Plan_R_Invalid" ? loc["Setup_Form_" + arg] : Bidi.Embed(arg, loc.IsRtl);
        }

        var reason = string.Format(loc.Culture, loc[item.ReasonKey], args);

        // The executor skips the drive and the shortcut until 0.6.0: their items say so, and "create it again?" can't
        // be ticked yet.
        var later = item.Action is PlanAction.Create or PlanAction.Change or PlanAction.Ask
            && item.Section is PlanSection.Drive or PlanSection.Shortcut;
        return new PlanItemView
        {
            Key = item.Key,
            Badge = badge,
            BadgeKind = item.Action.ToString(),
            Subject = subject,
            SubjectIsData = item.Subject.WordKey is null,
            Detail = detail,
            Reason = reason,
            BackupNote = item.BackupNeeded ? loc["Setup_Plan_Backup"] : "",
            IsAsk = item.Action == PlanAction.Ask,
            Ticked = item.Ticked && !later,
            AskEnabled = !later,
            LaterNote = later ? loc["Setup_Plan_QuickAccessLater"] : "",
            AskName = loc.Format("Setup_Plan_AskName", subject),
            HelpText = detail.Length == 0 ? badge : loc.Format("Setup_Section_Help", badge, detail),
        };
    }

    private static string Value(PlanValue value)
    {
        var loc = Loc.Instance;
        if (value.WordList is { } words)
        {
            var texts = new string[words.Count];
            for (var i = 0; i < texts.Length; i++)
            {
                texts[i] = loc[words[i]];
            }

            return string.Join(loc["Plan_ListSeparator"], texts);
        }

        if (value.WordKey is { } key)
        {
            return value.Data is { } number ? loc.Format(key, number) : loc[key];
        }

        return Bidi.Embed(value.Data ?? "", loc.IsRtl);
    }
}
