using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Cpfs.Core.Config;
using Cpfs.Core.Validation;

namespace Cpfs.Core.Planning;

// Setup's plan: the answers and the state read from Windows turned into a list of changes. Pure: no Windows call,
// so every rule is tested on Linux. The rules are CPFS's non-negotiables:
// - the backup is taken once, on the first Setup, and never again;
// - an existing setting stricter than CPFS's value is kept;
// - no network becomes Private, no foreign share or account is taken over, unless the user ticks it;
// - the VPN rule is bound to the VPN adapter, and an adapter on a Public network is used only when ticked;
// - CPFS removes only what it made (its "CPFS" name, or what config.json recorded);
// - the lockout threshold is set only when it is 0;
// - the drive letter must be free (CPFS's own letter counts as free);
// - only the SMB account keeps access to CPFS's share; anyone else is removed only when ticked.
public static class Planner
{
    public const int LockoutThreshold = 10;

    public const string AutoPrivateTask = "CPFS-AutoPrivate";
    public const string RunAsUserTask = "CPFS-RunAsUser";
    public const string LegacyAutoPrivateTask = "CyShare-AutoPrivate";

    public const string ShareDescription = "CPFS - CyClone Power File Share";

    // Logon subcategory audit flags: success 1, failure 2.
    public static readonly IReadOnlyList<HardeningValue> Hardening = new[]
    {
        new HardeningValue(@"SYSTEM\CurrentControlSet\Control\Lsa", "forceguest", 0, Stricter.Lower),
        new HardeningValue(@"SYSTEM\CurrentControlSet\Control\Lsa", "LmCompatibilityLevel", 5, Stricter.Higher),
        new HardeningValue(@"SYSTEM\CurrentControlSet\Control\Lsa", "NoLMHash", 1, Stricter.Higher),
        new HardeningValue(@"SYSTEM\CurrentControlSet\Control\Lsa", "RestrictAnonymous", 1, Stricter.Higher),
        new HardeningValue(@"SYSTEM\CurrentControlSet\Control\Lsa", "RestrictAnonymousSAM", 1, Stricter.Higher),
        new HardeningValue(@"SYSTEM\CurrentControlSet\Control\Lsa", "EveryoneIncludesAnonymous", 0, Stricter.Lower),
        new HardeningValue(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "RestrictNullSessAccess", 1, Stricter.Higher),
        new HardeningValue(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "AutoShareWks", 0, Stricter.Lower),
    };

    // Every reason key the planner can produce; Cpfs.Tests checks each has an English string.
    public static readonly IReadOnlyList<string> ReasonKeys = new[]
    {
        R.NeedsAdmin, R.ReadFailed, R.Invalid, R.AtTarget, R.Stricter,
        R.BackupFresh, R.BackupTaken, R.BackupNone,
        R.AccountCreate, R.AccountForeign, R.AccountTakenOver, R.AccountEnable, R.NeverExpires, R.Unlock, R.UsersGroup,
        R.Admin, R.AdminNew, R.Hidden, R.PasswordKeep, R.PasswordSet, R.PasswordNeeded,
        R.FolderCreate, R.AceGrant, R.AceAfterAccount,
        R.ShareCreate, R.ShareChange, R.ShareMove, R.ShareForeignName, R.ShareForeignPath, R.ShareOld, R.AdminShares,
        R.ServerStart, R.Smb2, R.Reject, R.Smb1, R.Smb1Feature, R.Smb1FeatureGone,
        R.Hardening, R.Guest, R.NoGuest, R.LockoutSet, R.LockoutKept, R.Audit,
        R.RuleCreate, R.RuleChange, R.RuleDuplicate, R.RuleRemove, R.RuleLegacy, R.VpnNone, R.VpnAdapter, R.VpnAdapterPublic,
        R.BuiltInDisable, R.BuiltInPortUnknown, R.BuiltInNone,
        R.NetworkPublic, R.NetworkPrivate,
        R.AutoCreate, R.AutoRemove, R.AutoOff, R.LegacyTask, R.StaleTask,
        R.DriveCreate, R.DriveGone, R.DriveForeign, R.DriveSession, R.DriveOldSession, R.DriveOldRemove, R.DriveOldForeign, R.DriveOff,
        R.LabelSet, R.LabelForeign, R.LabelRenamed, R.LabelOldRemove, R.LabelRestore,
        R.ShortcutCreate, R.ShortcutGone, R.ShortcutRetarget, R.ShortcutForeign, R.ShortcutOldRemove, R.ShortcutOff,
        R.DataCreate, R.DataLock,
        R.ShareAccessGrant, R.ShareAccessOther, R.ShareAccessDeny, R.ComputerSavedDiffers, R.ComputerPending,
    };

    public static Plan Setup(SystemSnapshot snapshot, SetupAnswers answers, ConfigDocument? config, ProtectedFolders protectedFolders)
    {
        var p = new Builder(snapshot, answers, config);
        p.Backup();
        var user = p.User();
        var path = p.Folder(protectedFolders, user);
        p.Share(path, user);
        p.Server();
        p.HardeningValues();
        p.Firewall();
        p.Networks();
        p.AutoPrivate();
        p.StaleTask();
        p.Drive(path);
        p.Shortcut(path);
        p.DataFolder();
        p.Computer();
        return new Plan(p.Items);
    }

    internal static class R
    {
        public const string NeedsAdmin = "Plan_R_NeedsAdmin";
        public const string ReadFailed = "Plan_R_ReadFailed";
        public const string Invalid = "Plan_R_Invalid";
        public const string AtTarget = "Plan_R_AtTarget";
        public const string Stricter = "Plan_R_Stricter";
        public const string BackupFresh = "Plan_R_BackupFresh";
        public const string BackupTaken = "Plan_R_BackupTaken";
        public const string BackupNone = "Plan_R_BackupNone";
        public const string AccountCreate = "Plan_R_AccountCreate";
        public const string AccountForeign = "Plan_R_AccountForeign";
        public const string AccountTakenOver = "Plan_R_AccountTakenOver";
        public const string AccountEnable = "Plan_R_AccountEnable";
        public const string NeverExpires = "Plan_R_NeverExpires";
        public const string Unlock = "Plan_R_Unlock";
        public const string UsersGroup = "Plan_R_UsersGroup";
        public const string Admin = "Plan_R_Admin";
        public const string AdminNew = "Plan_R_AdminNew";
        public const string Hidden = "Plan_R_Hidden";
        public const string PasswordKeep = "Plan_R_PasswordKeep";
        public const string PasswordSet = "Plan_R_PasswordSet";
        public const string PasswordNeeded = "Plan_R_PasswordNeeded";
        public const string FolderCreate = "Plan_R_FolderCreate";
        public const string AceGrant = "Plan_R_AceGrant";
        public const string AceAfterAccount = "Plan_R_AceAfterAccount";
        public const string ShareCreate = "Plan_R_ShareCreate";
        public const string ShareChange = "Plan_R_ShareChange";
        public const string ShareMove = "Plan_R_ShareMove";
        public const string ShareForeignName = "Plan_R_ShareForeignName";
        public const string ShareForeignPath = "Plan_R_ShareForeignPath";
        public const string ShareOld = "Plan_R_ShareOld";
        public const string AdminShares = "Plan_R_AdminShares";
        public const string ServerStart = "Plan_R_ServerStart";
        public const string Smb2 = "Plan_R_Smb2";
        public const string Reject = "Plan_R_Reject";
        public const string Smb1 = "Plan_R_Smb1";
        public const string Smb1Feature = "Plan_R_Smb1Feature";
        public const string Smb1FeatureGone = "Plan_R_Smb1FeatureGone";
        public const string Hardening = "Plan_R_Hardening";
        public const string Guest = "Plan_R_Guest";
        public const string NoGuest = "Plan_R_NoGuest";
        public const string LockoutSet = "Plan_R_LockoutSet";
        public const string LockoutKept = "Plan_R_LockoutKept";
        public const string Audit = "Plan_R_Audit";
        public const string RuleCreate = "Plan_R_RuleCreate";
        public const string RuleChange = "Plan_R_RuleChange";
        public const string RuleDuplicate = "Plan_R_RuleDuplicate";
        public const string RuleRemove = "Plan_R_RuleRemove";
        public const string RuleLegacy = "Plan_R_RuleLegacy";
        public const string VpnNone = "Plan_R_VpnNone";
        public const string VpnAdapter = "Plan_R_VpnAdapter";
        public const string VpnAdapterPublic = "Plan_R_VpnAdapterPublic";
        public const string BuiltInDisable = "Plan_R_BuiltInDisable";
        public const string BuiltInPortUnknown = "Plan_R_BuiltInPortUnknown";
        public const string BuiltInNone = "Plan_R_BuiltInNone";
        public const string NetworkPublic = "Plan_R_NetworkPublic";
        public const string NetworkPrivate = "Plan_R_NetworkPrivate";
        public const string AutoCreate = "Plan_R_AutoCreate";
        public const string AutoRemove = "Plan_R_AutoRemove";
        public const string AutoOff = "Plan_R_AutoOff";
        public const string LegacyTask = "Plan_R_LegacyTask";
        public const string StaleTask = "Plan_R_StaleTask";
        public const string DriveCreate = "Plan_R_DriveCreate";
        public const string DriveGone = "Plan_R_DriveGone";
        public const string DriveForeign = "Plan_R_DriveForeign";
        public const string DriveSession = "Plan_R_DriveSession";
        public const string DriveOldSession = "Plan_R_DriveOldSession";
        public const string DriveOldRemove = "Plan_R_DriveOldRemove";
        public const string DriveOldForeign = "Plan_R_DriveOldForeign";
        public const string DriveOff = "Plan_R_DriveOff";
        public const string LabelSet = "Plan_R_LabelSet";
        public const string LabelForeign = "Plan_R_LabelForeign";
        public const string LabelRenamed = "Plan_R_LabelRenamed";
        public const string LabelOldRemove = "Plan_R_LabelOldRemove";
        public const string LabelRestore = "Plan_R_LabelRestore";
        public const string ShortcutCreate = "Plan_R_ShortcutCreate";
        public const string ShortcutGone = "Plan_R_ShortcutGone";
        public const string ShortcutRetarget = "Plan_R_ShortcutRetarget";
        public const string ShortcutForeign = "Plan_R_ShortcutForeign";
        public const string ShortcutOldRemove = "Plan_R_ShortcutOldRemove";
        public const string ShortcutOff = "Plan_R_ShortcutOff";
        public const string DataCreate = "Plan_R_DataCreate";
        public const string DataLock = "Plan_R_DataLock";
        public const string ShareAccessGrant = "Plan_R_ShareAccessGrant";
        public const string ShareAccessOther = "Plan_R_ShareAccessOther";
        public const string ShareAccessDeny = "Plan_R_ShareAccessDeny";
        public const string ComputerSavedDiffers = "Plan_R_ComputerSavedDiffers";
        public const string ComputerPending = "Plan_R_ComputerPending";
    }

    // Word keys for values and subjects.
    internal static class W
    {
        public const string Yes = "Common_Yes";
        public const string No = "Common_No";
        public const string Absent = "Plan_V_Absent";
        public const string Exists = "Plan_V_Exists";
        public const string On = "Plan_V_On";
        public const string Off = "Plan_V_Off";
        public const string Public = "Plan_V_Public";
        public const string Private = "Plan_V_Private";
        public const string Domain = "Plan_V_Domain";
        public const string Automatic = "Plan_V_Automatic";
        public const string Manual = "Plan_V_Manual";
        public const string Disabled = "Plan_V_Disabled";
        public const string Enabled = "Plan_V_Enabled";
        public const string Locked = "Plan_V_Locked";
        public const string Unlocked = "Plan_V_Unlocked";
        public const string Member = "Plan_V_Member";
        public const string NotMember = "Plan_V_NotMember";
        public const string Modify = "Plan_V_Modify";
        public const string NoAccess = "Plan_V_NoAccess";
        public const string Locked2 = "Plan_V_LockedDown";
        public const string NotLocked = "Plan_V_NotLockedDown";
        public const string SetPassword = "Plan_V_SetPassword";
        public const string KeepPassword = "Plan_V_KeepPassword";
        public const string Taken = "Plan_V_Taken";
        public const string WillTake = "Plan_V_WillTake";
        public const string None = "Plan_V_None";
        public const string Success = "Plan_V_Success";
        public const string Failure = "Plan_V_Failure";
        public const string SuccessFailure = "Plan_V_SuccessFailure";
        public const string RightFull = "Plan_V_RightFull";
        public const string RightChange = "Plan_V_RightChange";
        public const string RightRead = "Plan_V_RightRead";
        public const string RightCustom = "Plan_V_RightCustom";
        public const string Denied = "Plan_V_Denied";
        public const string Visible = "Plan_V_Visible";
        public const string HiddenNow = "Plan_V_Hidden";
        public const string Strictest = "Plan_V_OnStrictest";
        public const string Level = "Plan_V_Level";
        public const string Value = "Plan_V_Value";
        public const string Unknown = "State_Unknown";
    }

    public static readonly IReadOnlyList<string> WordKeys = new[]
    {
        W.Absent, W.Exists, W.On, W.Off, W.Public, W.Private, W.Domain, W.Automatic, W.Manual, W.Disabled, W.Enabled, W.Locked,
        W.Unlocked, W.Member, W.NotMember, W.Modify, W.NoAccess, W.Locked2, W.NotLocked, W.SetPassword, W.KeepPassword,
        W.Taken, W.WillTake, W.None, W.Success, W.Failure, W.SuccessFailure,
        W.RightFull, W.RightChange, W.RightRead, W.RightCustom, W.Denied, W.Visible, W.HiddenNow, W.Strictest, W.Level, W.Value,
        F.EncryptData, F.FolderEnumerationMode, F.CachingMode, F.Description, F.Enabled, F.Direction, F.Action, F.Protocol,
        F.LocalPorts, F.Profiles, F.RemoteAddresses, F.Interfaces,
        S.Backup, S.Account, S.TakeOver, S.AccountEnabled, S.NeverExpires, S.Lockout, S.Users, S.Admins, S.Hidden, S.Password,
        S.Folder, S.Ace, S.Share, S.AdminShares, S.ServerStart, S.Smb2, S.Reject, S.Smb1, S.Smb1Feature, S.Guest,
        S.LockoutThreshold, S.Audit, S.BuiltIn, S.VpnAdapter, S.Drive, S.OldDrive, S.DriveSession, S.OldDriveSession, S.Label,
        S.OldLabel, S.Shortcut, S.OldShortcut, S.DataFolder, S.ShareAccess, S.ComputerName, S.SavedComputerName,
    };

    // A setting of a share or a firewall rule that differs from Setup's, by the field name the planner reports.
    internal static class F
    {
        public const string EncryptData = "Plan_F_EncryptData";
        public const string FolderEnumerationMode = "Plan_F_FolderEnumerationMode";
        public const string CachingMode = "Plan_F_CachingMode";
        public const string Description = "Plan_F_Description";
        public const string Enabled = "Plan_F_Enabled";
        public const string Direction = "Plan_F_Direction";
        public const string Action = "Plan_F_Action";
        public const string Protocol = "Plan_F_Protocol";
        public const string LocalPorts = "Plan_F_LocalPorts";
        public const string Profiles = "Plan_F_Profiles";
        public const string RemoteAddresses = "Plan_F_RemoteAddresses";
        public const string Interfaces = "Plan_F_Interfaces";
    }

    internal static class S
    {
        public const string Backup = "Plan_S_Backup";
        public const string Account = "Plan_S_Account";
        public const string TakeOver = "Plan_S_TakeOver";
        public const string AccountEnabled = "Plan_S_AccountEnabled";
        public const string NeverExpires = "Plan_S_NeverExpires";
        public const string Lockout = "Plan_S_AccountLockout";
        public const string Users = "Plan_S_Users";
        public const string Admins = "Plan_S_Admins";
        public const string Hidden = "Plan_S_Hidden";
        public const string Password = "Plan_S_Password";
        public const string Folder = "Plan_S_Folder";
        public const string Ace = "Plan_S_Ace";
        public const string Share = "Plan_S_Share";
        public const string AdminShares = "Plan_S_AdminShares";
        public const string ServerStart = "Plan_S_ServerStart";
        public const string Smb2 = "Plan_S_Smb2";
        public const string Reject = "Plan_S_Reject";
        public const string Smb1 = "Plan_S_Smb1";
        public const string Smb1Feature = "Plan_S_Smb1Feature";
        public const string Guest = "Plan_S_Guest";
        public const string LockoutThreshold = "Plan_S_LockoutThreshold";
        public const string Audit = "Plan_S_Audit";
        public const string BuiltIn = "Plan_S_BuiltIn";
        public const string VpnAdapter = "Plan_S_VpnAdapter";
        public const string Drive = "Plan_S_Drive";
        public const string OldDrive = "Plan_S_OldDrive";
        public const string DriveSession = "Plan_S_DriveSession";
        public const string OldDriveSession = "Plan_S_OldDriveSession";
        public const string Label = "Plan_S_Label";
        public const string OldLabel = "Plan_S_OldLabel";
        public const string Shortcut = "Plan_S_Shortcut";
        public const string OldShortcut = "Plan_S_OldShortcut";
        public const string DataFolder = "Plan_S_DataFolder";
        public const string ShareAccess = "Plan_S_ShareAccess";
        public const string ComputerName = "Plan_S_ComputerName";
        public const string SavedComputerName = "Plan_S_SavedComputerName";
    }

    private sealed class Builder
    {
        private readonly SystemSnapshot _s;
        private readonly SetupAnswers _a;
        private readonly ConfigDocument? _c;

        public Builder(SystemSnapshot snapshot, SetupAnswers answers, ConfigDocument? config)
        {
            _s = snapshot;
            _a = answers;
            _c = config;
        }

        public List<PlanItem> Items { get; } = new();

        // The bulk backup is taken only when no config.json exists (the scripts' rule): later its values may already
        // be CPFS's own.
        private bool FreshInstall => _c is null;

        public void Backup()
        {
            if (FreshInstall)
            {
                Add("backup", PlanSection.Backup, PlanAction.Create, Word(S.Backup), Word(W.Absent), Word(W.WillTake), R.BackupFresh);
            }
            else if (_c!.Backup == BackupState.Present)
            {
                Add("backup", PlanSection.Backup, PlanAction.Keep, Word(S.Backup), Word(W.Taken), null, R.BackupTaken, _c.BackupTaken ?? "?");
            }
            else
            {
                Add("backup", PlanSection.Backup, PlanAction.Keep, Word(S.Backup), Word(W.Absent), null, R.BackupNone);
            }
        }

        public UserPlan User()
        {
            var name = Validators.SmbUserName(_a.SmbUser, _s.WindowsUserNames, _s.ComputerName);
            if (!name.IsValid)
            {
                Invalid("user.account", PlanSection.User, Word(S.Account), _a.SmbUser, "SmbUser");
                return UserPlan.NotUsable;
            }

            var user = name.Value!;
            if (!_s.User.IsOk)
            {
                Unread("user.account", PlanSection.User, Word(S.Account), _s.User.State, _s.User.Error);
                return UserPlan.NotUsable;
            }

            var info = _s.User.Value;
            if (info is null)
            {
                // Each property is planned and checked on its own: what NetUserAdd gives a new account by default
                // differs between Windows editions (Server 2025 leaves it out of Users).
                Add("user.account", PlanSection.User, PlanAction.Create, Word(S.Account), Word(W.Absent), Data(user), R.AccountCreate);
                Add("user.enabled", PlanSection.User, PlanAction.Create, Word(S.AccountEnabled), Word(W.Absent), Word(W.Enabled), R.AccountEnable);
                Add("user.neverexpires", PlanSection.User, PlanAction.Create, Word(S.NeverExpires), Word(W.Absent), Word(W.Yes), R.NeverExpires);
                Add("user.users", PlanSection.User, PlanAction.Create, Word(S.Users), Word(W.Absent), Word(W.Member), R.UsersGroup);
                Add("user.admin", PlanSection.User, PlanAction.Create, Word(S.Admins), Word(W.Absent), Word(W.NotMember), R.AdminNew);
                Add("user.hidden", PlanSection.User, PlanAction.Create, Word(S.Hidden), Word(W.Visible), Word(W.HiddenNow), R.Hidden);
                PasswordItem(newAccount: true);
                return UserPlan.WillCreate;
            }

            var tracked = IsTrackedUser(user);
            var takeOver = false;
            if (!tracked)
            {
                var ticked = _a.IsTicked("user.takeover");
                Add("user.takeover", PlanSection.User, PlanAction.Ask, Word(S.TakeOver), Data(user), null, R.AccountForeign, user);
                Items[Items.Count - 1] = Items[Items.Count - 1] with { Ticked = ticked, BackupNeeded = ticked };
                if (!ticked)
                {
                    return UserPlan.NotUsable;
                }

                takeOver = true;
            }
            else
            {
                Add("user.account", PlanSection.User, PlanAction.Keep, Word(S.Account), Data(user), null, R.AccountTakenOver);
            }

            Bool("user.enabled", PlanSection.User, Word(S.AccountEnabled), info.Enabled, true, W.Enabled, W.Disabled, R.AccountEnable, takeOver);
            Bool("user.neverexpires", PlanSection.User, Word(S.NeverExpires), info.PasswordNeverExpires, true, W.Yes, W.No, R.NeverExpires, takeOver);
            Bool("user.unlock", PlanSection.User, Word(S.Lockout), !info.Locked, true, W.Unlocked, W.Locked, R.Unlock, false);
            if (info.InUsers is { } inUsers)
            {
                Bool("user.users", PlanSection.User, Word(S.Users), inUsers, true, W.Member, W.NotMember, R.UsersGroup, takeOver);
            }
            else
            {
                Unread("user.users", PlanSection.User, Word(S.Users), ReadState.Failed, null);
            }

            if (info.IsAdmin == true)
            {
                // Taking admin rights away is the scripts' recommended answer, so it starts ticked.
                var ticked = _a.IsTicked("user.admin", true);
                Items.Add(new PlanItem
                {
                    Key = "user.admin", Section = PlanSection.User, Action = PlanAction.Ask, Subject = Word(S.Admins),
                    Current = Word(W.Member), Target = Word(W.NotMember), Ticked = ticked, BackupNeeded = ticked && takeOver,
                    ReasonKey = R.Admin,
                });
            }

            var hiddenOk = info.HiddenValue == 0;
            Add("user.hidden", PlanSection.User, hiddenOk ? PlanAction.Keep : PlanAction.Change, Word(S.Hidden),
                HiddenWord(info.HiddenValue), hiddenOk ? null : Word(W.HiddenNow), hiddenOk ? R.AtTarget : R.Hidden);
            if (!hiddenOk && takeOver)
            {
                MarkBackup();
            }

            PasswordItem(newAccount: false);
            return UserPlan.Usable;
        }

        private void PasswordItem(bool newAccount)
        {
            switch (_a.Password)
            {
                case PasswordEntry.Empty when newAccount:
                    Items.Add(Cannot("user.password", PlanSection.User, Word(S.Password), PlanCause.InvalidAnswer, R.PasswordNeeded));
                    break;
                case PasswordEntry.Empty:
                    Add("user.password", PlanSection.User, PlanAction.Keep, Word(S.Password), null, Word(W.KeepPassword), R.PasswordKeep);
                    break;
                case PasswordEntry.Valid:
                    Add("user.password", PlanSection.User, newAccount ? PlanAction.Create : PlanAction.Change, Word(S.Password), null, Word(W.SetPassword), R.PasswordSet);
                    break;
                default:
                    Items.Add(Cannot("user.password", PlanSection.User, Word(S.Password), PlanCause.InvalidAnswer, R.Invalid, "Password"));
                    break;
            }
        }

        private bool IsTrackedUser(string name)
        {
            foreach (var t in _c?.TrackedUsers ?? Array.Empty<TrackedUser>())
            {
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // A 0.1 install has no Tracked; its account is CPFS's when it is the configured one.
            return _c is { Shape: ConfigShape.V01 } && string.Equals(_c.SmbUser, name, StringComparison.OrdinalIgnoreCase);
        }

        public string? Folder(ProtectedFolders protectedFolders, UserPlan user)
        {
            var path = Validators.SharePath(_a.SharePath, protectedFolders);
            if (!path.IsValid)
            {
                Invalid("folder.exists", PlanSection.Folder, Word(S.Folder), _a.SharePath, "SharePath");
                return null;
            }

            var p = path.Value!;
            if (!_s.Folder.IsOk)
            {
                Unread("folder.exists", PlanSection.Folder, Word(S.Folder), _s.Folder.State, _s.Folder.Error);
                return p;
            }

            var f = _s.Folder.Value!;
            Add("folder.exists", PlanSection.Folder, f.Exists ? PlanAction.Keep : PlanAction.Create, Data(p),
                f.Exists ? Word(W.Exists) : Word(W.Absent), f.Exists ? null : Word(W.Exists), f.Exists ? R.AtTarget : R.FolderCreate);
            if (user == UserPlan.NotUsable)
            {
                // The account item already says why; its access follows once it can be planned.
            }
            else if (user == UserPlan.WillCreate)
            {
                Add("folder.ace", PlanSection.Folder, PlanAction.Create, Word(S.Ace), Word(W.NoAccess), Word(W.Modify), R.AceAfterAccount, _a.SmbUser.Trim());
            }
            else if (f.Exists && f.UserHasModify)
            {
                Add("folder.ace", PlanSection.Folder, PlanAction.Keep, Word(S.Ace), Word(W.Modify), null, R.AtTarget);
            }
            else
            {
                Add("folder.ace", PlanSection.Folder, PlanAction.Change, Word(S.Ace), Word(W.NoAccess), Word(W.Modify), R.AceGrant, _a.SmbUser.Trim());
            }

            return p;
        }

        public void Share(string? path, UserPlan user)
        {
            var name = Validators.ShareName(_a.ShareName);
            if (!name.IsValid)
            {
                Invalid("share.main", PlanSection.Share, Word(S.Share), _a.ShareName, "ShareName");
                return;
            }

            if (!_s.Shares.IsOk)
            {
                Unread("share.main", PlanSection.Share, Word(S.Share), _s.Shares.State, _s.Shares.Error);
                return;
            }

            var shareName = name.Value!;
            var shares = _s.Shares.Value!;
            var configName = _c?.ShareName;
            var configPath = Trim(_c?.SharePath);
            var main = FindShare(shares, shareName);
            if (path is null)
            {
                Items.Add(Cannot("share.main", PlanSection.Share, Data(shareName), PlanCause.InvalidAnswer, R.Invalid, "SharePath"));
            }
            else if (main is null)
            {
                Add("share.main", PlanSection.Share, PlanAction.Create, Data(shareName), Word(W.Absent), Data(path), R.ShareCreate);
            }
            else
            {
                // CPFS's own: the share config.json recorded (name and folder), or one with CPFS's description.
                var ours = (SameText(main.Name, configName) && SamePath(main.Path, configPath))
                    || string.Equals(main.Description, ShareDescription, StringComparison.Ordinal);
                var samePath = SamePath(main.Path, path);
                if (!ours)
                {
                    AskItem("share.foreign:" + main.Name, PlanSection.Share, Data(main.Name), Data(main.Path ?? "?"), Data(path), R.ShareForeignName, main.Name, main.Path ?? "?");
                    if (Items[Items.Count - 1].Ticked)
                    {
                        // Ticked: the foreign share is removed and CPFS's own created in its place.
                        Add("share.main", PlanSection.Share, PlanAction.Create, Data(shareName), Word(W.Absent), Data(path), R.ShareCreate);
                    }
                }
                else if (!samePath)
                {
                    Add("share.main", PlanSection.Share, PlanAction.Change, Data(shareName), Data(main.Path ?? "?"), Data(path), R.ShareMove);
                }
                else
                {
                    var diff = new List<string>();
                    if (main.EncryptData != _a.Strict)
                    {
                        diff.Add("EncryptData");
                    }

                    if (main.FolderEnumerationMode != 0)
                    {
                        diff.Add("FolderEnumerationMode");
                    }

                    if (main.CachingMode != 0)
                    {
                        diff.Add("CachingMode");
                    }

                    if (!string.Equals(main.Description, ShareDescription, StringComparison.Ordinal))
                    {
                        diff.Add("Description");
                    }

                    if (diff.Count == 0)
                    {
                        Add("share.main", PlanSection.Share, PlanAction.Keep, Data(shareName), Data(path), null, R.AtTarget);
                    }
                    else
                    {
                        Add("share.main", PlanSection.Share, PlanAction.Change, Data(shareName), Data(path), FieldWords(diff), R.ShareChange, string.Join(", ", diff));
                    }

                    // A share that is created or moved gets the SMB account as its only entry, so only a share kept
                    // in place has permissions to compare.
                    ShareAccess(user);
                }
            }

            foreach (var share in shares)
            {
                if (share.Special || SameText(share.Name, shareName))
                {
                    continue;
                }

                if (configName is not null && SameText(share.Name, configName) && SamePath(share.Path, configPath))
                {
                    Add("share.old:" + share.Name, PlanSection.Share, PlanAction.RemoveIfOurs, Data(share.Name), Data(share.Path ?? "?"), Word(W.Absent), R.ShareOld);
                }
                else if (path is not null && SamePath(share.Path, path))
                {
                    AskItem("share.foreign:" + share.Name, PlanSection.Share, Data(share.Name), Data(share.Path ?? "?"), Word(W.Absent), R.ShareForeignPath, share.Name, share.Path ?? "?");
                }
            }

            var adminShares = new List<string>();
            foreach (var share in shares)
            {
                if (IsAdminShare(share.Name))
                {
                    adminShares.Add(share.Name);
                }
            }

            if (adminShares.Count > 0)
            {
                AskItem("share.adminshares", PlanSection.Share, Word(S.AdminShares), Data(string.Join(" ", adminShares)), Word(W.Absent), R.AdminShares);
            }
        }

        private void ShareAccess(UserPlan user)
        {
            if (user == UserPlan.NotUsable)
            {
                // The account item says why; without the account, no entry can be told apart as its own.
                return;
            }

            if (!_s.ShareAccess.IsOk)
            {
                Unread("share.access", PlanSection.Share, Word(S.ShareAccess), _s.ShareAccess.State, _s.ShareAccess.Error);
                return;
            }

            var entries = _s.ShareAccess.Value ?? Array.Empty<ShareAccessEntry>();
            var userName = _a.SmbUser.Trim();
            var userSid = user == UserPlan.Usable ? _s.User.Value?.Sid : null;
            if (user == UserPlan.WillCreate)
            {
                Add("share.access", PlanSection.Share, PlanAction.Create, Word(S.ShareAccess), Word(W.NoAccess), Word(W.RightFull), R.ShareAccessGrant, userName);
            }
            else
            {
                ShareAccessEntry? own = null;
                foreach (var e in entries)
                {
                    if (SameText(e.Sid, userSid) && e.ControlType == ShareAllow && (own is null || e.Right == ShareFull))
                    {
                        own = e;
                    }
                }

                if (own is { Right: ShareFull })
                {
                    Add("share.access", PlanSection.Share, PlanAction.Keep, Word(S.ShareAccess), Word(W.RightFull), null, R.AtTarget);
                }
                else
                {
                    Add("share.access", PlanSection.Share, PlanAction.Change, Word(S.ShareAccess), own is null ? Word(W.NoAccess) : RightWord(own.Right),
                        Word(W.RightFull), R.ShareAccessGrant, userName);
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                var isUser = SameText(e.Sid, userSid);
                var deny = e.ControlType != ShareAllow;

                // A deny entry for someone else only narrows access, so it stays.
                if (isUser != deny || !seen.Add(e.Sid ?? e.Account))
                {
                    continue;
                }

                AskItem("share.access:" + (e.Sid ?? e.Account), PlanSection.Share, Data(e.Account), deny ? Word(W.Denied) : RightWord(e.Right), Word(W.Absent),
                    isUser ? R.ShareAccessDeny : R.ShareAccessOther, e.Account);
            }
        }

        public void Server()
        {
            if (_s.ServerStart.IsOk)
            {
                var start = _s.ServerStart.Value;
                var keep = start == 2;
                Add("server.start", PlanSection.Server, keep ? PlanAction.Keep : PlanAction.Change, Word(S.ServerStart), StartWord(start),
                    keep ? null : Word(W.Automatic), keep ? R.AtTarget : R.ServerStart);
                if (!keep)
                {
                    MarkBackup(FreshInstall);
                }
            }
            else
            {
                Unread("server.start", PlanSection.Server, Word(S.ServerStart), _s.ServerStart.State, _s.ServerStart.Error);
            }

            if (_s.SmbServer.IsOk)
            {
                var smb = _s.SmbServer.Value!;
                ServerBool("server.smb2", Word(S.Smb2), smb.Smb2, true, R.Smb2, FreshInstall);
                ServerBool("server.reject", Word(S.Reject), smb.RejectUnencrypted, true, R.Reject, FreshInstall);
                ServerBool("server.smb1", Word(S.Smb1), smb.Smb1, false, R.Smb1, false);
            }
            else
            {
                Unread("server.smb2", PlanSection.Server, Word(S.Smb2), _s.SmbServer.State, _s.SmbServer.Error);
                Unread("server.reject", PlanSection.Server, Word(S.Reject), _s.SmbServer.State, _s.SmbServer.Error);
                Unread("server.smb1", PlanSection.Server, Word(S.Smb1), _s.SmbServer.State, _s.SmbServer.Error);
            }

            if (_s.Smb1Feature.IsOk)
            {
                var state = _s.Smb1Feature.Value;
                if (state == 1)
                {
                    Add("server.smb1feature", PlanSection.Server, PlanAction.Change, Word(S.Smb1Feature), Word(W.Enabled), Word(W.Disabled), R.Smb1Feature);
                }
                else
                {
                    Add("server.smb1feature", PlanSection.Server, PlanAction.Keep, Word(S.Smb1Feature), state == 2 ? Word(W.Disabled) : Word(W.Absent), null, R.Smb1FeatureGone);
                }
            }
            else
            {
                Unread("server.smb1feature", PlanSection.Server, Word(S.Smb1Feature), _s.Smb1Feature.State, _s.Smb1Feature.Error);
            }
        }

        private void ServerBool(string key, PlanValue subject, bool? current, bool target, string reason, bool backup)
        {
            if (current is null)
            {
                Unread(key, PlanSection.Server, subject, ReadState.Failed, null);
                return;
            }

            var keep = current == target;
            Add(key, PlanSection.Server, keep ? PlanAction.Keep : PlanAction.Change, subject, Word(current.Value ? W.On : W.Off),
                keep ? null : Word(target ? W.On : W.Off), keep ? R.AtTarget : reason);
            if (!keep)
            {
                MarkBackup(backup);
            }
        }

        public void HardeningValues()
        {
            foreach (var h in Hardening)
            {
                var key = "hardening." + h.Name;
                if (!_s.Hardening.TryGetValue(h.Name, out var reading) || !reading.IsOk)
                {
                    Unread(key, PlanSection.Hardening, Data(h.Name), reading?.State ?? ReadState.Failed, reading?.Error);
                    continue;
                }

                var current = reading.Value;
                var currentValue = HardeningWord(h.Name, current);
                if (current == h.Target)
                {
                    Add(key, PlanSection.Hardening, PlanAction.Keep, Data(h.Name), currentValue, null, R.AtTarget);
                }
                else if (current is { } c && h.IsStricter(c))
                {
                    Add(key, PlanSection.Hardening, PlanAction.Keep, Data(h.Name), currentValue, null, R.Stricter, h.Target.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    Add(key, PlanSection.Hardening, PlanAction.Change, Data(h.Name), currentValue, HardeningWord(h.Name, h.Target), R.Hardening);
                    MarkBackup(FreshInstall);
                }
            }

            if (!_s.GuestEnabled.IsOk)
            {
                Unread("hardening.guest", PlanSection.Hardening, Word(S.Guest), _s.GuestEnabled.State, _s.GuestEnabled.Error);
            }
            else if (_s.GuestEnabled.Value is null)
            {
                Add("hardening.guest", PlanSection.Hardening, PlanAction.Keep, Word(S.Guest), Word(W.Absent), null, R.NoGuest);
            }
            else
            {
                var enabled = _s.GuestEnabled.Value.Value;
                Add("hardening.guest", PlanSection.Hardening, enabled ? PlanAction.Change : PlanAction.Keep, Word(S.Guest),
                    Word(enabled ? W.Enabled : W.Disabled), enabled ? Word(W.Disabled) : null, enabled ? R.Guest : R.AtTarget);
            }

            if (!_s.Lockout.IsOk)
            {
                Unread("hardening.lockout", PlanSection.Hardening, Word(S.LockoutThreshold), _s.Lockout.State, _s.Lockout.Error);
            }
            else if (_s.Lockout.Value!.Threshold == 0)
            {
                // The scripts' Backup.Lockout is null when they could not read it; a threshold of 0 now was never
                // changed by them (they set 10 only when they read 0), so it is the original and is backed up.
                Add("hardening.lockout", PlanSection.Hardening, PlanAction.Change, Word(S.LockoutThreshold), LockoutWord(0), LockoutWord(LockoutThreshold), R.LockoutSet);
                MarkBackup(FreshInstall || (_c!.Backup == BackupState.Present && _c.BackupLockout is null));
            }
            else
            {
                Add("hardening.lockout", PlanSection.Hardening, PlanAction.Keep, Word(S.LockoutThreshold), LockoutWord(_s.Lockout.Value.Threshold), null, R.LockoutKept);
            }

            if (!_s.AuditLogon.IsOk)
            {
                Unread("hardening.audit", PlanSection.Hardening, Word(S.Audit), _s.AuditLogon.State, _s.AuditLogon.Error);
            }
            else
            {
                var audit = _s.AuditLogon.Value!;
                var current = Word(audit.Success && audit.Failure ? W.SuccessFailure : audit.Failure ? W.Failure : audit.Success ? W.Success : W.None);
                var target = Word(audit.Success ? W.SuccessFailure : W.Failure);
                Add("hardening.audit", PlanSection.Hardening, audit.Failure ? PlanAction.Keep : PlanAction.Change, Word(S.Audit), current,
                    audit.Failure ? null : target, audit.Failure ? R.AtTarget : R.Audit);
                if (!audit.Failure)
                {
                    // The scripts set this on every run without recording it, so only a first Setup knows the original.
                    MarkBackup(FreshInstall);
                }
            }
        }

        public void Firewall()
        {
            var vpn = VpnInterfaces();
            if (!_s.OwnRules.IsOk)
            {
                Unread("fw.rules", PlanSection.Firewall, Data(FirewallRules.LanRule), _s.OwnRules.State, _s.OwnRules.Error);
            }
            else
            {
                var rules = _s.OwnRules.Value!;
                var wanted = FirewallRules.Wanted(vpn);
                foreach (var spec in wanted)
                {
                    var matches = rules.FindAll(r => SameText(r.Name, spec.Name));
                    var key = "fw.rule:" + spec.Name;
                    if (matches.Count == 0)
                    {
                        Add(key, PlanSection.Firewall, PlanAction.Create, Data(spec.Name), Word(W.Absent), Word(W.Exists), R.RuleCreate);
                    }
                    else if (matches.Count > 1)
                    {
                        Add(key, PlanSection.Firewall, PlanAction.Change, Data(spec.Name), PlanValue.Of(matches.Count), PlanValue.Of(1), R.RuleDuplicate);
                    }
                    else
                    {
                        var diff = FirewallRules.Differences(spec, matches[0]);
                        if (diff.Count == 0)
                        {
                            Add(key, PlanSection.Firewall, PlanAction.Keep, Data(spec.Name), Word(W.Exists), null, R.AtTarget);
                        }
                        else
                        {
                            Add(key, PlanSection.Firewall, PlanAction.Change, Data(spec.Name), FieldWords(diff), null, R.RuleChange, string.Join(", ", diff));
                        }
                    }
                }

                if (vpn.Count == 0)
                {
                    var exists = rules.Exists(r => SameText(r.Name, FirewallRules.VpnRule));
                    Add("fw.rule:" + FirewallRules.VpnRule, PlanSection.Firewall, exists ? PlanAction.RemoveIfOurs : PlanAction.Keep,
                        Data(FirewallRules.VpnRule), Word(exists ? W.Exists : W.Absent), exists ? Word(W.Absent) : null, R.VpnNone);
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rule in rules)
                {
                    if (!seen.Add(rule.Name) || wanted.Exists(w => SameText(w.Name, rule.Name)) || SameText(rule.Name, FirewallRules.VpnRule))
                    {
                        continue;
                    }

                    var legacy = rule.Name.StartsWith("CyShare", StringComparison.OrdinalIgnoreCase);
                    Add("fw.extra:" + rule.Name, PlanSection.Firewall, PlanAction.RemoveIfOurs, Data(rule.Name), Word(W.Exists), Word(W.Absent), legacy ? R.RuleLegacy : R.RuleRemove);
                }
            }

            if (!_s.BuiltInRules.IsOk)
            {
                Unread("fw.builtin", PlanSection.Firewall, Word(S.BuiltIn), _s.BuiltInRules.State, _s.BuiltInRules.Error);
                return;
            }

            var any = false;
            foreach (var rule in _s.BuiltInRules.Value!)
            {
                var exposed = rule.Enabled && rule.Direction == FirewallRules.Inbound
                    && (FirewallRules.IsAllProfiles(rule.Profiles) || (rule.Profiles & FirewallRules.ProfilePublic) != 0);
                if (!exposed)
                {
                    continue;
                }

                if (rule.LocalPorts is null)
                {
                    any = true;
                    Items.Add(Cannot("fw.builtin:" + rule.Id, PlanSection.Firewall, Data(rule.Id), PlanCause.ReadFailed, R.BuiltInPortUnknown));
                }
                else if (FirewallRules.HasPort(rule.LocalPorts, "445"))
                {
                    any = true;
                    Add("fw.builtin:" + rule.Id, PlanSection.Firewall, PlanAction.Change, Data(rule.Id), Word(W.Enabled), Word(W.Disabled), R.BuiltInDisable);
                }
            }

            if (!any)
            {
                Add("fw.builtin", PlanSection.Firewall, PlanAction.Keep, Word(S.BuiltIn), Word(W.None), null, R.BuiltInNone);
            }
        }

        // An adapter with a VPN address is used when its network is not Public, or when the user ticks it.
        private List<string> VpnInterfaces()
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var adapter in _s.VpnAdapters)
            {
                if (!seen.Add(adapter.Name))
                {
                    continue;
                }

                int? category = null;
                foreach (var n in _s.Networks.Value ?? Array.Empty<NetworkInfo>())
                {
                    if (SameText(n.InterfaceAlias, adapter.Name))
                    {
                        category = n.Category;
                    }
                }

                var key = "fw.vpnadapter:" + adapter.Name;
                if (category is null or 0)
                {
                    var ticked = _a.IsTicked(key);
                    AskItem(key, PlanSection.Firewall, Data(adapter.Name), Data(adapter.Address), null, R.VpnAdapterPublic, adapter.Name, adapter.Address);
                    if (ticked)
                    {
                        list.Add(adapter.Name);
                    }
                }
                else
                {
                    Add(key, PlanSection.Firewall, PlanAction.Keep, Data(adapter.Name), Data(adapter.Address), null, R.VpnAdapter, adapter.Name);
                    list.Add(adapter.Name);
                }
            }

            return list;
        }

        public void Networks()
        {
            if (!_s.Networks.IsOk)
            {
                Unread("net", PlanSection.Networks, Data("-"), _s.Networks.State, _s.Networks.Error);
                return;
            }

            foreach (var n in _s.Networks.Value!)
            {
                var key = "net:" + n.Name;
                if (n.Category == 0)
                {
                    AskItem(key, PlanSection.Networks, Data(n.Name), Word(W.Public), Word(W.Private), R.NetworkPublic, n.InterfaceAlias ?? "?");
                }
                else
                {
                    Add(key, PlanSection.Networks, PlanAction.Keep, Data(n.Name), Word(n.Category == 2 ? W.Domain : W.Private), null, R.NetworkPrivate);
                }
            }
        }

        public void AutoPrivate()
        {
            if (!_s.Tasks.IsOk)
            {
                Unread("auto.task", PlanSection.AutoPrivate, Data(AutoPrivateTask), _s.Tasks.State, _s.Tasks.Error);
                return;
            }

            var tasks = _s.Tasks.Value!;
            var exists = tasks.TryGetValue(AutoPrivateTask, out var e) && e;
            if (_a.AutoPrivate)
            {
                Add("auto.task", PlanSection.AutoPrivate, exists ? PlanAction.Keep : PlanAction.Create, Data(AutoPrivateTask),
                    Word(exists ? W.Exists : W.Absent), exists ? null : Word(W.Exists), exists ? R.AtTarget : R.AutoCreate);
            }
            else
            {
                Add("auto.task", PlanSection.AutoPrivate, exists ? PlanAction.RemoveIfOurs : PlanAction.Keep, Data(AutoPrivateTask),
                    Word(exists ? W.Exists : W.Absent), exists ? Word(W.Absent) : null, exists ? R.AutoRemove : R.AutoOff);
            }

            if (tasks.TryGetValue(LegacyAutoPrivateTask, out var legacy) && legacy)
            {
                Add("auto.legacy", PlanSection.AutoPrivate, PlanAction.RemoveIfOurs, Data(LegacyAutoPrivateTask), Word(W.Exists), Word(W.Absent), R.LegacyTask);
            }

        }

        // A CPFS-RunAsUser task in the root folder is a leftover (from a run that stopped, or the 0.2.x scripts):
        // CPFS's own exists only inside the drive.session step. It goes with the drive, not with Auto-Private.
        public void StaleTask()
        {
            if (_s.Tasks.IsOk && _s.Tasks.Value!.TryGetValue(RunAsUserTask, out var stale) && stale)
            {
                Add("task.runasuser", PlanSection.Drive, PlanAction.RemoveIfOurs, Data(RunAsUserTask), Word(W.Exists), Word(W.Absent), R.StaleTask);
            }
        }

        // The drive letter: the DOS Devices value (every session, after a restart), the same mapping in the desktop
        // user's session now (drive.session, through the CPFS-RunAsUser task), and the name Explorer shows. The old
        // letter's mapping and name go first, and only when they are still what CPFS recorded.
        public void Drive(string? path)
        {
            var oldLetter = _c?.DriveLetter is { Length: 1 } l && IsAsciiLetter(l[0]) ? char.ToUpperInvariant(l[0]) : (char?)null;
            var ownPath = _c?.OwnDrivePath;
            var ownTarget = ownPath is { Length: > 0 } ? DosTarget(ownPath) : null;
            if (!_s.DosDevices.IsOk)
            {
                if (_a.Drive || oldLetter is not null)
                {
                    Unread("drive.map", PlanSection.Drive, Word(S.Drive), _s.DosDevices.State, _s.DosDevices.Error);
                }

                return;
            }

            var dos = _s.DosDevices.Value!;
            var labels = _s.DriveLabels.Value;
            char? letter = null;
            string? target = null;
            var label = Validators.DriveName(_a.DriveName);
            var invalid = false;
            if (_a.Drive)
            {
                var check = Validators.DriveLetter(_a.DriveLetter?.ToString(), _s.Letters.Free(oldLetter));
                if (!check.IsValid)
                {
                    Invalid("drive.map", PlanSection.Drive, Word(S.Drive), _a.DriveLetter?.ToString() ?? "", "DriveLetter");
                    invalid = true;
                }
                else if (path is null)
                {
                    Items.Add(Cannot("drive.map", PlanSection.Drive, Word(S.Drive), PlanCause.InvalidAnswer, R.Invalid, "SharePath"));
                    invalid = true;
                }
                else
                {
                    letter = check.Value![0];
                    target = DosTarget(path);
                }
            }

            var oldValue = oldLetter is { } o && dos.TryGetValue(o, out var ov) ? ov : null;
            var oldIsOurs = oldValue is not null && SameText(oldValue, ownTarget);

            // CPFS's own letter is taken again only while nothing but CPFS's mapping holds it.
            var foreign = letter is { } own && own == oldLetter
                && !_s.Letters.IsHeldOnlyBy(own, ownTarget is null ? new[] { target! } : new[] { ownTarget, target! });
            var removeOld = !invalid && !foreign && oldIsOurs && (letter != oldLetter || !SameText(oldValue, target));

            if (!invalid && !foreign && oldLetter is { } ol && letter != ol && labels is not null && labels.TryGetValue(ol, out var oldLabel))
            {
                var original = _c!.BackupDriveLabel(ol);
                if (original is not null && !string.Equals(oldLabel, original, StringComparison.Ordinal) && SameText(oldLabel, _c.OwnDriveLabel))
                {
                    Add("drive.oldlabel", PlanSection.Drive, PlanAction.Change, Word(S.OldLabel), Data(oldLabel), Data(original), R.LabelRestore);
                }
                else if (original is null && SameText(oldLabel, _c.OwnDriveLabel))
                {
                    Add("drive.oldlabel", PlanSection.Drive, PlanAction.RemoveIfOurs, Word(S.OldLabel), Data(oldLabel), Word(W.Absent), R.LabelOldRemove);
                }
            }

            if (removeOld)
            {
                var shown = oldLetter + ": " + oldValue;
                Add("drive.oldsession", PlanSection.Drive, PlanAction.RemoveIfOurs, Word(S.OldDriveSession), Data(shown), Word(W.Absent), R.DriveOldSession);
                Add("drive.oldmap", PlanSection.Drive, PlanAction.RemoveIfOurs, Word(S.OldDrive), Data(shown), Word(W.Absent), R.DriveOldRemove);
            }
            else if (!invalid && oldLetter is { } of && letter != of && oldValue is not null && !oldIsOurs)
            {
                Add("drive.oldmap", PlanSection.Drive, PlanAction.Keep, Word(S.OldDrive), Data(of + ": " + oldValue), null, R.DriveOldForeign);
            }

            if (!_a.Drive)
            {
                if (oldLetter is null)
                {
                    Add("drive.map", PlanSection.Drive, PlanAction.Keep, Word(S.Drive), Word(W.None), null, R.DriveOff);
                }

                return;
            }

            if (letter is not { } d)
            {
                return;
            }

            var current = dos.TryGetValue(d, out var cv) ? cv : null;
            var mapping = d + ": " + target;
            var willMap = false;
            if (foreign || (current is not null && !SameText(current, target) && !(d == oldLetter && oldIsOurs)))
            {
                Items.Add(Cannot("drive.map", PlanSection.Drive, Word(S.Drive), PlanCause.InvalidAnswer, R.DriveForeign, d + ":") with { Current = Data(current ?? d + ":") });
                return;
            }

            if (SameText(current, target))
            {
                Add("drive.map", PlanSection.Drive, PlanAction.Keep, Word(S.Drive), Data(mapping), null, R.AtTarget);
            }
            else if (d == oldLetter && !removeOld)
            {
                // The drive config.json records is gone: the user removed it, so it comes back only when ticked.
                AskItem("drive.map", PlanSection.Drive, Word(S.Drive), Word(W.Absent), Data(mapping), R.DriveGone, d + ":");
                willMap = _a.IsTicked("drive.map");
            }
            else
            {
                Add("drive.map", PlanSection.Drive, PlanAction.Create, Word(S.Drive), Word(W.Absent), Data(mapping), R.DriveCreate);
                willMap = true;
            }

            if (willMap)
            {
                Add("drive.session", PlanSection.Drive, PlanAction.Create, Word(S.DriveSession), Word(W.Absent), Data(mapping), R.DriveSession);
            }

            if (!label.IsValid)
            {
                Invalid("drive.label", PlanSection.Drive, Word(S.Label), _a.DriveName, "DriveName");
            }
            else if (labels is null)
            {
                Unread("drive.label", PlanSection.Drive, Word(S.Label), _s.DriveLabels.State, _s.DriveLabels.Error);
            }
            else
            {
                var currentLabel = labels.TryGetValue(d, out var cl) ? cl : null;
                if (string.Equals(currentLabel, label.Value, StringComparison.Ordinal))
                {
                    Add("drive.label", PlanSection.Drive, PlanAction.Keep, Word(S.Label), Data(currentLabel), null, R.AtTarget);
                }
                else if (currentLabel is null)
                {
                    Add("drive.label", PlanSection.Drive, PlanAction.Create, Word(S.Label), Word(W.Absent), Data(label.Value), R.LabelSet);
                }
                else
                {
                    // A name CPFS gave this letter is replaced; anyone else's is saved first, so Uninstall puts it back.
                    // A backup is taken once per letter, so a name given since (the user renamed CPFS's drive) would
                    // be lost on Uninstall: it is replaced only when ticked.
                    var ours = d == oldLetter && SameText(currentLabel, _c?.OwnDriveLabel);
                    var original = _c?.BackupDriveLabel(d);
                    if (ours)
                    {
                        Add("drive.label", PlanSection.Drive, PlanAction.Change, Word(S.Label), Data(currentLabel), Data(label.Value), R.LabelSet);
                    }
                    else if (original is not null && !string.Equals(currentLabel, original, StringComparison.Ordinal))
                    {
                        AskItem("drive.label", PlanSection.Drive, Word(S.Label), Data(currentLabel), Data(label.Value), R.LabelRenamed,
                            d + ":", currentLabel, label.Value!);
                    }
                    else
                    {
                        Add("drive.label", PlanSection.Drive, PlanAction.Change, Word(S.Label), Data(currentLabel), Data(label.Value), R.LabelForeign);
                        MarkBackup();
                    }
                }
            }
        }

        // The desktop shortcut on the Public Desktop. A recorded shortcut under another name goes first; a file CPFS did
        // not record is never overwritten.
        public void Shortcut(string? path)
        {
            var recorded = _c?.Shortcut;
            var files = _s.PublicDesktopFiles;
            string? target = null;
            string? fileName = null;
            if (_a.Shortcut)
            {
                var name = Validators.ShortcutName(_a.ShortcutName);
                if (!name.IsValid)
                {
                    Invalid("shortcut.file", PlanSection.Shortcut, Word(S.Shortcut), _a.ShortcutName, "ShortcutName");
                    return;
                }

                if (!files.IsOk)
                {
                    Unread("shortcut.file", PlanSection.Shortcut, Word(S.Shortcut), files.State, files.Error);
                    return;
                }

                fileName = name.Value + ".lnk";
                target = _s.CommonDesktop.TrimEnd('\\') + "\\" + fileName;
            }

            if (recorded is not null && !SameText(recorded, target) && files.IsOk)
            {
                var inDesktop = SameText(DirectoryOf(recorded), _s.CommonDesktop.TrimEnd('\\'));
                if (inDesktop && Contains(files.Value!, FileName(recorded)))
                {
                    Add("shortcut.old", PlanSection.Shortcut, PlanAction.RemoveIfOurs, Word(S.OldShortcut), Data(recorded), Word(W.Absent), R.ShortcutOldRemove);
                }
            }

            if (!_a.Shortcut)
            {
                if (recorded is null)
                {
                    Add("shortcut.file", PlanSection.Shortcut, PlanAction.Keep, Word(S.Shortcut), Word(W.None), null, R.ShortcutOff);
                }

                return;
            }

            var exists = Contains(files.Value!, fileName!);
            var ours = SameText(target, recorded);
            if (exists && !ours)
            {
                Items.Add(Cannot("shortcut.file", PlanSection.Shortcut, Data(target), PlanCause.InvalidAnswer, R.ShortcutForeign, fileName!));
            }
            else if (exists && path is not null && !SamePath(path, _c?.SharePath))
            {
                Add("shortcut.file", PlanSection.Shortcut, PlanAction.Change, Data(target), Data(_c?.SharePath ?? "?"), Data(path), R.ShortcutRetarget);
            }
            else if (exists)
            {
                Add("shortcut.file", PlanSection.Shortcut, PlanAction.Keep, Data(target), Word(W.Exists), null, R.AtTarget);
            }
            else if (ours)
            {
                // The earlier install made it and it is gone: the user removed it, so it comes back only when ticked.
                AskItem("shortcut.file", PlanSection.Shortcut, Data(target), Word(W.Absent), Data(path ?? _a.SharePath), R.ShortcutGone, fileName!);
            }
            else
            {
                Add("shortcut.file", PlanSection.Shortcut, PlanAction.Create, Data(target), Word(W.Absent), Data(path ?? _a.SharePath), R.ShortcutCreate);
            }
        }

        public void DataFolder()
        {
            if (!_s.DataFolder.IsOk)
            {
                Unread("data.folder", PlanSection.DataFolder, Word(S.DataFolder), _s.DataFolder.State, _s.DataFolder.Error);
                return;
            }

            var d = _s.DataFolder.Value!;
            if (!d.Exists)
            {
                Add("data.folder", PlanSection.DataFolder, PlanAction.Create, Word(S.DataFolder), Word(W.Absent), Word(W.Locked2), R.DataCreate);
            }
            else if (d.OwnerIsAdministrators && d.Protected && !d.OthersCanWrite)
            {
                Add("data.folder", PlanSection.DataFolder, PlanAction.Keep, Word(S.DataFolder), Word(W.Locked2), null, R.AtTarget);
            }
            else
            {
                Add("data.folder", PlanSection.DataFolder, PlanAction.Change, Word(S.DataFolder), Word(W.NotLocked), Word(W.Locked2), R.DataLock);
            }
        }

        // As in the scripts, the live name (or the pending one) is kept: the form has no computer name field yet, and a
        // saved name that differs is shown, never planned as a rename.
        public void Computer()
        {
            if (!_s.ComputerNames.IsOk || (_s.ComputerNames.Value!.Pending ?? _s.ComputerNames.Value.Active) is null)
            {
                Unread("computer.name", PlanSection.Computer, Word(S.ComputerName), _s.ComputerNames.State, _s.ComputerNames.Error);
                return;
            }

            var names = _s.ComputerNames.Value!;
            var current = (names.Pending ?? names.Active)!;
            if (names.Pending is not null)
            {
                Add("computer.name", PlanSection.Computer, PlanAction.Keep, Word(S.ComputerName), Data(names.Active ?? "?"), Data(names.Pending), R.ComputerPending, names.Pending);
            }
            else
            {
                Add("computer.name", PlanSection.Computer, PlanAction.Keep, Word(S.ComputerName), Data(current), null, R.AtTarget);
            }

            var saved = _c?.HostName?.Trim();
            if (!string.IsNullOrEmpty(saved) && !SameText(saved, current))
            {
                Add("computer.saved", PlanSection.Computer, PlanAction.Keep, Word(S.SavedComputerName), Data(saved), null, R.ComputerSavedDiffers, saved!, current);
            }
        }

        private void Bool(string key, PlanSection section, PlanValue subject, bool current, bool target, string yes, string no, string reason, bool backup)
        {
            var keep = current == target;
            Add(key, section, keep ? PlanAction.Keep : PlanAction.Change, subject, Word(current ? yes : no), keep ? null : Word(target ? yes : no), keep ? R.AtTarget : reason);
            if (!keep)
            {
                MarkBackup(backup);
            }
        }

        private void AskItem(string key, PlanSection section, PlanValue subject, PlanValue? current, PlanValue? target, string reason, params string[] args)
        {
            Items.Add(new PlanItem
            {
                Key = key, Section = section, Action = PlanAction.Ask, Subject = subject, Current = current, Target = target,
                Ticked = _a.IsTicked(key), ReasonKey = reason, ReasonArgs = args,
            });
        }

        private void Add(string key, PlanSection section, PlanAction action, PlanValue subject, PlanValue? current, PlanValue? target, string reason, params string[] args) =>
            Items.Add(new PlanItem
            {
                Key = key, Section = section, Action = action, Subject = subject, Current = current, Target = target, ReasonKey = reason, ReasonArgs = args,
            });

        private void MarkBackup(bool needed = true)
        {
            if (needed)
            {
                Items[Items.Count - 1] = Items[Items.Count - 1] with { BackupNeeded = true };
            }
        }

        private void Unread(string key, PlanSection section, PlanValue subject, ReadState state, string? error) =>
            Items.Add(state == ReadState.NeedsAdmin
                ? Cannot(key, section, subject, PlanCause.NeedsAdmin, R.NeedsAdmin)
                : Cannot(key, section, subject, PlanCause.ReadFailed, R.ReadFailed, error ?? "?"));

        private void Invalid(string key, PlanSection section, PlanValue subject, string value, string field) =>
            Items.Add(Cannot(key, section, subject, PlanCause.InvalidAnswer, R.Invalid, field) with { Current = Data(value) });

        private static PlanItem Cannot(string key, PlanSection section, PlanValue subject, PlanCause cause, string reason, params string[] args) => new()
        {
            Key = key, Section = section, Action = PlanAction.CannotPlan, Cause = cause, Subject = subject, ReasonKey = reason, ReasonArgs = args,
        };
    }

    private static PlanValue Word(string key) => PlanValue.Word(key);

    private static PlanValue Data(string? value) => PlanValue.Of(value ?? "");

    private const int ShareAllow = 0;
    private const int ShareFull = 0;

    private static PlanValue RightWord(int right) => right switch
    {
        0 => Word(W.RightFull),
        1 => Word(W.RightChange),
        2 => Word(W.RightRead),
        _ => Word(W.RightCustom),
    };

    internal static PlanValue StartWord(int? start) => start switch
    {
        2 => Word(W.Automatic),
        3 => Word(W.Manual),
        4 => Word(W.Disabled),
        null => Word(W.Absent),
        _ => PlanValue.Of(start.Value),
    };

    // The UserList value: 0 hides the account from the sign-in screen; absent or any other value shows it.
    internal static PlanValue HiddenWord(int? value) => Word(value == 0 ? W.HiddenNow : W.Visible);

    // A lockout threshold of 0 means lockout is off; any other value is a number of attempts.
    internal static PlanValue LockoutWord(long threshold) => threshold == 0 ? Word(W.Off) : PlanValue.Of(threshold);

    // The hardening values as words: the flags read off (0) or on (1); RestrictAnonymous 2 is its strictest level;
    // LmCompatibilityLevel is a level from 0 to 5. Anything else shows its number.
    internal static PlanValue HardeningWord(string name, long? value)
    {
        if (value is not { } v)
        {
            return Word(W.Absent);
        }

        if (string.Equals(name, "LmCompatibilityLevel", StringComparison.OrdinalIgnoreCase))
        {
            return PlanValue.WordWith(W.Level, v);
        }

        return v switch
        {
            0 => Word(W.Off),
            1 => Word(W.On),
            2 when string.Equals(name, "RestrictAnonymous", StringComparison.OrdinalIgnoreCase) => Word(W.Strictest),
            _ => PlanValue.WordWith(W.Value, v),
        };
    }

    private static PlanValue FieldWords(IReadOnlyList<string> fields)
    {
        var keys = new List<string>(fields.Count);
        foreach (var f in fields)
        {
            keys.Add("Plan_F_" + f);
        }

        return PlanValue.Words(keys);
    }

    private static ShareInfo? FindShare(IReadOnlyList<ShareInfo> shares, string name)
    {
        foreach (var s in shares)
        {
            if (SameText(s.Name, name))
            {
                return s;
            }
        }

        return null;
    }

    // C$, D$ and ADMIN$: the shares AutoShareWks = 0 stops Windows from creating.
    private static bool IsAdminShare(string name) =>
        string.Equals(name, "ADMIN$", StringComparison.OrdinalIgnoreCase)
        || (name.Length == 2 && name[1] == '$' && char.IsLetter(name[0]) && name[0] < 128);

    private static bool SameText(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // A DOS Devices value, and the target DefineDosDevice gets with DDD_RAW_TARGET_PATH: the folder as an NT path.
    public static string DosTarget(string path) => @"\??\" + path.Trim().TrimEnd('\\');

    private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool SamePath(string? a, string? b) => SameText(Trim(a), Trim(b));

    private static string? Trim(string? path) => path?.Trim().TrimEnd('\\');

    private static string DirectoryOf(string path)
    {
        var i = path.LastIndexOf('\\');
        return i < 0 ? "" : path.Substring(0, i);
    }

    private static string FileName(string path)
    {
        var i = path.LastIndexOf('\\');
        return i < 0 ? path : path.Substring(i + 1);
    }

    private static bool Contains(IReadOnlyList<string> files, string name)
    {
        foreach (var f in files)
        {
            if (string.Equals(f, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<T> FindAll<T>(this IReadOnlyList<T> list, Predicate<T> match)
    {
        var result = new List<T>();
        foreach (var item in list)
        {
            if (match(item))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static bool Exists<T>(this IReadOnlyList<T> list, Predicate<T> match)
    {
        foreach (var item in list)
        {
            if (match(item))
            {
                return true;
            }
        }

        return false;
    }
}

public enum UserPlan
{
    NotUsable,
    WillCreate,
    Usable,
}

public enum Stricter
{
    Higher,
    Lower,
}

public sealed record HardeningValue(string Key, string Name, int Target, Stricter Direction)
{
    public bool IsStricter(int current) => Direction == Stricter.Higher ? current > Target : current < Target;
}
