using System;
using System.Collections.Generic;
using Cpfs.Core.Config;
using Cpfs.Core.Validation;

namespace Cpfs.Core.Planning;

// What Uninstall reads: the state (the account, folder and share named in config.json), config.json itself, the
// names the Auto-Private task logged, and the choices ticked on the page.
public sealed record UninstallInput
{
    public SystemSnapshot Snapshot { get; init; } = new();

    public ConfigDocument? Config { get; init; }

    public IReadOnlyList<string> AutoPrivateNames { get; init; } = Array.Empty<string>();

    // The SMB account's profile folder, when Windows has one.
    public string? ProfilePath { get; init; }

    // The signed-in and desktop users' SIDs: Uninstall never touches those accounts.
    public IReadOnlyList<string> ProtectedSids { get; init; } = Array.Empty<string>();

    // Who gets the files the SMB account owned, as shown.
    public string DesktopUser { get; init; } = "";

    // The folders Setup refuses to share; Uninstall refuses to act on a config.json whose SharePath is one of them.
    public ProtectedFolders ProtectedFolders { get; init; } = new();

    public IReadOnlyDictionary<string, bool> Choices { get; init; } = new Dictionary<string, bool>();

    public bool IsTicked(string key, bool defaultValue) => Choices.TryGetValue(key, out var t) ? t : defaultValue;
}

// Uninstall's plan, from config.json's Backup and Tracked and the state now, in the scripts' order: share, drive and
// shortcut, folder permissions and ownership, account, firewall and tasks, settings, networks, computer name, and the
// data folder last. Pure, like Planner.Setup. The advisor's rules:
// - files are never deleted; ownership goes back only for items the SMB account owns, to the desktop user, ticked;
// - the profile folder is offered only for an account CPFS created, unticked;
// - Windows' own firewall rules Setup turned off are turned on again on every path;
// - SMB1 off, Guest off and the logon audit always stay;
// - a copy of config.json goes to the desktop user's Documents before anything else, restored or not;
// - without a backup (0.1), Windows' defaults are offered instead.
public static class UninstallPlanner
{
    public const string RestoreKey = "u.restore";

    public static readonly IReadOnlyList<string> ReasonKeys = new[]
    {
        R.NoConfig, R.RefuseUser, R.RefusePath, R.ShareRemove, R.ShareForeign, R.UserDeleteUntracked, R.RestoreUnknown, R.ShareReplaced, R.Owner, R.AceRemove, R.UserDelete, R.UserDeleteExisted,
        R.UserDeleteAdmin, R.Profile, R.UserBefore, R.UserAdmin, R.UserHidden, R.UserGone, R.RuleRemove, R.BuiltInEnable,
        R.BuiltInMissing, R.Restore, R.RestoreValue, R.SettingsKept, R.Revert, R.AlwaysKept, R.NetworkPublic, R.NetworkSaved,
        R.SaveCopy, R.DataRemove, R.DataKept, R.Later, R.LabelRemove, R.DriveSession, R.DriveRemove, R.DriveForeign, R.ShortcutRemove,
    };

    public static readonly IReadOnlyList<string> WordKeys = new[]
    {
        S.Owner, S.Ace, S.Delete, S.Profile, S.Before, S.Restore, S.Revert, S.SaveCopy, S.Config, S.Kept,
    };

    // A backed-up value recorded as present but without a value: it is left as it is, never written as 0.
    public static bool IsUnknownOriginal(PlanItem item) => item is not null && string.Equals(item.ReasonKey, R.RestoreUnknown, StringComparison.Ordinal);

    // A choice that is not itself a change: it decides which other items appear.
    public static bool IsChoiceOnly(string key) => string.Equals(key, RestoreKey, StringComparison.Ordinal);

    public static Plan Uninstall(UninstallInput input)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var b = new Builder(input);
        b.Run();
        return new Plan(b.Items);
    }

    internal static class R
    {
        public const string NoConfig = "Uninst_R_NoConfig";
        public const string RefuseUser = "Uninst_R_RefuseUser";
        public const string RefusePath = "Uninst_R_RefusePath";
        public const string ShareForeign = "Uninst_R_ShareForeign";
        public const string UserDeleteUntracked = "Uninst_R_UserDeleteUntracked";
        public const string RestoreUnknown = "Uninst_R_RestoreUnknown";
        public const string ShareRemove = "Uninst_R_ShareRemove";
        public const string ShareReplaced = "Uninst_R_ShareReplaced";
        public const string Owner = "Uninst_R_Owner";
        public const string AceRemove = "Uninst_R_AceRemove";
        public const string UserDelete = "Uninst_R_UserDelete";
        public const string UserDeleteExisted = "Uninst_R_UserDeleteExisted";
        public const string UserDeleteAdmin = "Uninst_R_UserDeleteAdmin";
        public const string Profile = "Uninst_R_Profile";
        public const string UserBefore = "Uninst_R_UserBefore";
        public const string UserAdmin = "Uninst_R_UserAdmin";
        public const string UserHidden = "Uninst_R_UserHidden";
        public const string UserGone = "Uninst_R_UserGone";
        public const string RuleRemove = "Uninst_R_RuleRemove";
        public const string BuiltInEnable = "Uninst_R_BuiltInEnable";
        public const string BuiltInMissing = "Uninst_R_BuiltInMissing";
        public const string Restore = "Uninst_R_Restore";
        public const string RestoreValue = "Uninst_R_RestoreValue";
        public const string SettingsKept = "Uninst_R_SettingsKept";
        public const string Revert = "Uninst_R_Revert";
        public const string AlwaysKept = "Uninst_R_AlwaysKept";
        public const string NetworkPublic = "Uninst_R_NetworkPublic";
        public const string NetworkSaved = "Uninst_R_NetworkSaved";
        public const string SaveCopy = "Uninst_R_SaveCopy";
        public const string DataRemove = "Uninst_R_DataRemove";
        public const string DataKept = "Uninst_R_DataKept";
        public const string Later = "Uninst_R_Later";
        public const string LabelRemove = "Uninst_R_LabelRemove";
        public const string DriveSession = "Uninst_R_DriveSession";
        public const string DriveRemove = "Uninst_R_DriveRemove";
        public const string DriveForeign = "Uninst_R_DriveForeign";
        public const string ShortcutRemove = "Uninst_R_ShortcutRemove";
    }

    internal static class S
    {
        public const string Owner = "Uninst_S_Owner";
        public const string Ace = "Uninst_S_Ace";
        public const string Delete = "Uninst_S_Delete";
        public const string Profile = "Uninst_S_Profile";
        public const string Before = "Uninst_S_Before";
        public const string Restore = "Uninst_S_Restore";
        public const string Revert = "Uninst_S_Revert";
        public const string SaveCopy = "Uninst_S_SaveCopy";
        public const string Config = "Uninst_S_Config";
        public const string Kept = "Uninst_S_Kept";
    }

    private sealed class Builder
    {
        private readonly UninstallInput _in;
        private readonly SystemSnapshot _s;
        private readonly ConfigDocument? _c;
        private bool _deferredLeft;

        public Builder(UninstallInput input)
        {
            _in = input;
            _s = input.Snapshot;
            _c = input.Config;
        }

        public List<PlanItem> Items { get; } = new();

        private bool HasBackup => _c?.Backup == BackupState.Present;

        private bool Restoring => HasBackup && _in.IsTicked(RestoreKey, true);

        public void Run()
        {
            if (_c is null)
            {
                Add("u.config", PlanSection.Backup, PlanAction.Keep, Word(S.Config), Word(Planner.W.Absent), null, R.NoConfig);
                Firewall();
                return;
            }

            var user = _c.SmbUser?.Trim();
            var info = _s.User.IsOk ? _s.User.Value : null;
            if (user is { Length: > 0 } && (Contains(_s.WindowsUserNames, user) || (info is not null && Contains(_in.ProtectedSids, info.Sid))))
            {
                Items.Add(new PlanItem
                {
                    Key = "u.guard", Section = PlanSection.User, Action = PlanAction.CannotPlan, Cause = PlanCause.InvalidAnswer,
                    Subject = Word(Planner.S.Account), Current = PlanValue.Of(user), ReasonKey = R.RefuseUser, ReasonArgs = new[] { user },
                });
                return;
            }

            // Ownership and the folder permission work on SharePath: a path Setup would refuse stops everything.
            var path = Validators.SharePath(_c.SharePath, _in.ProtectedFolders);
            if (path.Error is not (ValidationError.None or ValidationError.Empty))
            {
                Items.Add(new PlanItem
                {
                    Key = "u.guard", Section = PlanSection.Folder, Action = PlanAction.CannotPlan, Cause = PlanCause.InvalidAnswer,
                    Subject = Word(Planner.S.Folder), Current = PlanValue.Of(_c.SharePath ?? "?"), ReasonKey = R.RefusePath,
                    ReasonArgs = new[] { _c.SharePath ?? "?" },
                });
                return;
            }

            if (HasBackup)
            {
                AskItem(RestoreKey, PlanSection.Backup, Word(S.Restore), null, null, true, R.Restore, _c.BackupTaken ?? "?");
            }

            // First, before anything changes: ProgramData\CPFS, and with it the only copy of the backup, goes last.
            Add("u.savecopy", PlanSection.DataFolder, PlanAction.Create, Word(S.SaveCopy), null, null, R.SaveCopy);

            Shares();
            StaleTask();
            QuickAccess();
            Later();
            Folder(user, info);
            User(user, info);
            Firewall();
            Settings();
            Networks();
            Computer();
            Data();
        }

        private void Shares()
        {
            if (!_s.Shares.IsOk)
            {
                Unread("u.share", PlanSection.Share, Word(Planner.S.Share), _s.Shares);
                return;
            }

            foreach (var share in _s.Shares.Value!)
            {
                if (share.Special)
                {
                    continue;
                }

                // The same rule as Setup: the share config.json recorded (name and folder), or one with CPFS's description.
                var sameName = SameText(share.Name, _c!.ShareName);
                var ours = (sameName && SamePath(share.Path, _c.SharePath))
                    || string.Equals(share.Description, Planner.ShareDescription, StringComparison.Ordinal);
                if (ours)
                {
                    Add("u.share:" + share.Name, PlanSection.Share, PlanAction.RemoveIfOurs, PlanValue.Of(share.Name), PlanValue.Of(share.Path ?? "?"),
                        Word(Planner.W.Absent), R.ShareRemove);
                }
                else if (sameName)
                {
                    AskItem("u.share:" + share.Name, PlanSection.Share, PlanValue.Of(share.Name), PlanValue.Of(share.Path ?? "?"), Word(Planner.W.Absent), false,
                        R.ShareForeign, share.Name, share.Path ?? "?");
                }
            }

            foreach (var replaced in _c!.TrackedReplacedShares)
            {
                Add("u.replaced:" + replaced.Name, PlanSection.Share, PlanAction.Keep, PlanValue.Of(replaced.Name), PlanValue.Of(replaced.Path ?? "?"), null,
                    R.ShareReplaced, replaced.Name, replaced.Path ?? "?");
            }
        }

        // The drive and the shortcut config.json records, and only while they are still CPFS's: the drive name it
        // gave (or the one that was there before, put back), the mapping in the desktop user's session and the DOS
        // Devices value pointing at the recorded folder, and the shortcut file (its target is checked when it runs).
        private void QuickAccess()
        {
            var letter = _c!.DriveLetter is { Length: 1 } l && l[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' ? char.ToUpperInvariant(l[0]) : (char?)null;
            if (letter is { } d)
            {
                var labels = _s.DriveLabels.IsOk ? _s.DriveLabels.Value! : null;
                var current = labels is not null && labels.TryGetValue(d, out var cl) ? cl : null;
                var original = _c.BackupDriveLabel(d);
                if (labels is null)
                {
                    Unread("u.drive.label", PlanSection.Drive, Word(Planner.S.Label), _s.DriveLabels);
                }
                else if (original is not null && !string.Equals(current, original, StringComparison.Ordinal) && (current is null || SameText(current, _c.OwnDriveLabel)))
                {
                    Add("u.drive.label", PlanSection.Drive, PlanAction.Change, Word(Planner.S.Label), current is null ? Word(Planner.W.Absent) : PlanValue.Of(current),
                        PlanValue.Of(original), Planner.R.LabelRestore);
                }
                else if (original is null && current is not null && SameText(current, _c.OwnDriveLabel))
                {
                    Add("u.drive.label", PlanSection.Drive, PlanAction.RemoveIfOurs, Word(Planner.S.Label), PlanValue.Of(current), Word(Planner.W.Absent), R.LabelRemove);
                }

                var path = _c.OwnDrivePath;
                if (!_s.DosDevices.IsOk)
                {
                    Unread("u.drive", PlanSection.Drive, Word(Planner.S.Drive), _s.DosDevices);
                }
                else if (_s.DosDevices.Value!.TryGetValue(d, out var value))
                {
                    var shown = PlanValue.Of(d + ": " + value);
                    if (path is { Length: > 0 } && SameText(value, Planner.DosTarget(path)))
                    {
                        Add("u.drive.session", PlanSection.Drive, PlanAction.RemoveIfOurs, Word(Planner.S.DriveSession), shown, Word(Planner.W.Absent), R.DriveSession);
                        Add("u.drive", PlanSection.Drive, PlanAction.RemoveIfOurs, Word(Planner.S.Drive), shown, Word(Planner.W.Absent), R.DriveRemove);
                    }
                    else
                    {
                        Add("u.drive", PlanSection.Drive, PlanAction.Keep, Word(Planner.S.Drive), shown, null, R.DriveForeign);
                    }
                }
            }

            if (_c.Shortcut is { Length: > 0 } lnk && _s.PublicDesktopFiles.IsOk
                && SameText(lnk.Substring(0, Math.Max(0, lnk.LastIndexOf('\\'))), _s.CommonDesktop.TrimEnd('\\')))
            {
                var name = lnk.Substring(lnk.LastIndexOf('\\') + 1);
                foreach (var f in _s.PublicDesktopFiles.Value!)
                {
                    if (string.Equals(f, name, StringComparison.OrdinalIgnoreCase))
                    {
                        Add("u.shortcut", PlanSection.Shortcut, PlanAction.RemoveIfOurs, Word(Planner.S.Shortcut), PlanValue.Of(lnk), Word(Planner.W.Absent), R.ShortcutRemove);
                    }
                }
            }
        }

        // A leftover CPFS-RunAsUser task in the root folder: Setup's drive.session step creates and deletes it, so one
        // that exists now is CPFS's and goes.
        private void StaleTask()
        {
            if (_s.Tasks.IsOk && _s.Tasks.Value!.TryGetValue(Planner.RunAsUserTask, out var stale) && stale)
            {
                Add("u.task.runasuser", PlanSection.Drive, PlanAction.RemoveIfOurs, PlanValue.Of(Planner.RunAsUserTask), Word(Planner.W.Exists),
                    Word(Planner.W.Absent), Planner.R.StaleTask);
            }
        }

        // Auto-Private is removed from a later version on; until then its tasks are shown and left.
        private void Later()
        {
            if (_s.Tasks.IsOk)
            {
                foreach (var task in new[] { Planner.AutoPrivateTask, Planner.LegacyAutoPrivateTask })
                {
                    if (_s.Tasks.Value!.TryGetValue(task, out var exists) && exists)
                    {
                        Deferred("u.task:" + task, PlanSection.AutoPrivate, PlanValue.Of(task), Word(Planner.W.Exists));
                    }
                }
            }
        }

        private void Folder(string? user, UserInfo? info)
        {
            if (user is null || info is null || !_s.Folder.IsOk || !_s.Folder.Value!.Exists)
            {
                return;
            }

            var path = _c!.SharePath ?? "?";
            AskItem("u.owner", PlanSection.Folder, Word(S.Owner), PlanValue.Of(path), null, true, R.Owner, user, _in.DesktopUser);
            if (_s.Folder.Value.UserHasModify)
            {
                Add("u.ace", PlanSection.Folder, PlanAction.RemoveIfOurs, Word(S.Ace), Word(Planner.W.Modify), Word(Planner.W.NoAccess), R.AceRemove, user);
            }
        }

        private void User(string? user, UserInfo? info)
        {
            if (user is null)
            {
                return;
            }

            if (!_s.User.IsOk)
            {
                Unread("u.user", PlanSection.User, Word(Planner.S.Account), _s.User);
                return;
            }

            if (info is null)
            {
                Add("u.user", PlanSection.User, PlanAction.Keep, PlanValue.Of(user), Word(Planner.W.Absent), null, R.UserGone);
                return;
            }

            TrackedUser? tracked = null;
            foreach (var t in _c!.TrackedUsers)
            {
                if (SameText(t.Name, user))
                {
                    tracked = t;
                }
            }

            // Deleted by default only when CPFS recorded creating it (or a 0.1 install, which created its account); one it
            // never recorded is kept unless ticked, unlike the scripts.
            var created = tracked?.Existed == false || (_c.Shape == ConfigShape.V01 && SameText(_c.SmbUser, user));
            var existed = tracked?.Existed == true;
            var admin = info.IsAdmin == true;
            var deleteDefault = created && !admin;
            var delete = AskItem("u.user.delete", PlanSection.User, Word(S.Delete), PlanValue.Of(user), Word(Planner.W.Absent), deleteDefault,
                admin ? R.UserDeleteAdmin : existed ? R.UserDeleteExisted : created ? R.UserDelete : R.UserDeleteUntracked);
            if (delete)
            {
                if (created && _in.ProfilePath is { Length: > 0 } profile)
                {
                    // Inserted before the account's own item: the profile is found by the account's SID.
                    var item = NewAsk("u.user.profile", PlanSection.User, Word(S.Profile), PlanValue.Of(profile), Word(Planner.W.Absent), false, R.Profile, profile);
                    Items.Insert(Items.Count - 1, item);
                }

                return;
            }

            var before = _c.TrackedUserBefore(user);
            if (before is not null)
            {
                var same = (before.Enabled ?? info.Enabled) == info.Enabled
                    && (before.PasswordNeverExpires ?? info.PasswordNeverExpires) == info.PasswordNeverExpires
                    && Contains(before.Groups, AdminsSid) == admin
                    && Contains(before.Groups, UsersSid) == (info.InUsers == true)
                    && before.HiddenValue == info.HiddenValue;
                Add("u.user.before", PlanSection.User, same ? PlanAction.Keep : PlanAction.Change, Word(S.Before), null, null,
                    same ? Planner.R.AtTarget : R.UserBefore);
                return;
            }

            if (tracked?.WasAdmin == true && !admin)
            {
                AskItem("u.user.admin", PlanSection.User, Word(Planner.S.Admins), Word(Planner.W.NotMember), Word(Planner.W.Member), false, R.UserAdmin);
            }

            if (existed && info.HiddenValue is not null)
            {
                Add("u.user.hidden", PlanSection.User, PlanAction.RemoveIfOurs, Word(Planner.S.Hidden), Planner.HiddenWord(info.HiddenValue),
                    Word(Planner.W.Visible), R.UserHidden);
            }
        }

        private void Firewall()
        {
            if (!_s.OwnRules.IsOk)
            {
                Unread("u.fw", PlanSection.Firewall, PlanValue.Of(FirewallRules.LanRule), _s.OwnRules);
            }
            else
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rule in _s.OwnRules.Value!)
                {
                    if (seen.Add(rule.Name))
                    {
                        Add("u.fw:" + rule.Name, PlanSection.Firewall, PlanAction.RemoveIfOurs, PlanValue.Of(rule.Name), Word(Planner.W.Exists),
                            Word(Planner.W.Absent), R.RuleRemove);
                    }
                }
            }

            if (_c is null)
            {
                return;
            }

            foreach (var id in _c.TrackedFirewallRules)
            {
                BuiltInRuleInfo? rule = null;
                foreach (var r in _s.BuiltInRules.Value ?? Array.Empty<BuiltInRuleInfo>())
                {
                    if (string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        rule = r;
                    }
                }

                var key = "u.builtin:" + id;
                if (!_s.BuiltInRules.IsOk)
                {
                    Unread(key, PlanSection.Firewall, PlanValue.Of(id), _s.BuiltInRules);
                }
                else if (rule is null)
                {
                    Add(key, PlanSection.Firewall, PlanAction.Keep, PlanValue.Of(id), Word(Planner.W.Absent), null, R.BuiltInMissing);
                }
                else if (rule.Enabled)
                {
                    Add(key, PlanSection.Firewall, PlanAction.Keep, PlanValue.Of(id), Word(Planner.W.Enabled), null, Planner.R.AtTarget);
                }
                else
                {
                    Add(key, PlanSection.Firewall, PlanAction.Change, PlanValue.Of(id), Word(Planner.W.Disabled), Word(Planner.W.Enabled), R.BuiltInEnable);
                }
            }
        }

        private void Settings()
        {
            if (Restoring)
            {
                foreach (var entry in _c!.BackupRegistry)
                {
                    if (entry.Name is not { Length: > 0 } name || entry.Location is null)
                    {
                        continue;
                    }

                    var original = entry.Exists == true ? entry.Value : null;
                    var key = "u.reg:" + name;
                    var subject = PlanValue.Of(name);
                    if (entry.Exists == true && entry.Value is null)
                    {
                        var now = _s.Hardening.TryGetValue(name, out var r) && r.IsOk && r.Value is { } v ? Planner.HardeningWord(name, v) : Word(Planner.W.Unknown);
                        Add(key, PlanSection.Hardening, PlanAction.Keep, subject, now, null, R.RestoreUnknown);
                        continue;
                    }

                    var target = Planner.HardeningWord(name, original);
                    if (_s.Hardening.TryGetValue(name, out var reading) && reading.IsOk)
                    {
                        var current = reading.Value;
                        var same = current == original;
                        Add(key, PlanSection.Hardening, same ? PlanAction.Keep : PlanAction.Change, subject,
                            Planner.HardeningWord(name, current), same ? null : target, same ? Planner.R.AtTarget : R.RestoreValue);
                    }
                    else
                    {
                        Add(key, PlanSection.Hardening, PlanAction.Change, subject, Word(Planner.W.Unknown), target, R.RestoreValue);
                    }
                }

                var smb = _s.SmbServer.IsOk ? _s.SmbServer.Value : null;
                RestoreBool("u.smb2", Word(Planner.S.Smb2), smb?.Smb2, _c.BackupSmb2);
                RestoreBool("u.reject", Word(Planner.S.Reject), smb?.RejectUnencrypted, _c.BackupRejectUnencrypted);

                var start = _c.BackupServerStart switch
                {
                    "Automatic" => 2,
                    "Manual" => 3,
                    "Disabled" => 4,
                    _ => (int?)null,
                };
                if (start is { } wanted)
                {
                    var current = _s.ServerStart.IsOk ? _s.ServerStart.Value : null;
                    var same = current == wanted;
                    Add("u.serverstart", PlanSection.Server, same ? PlanAction.Keep : PlanAction.Change, Word(Planner.S.ServerStart),
                        current is { } c ? Planner.StartWord(c) : Word(Planner.W.Unknown), same ? null : Planner.StartWord(wanted), same ? Planner.R.AtTarget : R.RestoreValue);
                }

                if (_c.BackupLockout is { } threshold)
                {
                    var current = _s.Lockout.IsOk ? _s.Lockout.Value : null;
                    var same = current is not null && current.Threshold == threshold
                        && (_c.BackupLockoutDuration is not { } d || current.DurationSeconds == d)
                        && (_c.BackupLockoutWindow is not { } w || current.WindowSeconds == w);
                    Add("u.lockout", PlanSection.Hardening, same ? PlanAction.Keep : PlanAction.Change, Word(Planner.S.LockoutThreshold),
                        current is not null ? Planner.LockoutWord(current.Threshold) : Word(Planner.W.Unknown), same ? null : Planner.LockoutWord(threshold),
                        same ? Planner.R.AtTarget : R.RestoreValue);
                }
            }
            else if (HasBackup)
            {
                Add("u.settings", PlanSection.Hardening, PlanAction.Keep, Word(S.Restore), null, null, R.SettingsKept);
            }
            else
            {
                AskItem("u.revert", PlanSection.Hardening, Word(S.Revert), null, null, false, R.Revert);
            }

            Add("u.keep", PlanSection.Hardening, PlanAction.Keep, Word(S.Kept), null, null, R.AlwaysKept);
        }

        private void RestoreBool(string key, PlanValue subject, bool? current, bool? original)
        {
            if (original is null)
            {
                return;
            }

            var same = current == original;
            Add(key, PlanSection.Server, same ? PlanAction.Keep : PlanAction.Change, subject,
                current is { } c ? Word(c ? Planner.W.On : Planner.W.Off) : Word(Planner.W.Unknown),
                same ? null : Word(original.Value ? Planner.W.On : Planner.W.Off), same ? Planner.R.AtTarget : R.RestoreValue);
        }

        private void Networks()
        {
            var names = new List<string>();
            foreach (var n in _c!.TrackedNetworks)
            {
                AddName(names, n);
            }

            foreach (var n in _c.TrackedNetworkProfiles)
            {
                AddName(names, n.Name);
            }

            foreach (var n in _in.AutoPrivateNames)
            {
                AddName(names, n);
            }

            foreach (var name in names)
            {
                var connected = false;
                var allPublic = true;
                foreach (var n in _s.Networks.Value ?? Array.Empty<NetworkInfo>())
                {
                    if (string.Equals(n.Name, name, StringComparison.Ordinal))
                    {
                        connected = true;
                        allPublic &= n.Category == 0;
                    }
                }

                var key = "u.net:" + name;
                if (connected && allPublic)
                {
                    Add(key, PlanSection.Networks, PlanAction.Keep, PlanValue.Of(name), Word(Planner.W.Public), null, Planner.R.AtTarget);
                }
                else
                {
                    AskItem(key, PlanSection.Networks, PlanValue.Of(name), connected ? Word(Planner.W.Private) : null, Word(Planner.W.Public), Restoring,
                        connected ? R.NetworkPublic : R.NetworkSaved);
                }
            }
        }

        private void Computer()
        {
            var original = _c!.BackupHostName?.Trim();
            var names = _s.ComputerNames.IsOk ? _s.ComputerNames.Value : null;
            var current = names?.Pending ?? names?.Active;
            if (Restoring && original is { Length: > 0 } && current is not null && !SameText(original, current))
            {
                Deferred("u.computer", PlanSection.Computer, Word(Planner.S.ComputerName), PlanValue.Of(current), PlanValue.Of(original));
            }
        }

        private void Data()
        {
            if (!_s.DataFolder.IsOk || !_s.DataFolder.Value!.Exists)
            {
                return;
            }

            if (_deferredLeft)
            {
                Add("u.data", PlanSection.DataFolder, PlanAction.Keep, Word(Planner.S.DataFolder), Word(Planner.W.Exists), null, R.DataKept);
            }
            else
            {
                Add("u.data", PlanSection.DataFolder, PlanAction.RemoveIfOurs, Word(Planner.S.DataFolder), Word(Planner.W.Exists), Word(Planner.W.Absent),
                    R.DataRemove);
            }
        }

        private void Deferred(string key, PlanSection section, PlanValue subject, PlanValue current, PlanValue? target = null)
        {
            _deferredLeft = true;
            Add(key, section, target is null ? PlanAction.RemoveIfOurs : PlanAction.Change, subject, current, target ?? Word(Planner.W.Absent), R.Later);
        }

        private bool AskItem(string key, PlanSection section, PlanValue subject, PlanValue? current, PlanValue? target, bool defaultTicked, string reason,
            params string[] args)
        {
            var item = NewAsk(key, section, subject, current, target, defaultTicked, reason, args);
            Items.Add(item);
            return item.Ticked;
        }

        private PlanItem NewAsk(string key, PlanSection section, PlanValue subject, PlanValue? current, PlanValue? target, bool defaultTicked, string reason,
            params string[] args) => new()
        {
            Key = key, Section = section, Action = PlanAction.Ask, Subject = subject, Current = current, Target = target,
            Ticked = _in.IsTicked(key, defaultTicked), ReasonKey = reason, ReasonArgs = args,
        };

        private void Add(string key, PlanSection section, PlanAction action, PlanValue subject, PlanValue? current, PlanValue? target, string reason,
            params string[] args) =>
            Items.Add(new PlanItem
            {
                Key = key, Section = section, Action = action, Subject = subject, Current = current, Target = target, ReasonKey = reason, ReasonArgs = args,
            });

        private void Unread<T>(string key, PlanSection section, PlanValue subject, Reading<T> reading) =>
            Items.Add(new PlanItem
            {
                Key = key, Section = section, Action = PlanAction.CannotPlan,
                Cause = reading.State == ReadState.NeedsAdmin ? PlanCause.NeedsAdmin : PlanCause.ReadFailed,
                Subject = subject,
                ReasonKey = reading.State == ReadState.NeedsAdmin ? Planner.R.NeedsAdmin : Planner.R.ReadFailed,
                ReasonArgs = reading.State == ReadState.NeedsAdmin ? Array.Empty<string>() : new[] { reading.Error ?? "?" },
            });

        private static void AddName(List<string> names, string name)
        {
            if (name.Trim().Length > 0 && !names.Contains(name))
            {
                names.Add(name);
            }
        }
    }

    public const string AdminsSid = "S-1-5-32-544";
    public const string UsersSid = "S-1-5-32-545";

    private static PlanValue Word(string key) => PlanValue.Word(key);

    private static bool SameText(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string? a, string? b) => SameText(a?.Trim().TrimEnd('\\'), b?.Trim().TrimEnd('\\'));

    private static bool Contains(IReadOnlyList<string> list, string value)
    {
        foreach (var item in list)
        {
            if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
