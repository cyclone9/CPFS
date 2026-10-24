using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.Principal;
using Cpfs.Core.Apply;
using Cpfs.Core.Config;
using Cpfs.Core.Planning;

namespace Cpfs.Windows.Apply;

// What Setup's steps share: the answers, the password (only for the account steps, never stored), the plan they
// come from and config.json while it is being written.
internal sealed class SetupRun
{
    public SetupRun(SetupAnswers answers, SecureString? password, Plan plan, ConfigBook book)
    {
        Answers = answers;
        Password = password;
        Plan = plan;
        Book = book;
        var vpn = new List<string>();
        foreach (var item in plan.Items)
        {
            if (item.Key.StartsWith("fw.vpnadapter:", StringComparison.Ordinal)
                && (item.Action == PlanAction.Keep || (item.Action == PlanAction.Ask && item.Ticked)))
            {
                vpn.Add(item.Key.Substring("fw.vpnadapter:".Length));
            }
        }

        Rules = FirewallRules.Wanted(vpn);

        // config.json as it was before this run: what the previous drive and shortcut were, and where they point.
        Original = book.FirstSetup ? null : ConfigReader.Parse(ConfigWriter.Serialize(book.Root));
    }

    public ConfigDocument? Original { get; }

    public SetupAnswers Answers { get; }

    public SecureString? Password { get; }

    public Plan Plan { get; }

    public ConfigBook Book { get; }

    public IReadOnlyList<RuleSpec> Rules { get; }

    public string User => Answers.SmbUser.Trim();

    public string SharePath => Answers.SharePath.Trim().TrimEnd('\\');

    public string ShareName => Answers.ShareName.Trim();

    public bool CreatedAccount { get; set; }

    public bool RestartNeeded { get; set; }

    // A drive letter mapping was removed: Windows keeps the one it made at start until the next restart.
    public bool DriveRestartNeeded { get; set; }

    public char? OldLetter => Original?.DriveLetter is { Length: 1 } l ? char.ToUpperInvariant(l[0]) : null;

    public char Letter => Answers.DriveLetter is { } l ? char.ToUpperInvariant(l) : throw new InvalidOperationException("No drive letter was chosen.");

    public string DriveName => Answers.DriveName.Trim();

    public string ShortcutDescription => Planner.ShareDescription + " - " + SharePath;

    // Runs the plan; the progress callback is called on the worker thread.
    public ExecutionResult Execute(Action<StepReport> progress)
    {
        using var privileges = Privileges.Enable("SeSecurityPrivilege", "SeTakeOwnershipPrivilege", "SeRestorePrivilege");
        var entries = ApplyList.Setup(Plan);
        return Executor.Run(entries, e => SetupSteps.For(this, e.Item), Book,
            b => ConfigStore.Save(b.Serialize(AppLog.VersionNumber(), DateTime.Now)),
            r =>
            {
                if (r.Outcome != StepOutcome.Running)
                {
                    AppLog.Write("apply", r.Outcome + " " + r.Key + (r.Error is null ? "" : ": " + r.Error));
                }

                progress(r);
            },
            b =>
            {
                b.SetField("SmbUser", User);
                b.SetField("SharePath", SharePath);
                b.SetField("ShareName", ShareName);
                b.SetField("Strict", Answers.Strict);
                b.SetField("Access", (Answers.Drive ? 1 : 0) | (Answers.Shortcut ? 2 : 0));

                // Turned off: whatever is still recorded is no longer CPFS's (its own was removed above).
                if (!Answers.Drive)
                {
                    b.SetField("DriveLetter", null);
                    b.SetField("DriveLabel", null);
                    b.TrackCreated("DrivePath", null);
                }

                if (!Answers.Shortcut)
                {
                    b.SetField("Shortcut", null);
                }

                // Turned off: an entry recorded for this desktop user is gone now (removed above, or by the user); one
                // recorded for another user stays recorded.
                b.SetField("ExplorerEntry", Answers.ExplorerEntry);
                if (!Answers.ExplorerEntry && string.Equals(Original?.TrackedExplorerEntry?.Sid, DesktopUser.Current.Sid, StringComparison.OrdinalIgnoreCase))
                {
                    b.TrackCreated("ExplorerEntry", null);
                }
            });
    }

    public SecurityIdentifier UserSid() =>
        LocalAccounts.GetUser(User)?.Sid ?? throw new InvalidOperationException("The SMB account does not exist.");

    public string UserAccount() => UserSid().Translate(typeof(NTAccount)).Value;
}

// One step per plan item key. Each reads the state again first, so a step already done (by an earlier run that
// stopped, or by hand) is kept instead of written twice.
internal static class SetupSteps
{
    public static IChangeStep For(SetupRun run, PlanItem item)
    {
        var key = item.Key;
        var colon = key.IndexOf(':');
        var family = colon < 0 ? key : key.Substring(0, colon);
        var arg = colon < 0 ? "" : key.Substring(colon + 1);
        return family switch
        {
            "data.folder" => DataFolder(),
            "backup" => Backup(run),
            "user.account" => CreateAccount(run),
            "user.takeover" => TakeOver(run),
            "user.enabled" => Flag(run, u => u.Enabled, f => f & ~WinApi.UfAccountDisable),
            "user.neverexpires" => Flag(run, u => u.PasswordNeverExpires, f => f | WinApi.UfDontExpirePasswd),
            "user.unlock" => Flag(run, u => !u.Locked, f => f),
            "user.users" => Group(run, Accounts.UsersSid, member: true),
            "user.admin" => Group(run, Accounts.AdminsSid, member: false),
            "user.hidden" => Hidden(run),
            "user.password" => Password(run),
            "folder.exists" => FolderExists(run),
            "folder.ace" => FolderAce(run),
            "share.main" => MainShare(run, item),
            "share.foreign" => ForeignShare(run, arg),
            "share.old" => OldShare(run, arg),
            "share.adminshares" => AdminShares(),
            "share.access" when arg.Length == 0 => ShareAccess(run),
            "share.access" => ShareEntry(run, arg),
            "server.start" => ServerStart(run, item),
            "server.smb2" => ServerFlag(run, item, "EnableSMB2Protocol", "Smb2", s => s.Smb2, true),
            "server.reject" => ServerFlag(run, item, "RejectUnencryptedAccess", "RejectUnencrypted", s => s.RejectUnencrypted, true),
            "server.smb1" => ServerFlag(run, item, "EnableSMB1Protocol", null, s => s.Smb1, false),
            "server.smb1feature" => Smb1(run),
            "hardening.guest" => Guest(),
            "hardening.lockout" => LockoutStep(run),
            "hardening.audit" => AuditStep(run),
            "fw.rule" => Rule(run, item, arg),
            "fw.extra" => RemoveRules(arg),
            "fw.builtin" => BuiltIn(run, arg),
            "net" => Network(run, arg),
            "task.runasuser" => QuickAccessSteps.StaleTask(),
            "drive.oldlabel" => QuickAccessSteps.OldLabel(run, item),
            "drive.oldicon" => QuickAccessSteps.OldIcon(run),
            "drive.oldsession" => QuickAccessSteps.OldSession(run),
            "drive.oldmap" => QuickAccessSteps.OldMap(run),
            "drive.map" => QuickAccessSteps.Map(run),
            "drive.session" => QuickAccessSteps.Session(run),
            "drive.label" => QuickAccessSteps.Label(run, item),
            "drive.icon" => QuickAccessSteps.Icon(run, item),
            "explorer.entry" => QuickAccessSteps.Explorer(run, item),
            "shortcut.old" => QuickAccessSteps.OldShortcut(run),
            "shortcut.file" => QuickAccessSteps.Shortcut(run, item),
            "shortcut.icon" => QuickAccessSteps.ShortcutIcon(run),
            _ when family.StartsWith("hardening.", StringComparison.Ordinal) => Hardening(family.Substring("hardening.".Length)),
            _ => throw new NotSupportedException("No step for plan item " + key),
        };
    }

    private static LambdaStep DataFolder() => new LambdaStep
    {
        Read = Locked,
        Do = () => Folders.LockDataFolder(StateReader.DataFolder),
        Check = Locked,
    };

    private static bool Locked()
    {
        var d = PlanReader.DataFolder();
        return d.Exists && d.OwnerIsAdministrators && d.Protected && !d.OthersCanWrite;
    }

    // The scripts' Backup block, read now (not from the plan's reading), before anything else changes.
    private static LambdaStep Backup(SetupRun run)
    {
        BulkBackup? values = null;
        return new LambdaStep
        {
            Read = () => !run.Book.FirstSetup || run.Book.HasBackupBlock,
            Do = () => values = ReadBulk(),
            TrackWith = b => b!.TakeBulkBackup(values!, DateTime.Now),
            Check = () => run.Book.HasBackupBlock,
        };
    }

    private static BulkBackup ReadBulk()
    {
        var registry = new List<RegistryBackup>();
        foreach (var h in Planner.Hardening)
        {
            var value = Reg.ReadDword(h.Key, h.Name);
            var exists = Reg.Exists(h.Key, h.Name);
            if (exists && value is null)
            {
                // Restoring writes a DWORD, as the scripts do; a value of another type would be lost.
                throw new InvalidOperationException(h.Name + " exists but is not a DWORD; Setup stops before changing anything.");
            }

            registry.Add(new RegistryBackup(@"HKLM:\" + h.Key, h.Name, exists, value));
        }

        // Without these the 0.2.0 Uninstall would read null as "off", so a failed read stops Setup here.
        var smb = PlanReader.SmbServer();
        if (smb.Smb2 is not { } smb2 || smb.RejectUnencrypted is not { } reject)
        {
            throw new InvalidOperationException("The SMB server settings could not be read for the backup.");
        }

        var names = PlanReader.ComputerName();
        var start = PlanReader.ServerStart() switch
        {
            0 => "Boot",
            1 => "System",
            2 => "Automatic",
            3 => "Manual",
            4 => "Disabled",
            _ => null,
        };
        AuditSetting? audit = null;
        try
        {
            audit = PlanReader.AuditLogon();
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            AppLog.Write("apply", "Backup: audit setting not read (" + ex.GetType().Name + ")");
        }

        using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var tcp = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters");
        var host = tcp?.GetValue("Hostname") as string ?? names;
        return new BulkBackup(host, registry, smb2, reject, start, PlanReader.Lockout(), audit);
    }

    private static LambdaStep CreateAccount(SetupRun run) => new LambdaStep
    {
        Read = () => LocalAccounts.GetUser(run.User) is not null
            ? throw new InvalidOperationException("The account " + run.User + " appeared after the plan was made. Read the plan again.")
            : false,

        // Recorded before it exists, so an account created by a step that then fails is still CPFS's to remove.
        RecordWith = b => b!.TrackUser(run.User, false, false, null) ? BackupWrite.Recorded : BackupWrite.NotNeeded,
        Do = () =>
        {
            Accounts.Create(run.User, run.Password ?? throw new InvalidOperationException("No password was given for the new account."));
            run.CreatedAccount = true;
        },
        TrackWith = b => b!.SetField("SmbUser", run.User),
        Check = () => LocalAccounts.GetUser(run.User) is not null,
    };

    private static LambdaStep TakeOver(SetupRun run) => new LambdaStep
    {
        Read = () => run.Book.IsTrackedUser(run.User),
        RecordWith = b =>
        {
            var info = PlanReader.User(run.User) ?? throw new InvalidOperationException("The account is gone.");
            var groups = new List<string>();
            if (info.IsAdmin == true)
            {
                groups.Add(Accounts.AdminsSid);
            }

            if (info.InUsers == true)
            {
                groups.Add(Accounts.UsersSid);
            }

            return b!.TrackUser(run.User, true, info.IsAdmin == true, new UserBefore(info.Enabled, info.PasswordNeverExpires, groups, info.HiddenValue))
                ? BackupWrite.Recorded
                : BackupWrite.AlreadyRecorded;
        },
        TrackWith = b => b!.SetField("SmbUser", run.User),
        Check = () => run.Book.IsTrackedUser(run.User),
    };

    private static LambdaStep Flag(SetupRun run, Func<LocalUser, bool> atTarget, Func<uint, uint> change) => new LambdaStep
    {
        Read = () => atTarget(LocalAccounts.GetUser(run.User) ?? throw new InvalidOperationException("The account is gone.")),
        Do = () => Accounts.SetFlags(run.User, change),
        Check = () => atTarget(LocalAccounts.GetUser(run.User)!),
    };

    private static LambdaStep Group(SetupRun run, string groupSid, bool member) => new LambdaStep
    {
        Read = () => LocalAccounts.IsMember(run.UserSid(), groupSid) == member,
        Do = () =>
        {
            if (member)
            {
                Accounts.AddToGroup(run.UserSid(), groupSid);
            }
            else
            {
                Accounts.RemoveFromGroup(run.UserSid(), groupSid);
            }
        },
        Check = () => LocalAccounts.IsMember(run.UserSid(), groupSid) == member,
    };

    private static LambdaStep Hidden(SetupRun run) => new LambdaStep
    {
        Read = () => Reg.ReadDword(Reg.UserList, run.User) == 0,
        Do = () => Reg.SetDword(Reg.UserList, run.User, 0),
        Check = () => Reg.ReadDword(Reg.UserList, run.User) == 0,
    };

    // A password can't be read back: a new account got it when it was created, an existing one gets it here.
    private static LambdaStep Password(SetupRun run) => new LambdaStep
    {
        Read = () => run.CreatedAccount,
        Do = () => Accounts.SetPassword(run.User, run.Password ?? throw new InvalidOperationException("No password was given.")),
    };

    private static LambdaStep FolderExists(SetupRun run) => new LambdaStep
    {
        Read = () => Directory.Exists(run.SharePath),
        Do = () => Directory.CreateDirectory(run.SharePath),
        Check = () => Directory.Exists(run.SharePath),
    };

    private static LambdaStep FolderAce(SetupRun run) => new LambdaStep
    {
        Read = () => PlanReader.Folder(run.SharePath, run.UserSid().Value).UserHasModify,
        RecordWith = b =>
        {
            b!.TrackCreated("AcePath", run.SharePath);
            b.TrackCreated("AceSid", run.UserSid().Value);
            return BackupWrite.Recorded;
        },
        Do = () => Folders.GrantModify(run.SharePath, run.UserSid()),
        TrackWith = b => b!.SetField("SharePath", run.SharePath),
        Check = () => PlanReader.Folder(run.SharePath, run.UserSid().Value).UserHasModify,
    };

    private static ShareInfo? ReadShare(string name)
    {
        foreach (var s in PlanReader.Shares())
        {
            if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }

        return null;
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool ShareAtTarget(SetupRun run)
    {
        var s = ReadShare(run.ShareName);
        return s is not null && SamePath(s.Path, run.SharePath) && s.EncryptData == run.Answers.Strict && s.FolderEnumerationMode == 0
            && s.CachingMode == 0 && string.Equals(s.Description, Planner.ShareDescription, StringComparison.Ordinal);
    }

    private static bool IsOurs(SetupRun run, ShareInfo share, ConfigDocument? config) =>
        string.Equals(share.Description, Planner.ShareDescription, StringComparison.Ordinal)
        || (config is not null && string.Equals(share.Name, config.ShareName, StringComparison.OrdinalIgnoreCase) && SamePath(share.Path, config.SharePath));

    private static LambdaStep MainShare(SetupRun run, PlanItem item)
    {
        void Track(ConfigBook? b)
        {
            b!.SetField("ShareName", run.ShareName);
            b.SetField("SharePath", run.SharePath);
            b.SetField("Strict", run.Answers.Strict);
        }

        if (item.Action == PlanAction.Create)
        {
            return new LambdaStep
            {
                Read = () => ReadShare(run.ShareName) is not null
                    && (ShareAtTarget(run) ? true : throw new InvalidOperationException("A share named " + run.ShareName + " appeared after the plan was made. Read the plan again.")),
                Do = () => Shares.Create(run.ShareName, run.SharePath, run.Answers.Strict, run.UserAccount()),
                TrackWith = b =>
                {
                    Track(b);
                    b!.TrackCreated("Share", run.ShareName);
                },
                Check = () => ShareAtTarget(run),
            };
        }

        // The share is CPFS's (the planner checked); a new folder means a new share, since a share's path is fixed.
        var moving = item.ReasonKey == "Plan_R_ShareMove";
        return new LambdaStep
        {
            Read = () => ShareAtTarget(run),
            Do = () =>
            {
                if (!moving)
                {
                    Shares.Update(run.ShareName, run.Answers.Strict);
                    return;
                }

                var current = ReadShare(run.ShareName) ?? throw new InvalidOperationException("The share is gone.");
                if (!IsOurs(run, current, null) && !string.Equals(current.Name, ConfigName(run), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The share " + current.Name + " is not CPFS's; it is left alone.");
                }

                Shares.CloseSessions(run.User);
                Shares.Delete(run.ShareName);
                Shares.Create(run.ShareName, run.SharePath, run.Answers.Strict, run.UserAccount());
            },
            TrackWith = Track,
            Check = () => ShareAtTarget(run),
        };
    }

    private static string? ConfigName(SetupRun run) => ConfigJson.GetString(run.Book.Root, "ShareName");

    private static LambdaStep ForeignShare(SetupRun run, string name) => new LambdaStep
    {
        Read = () => ReadShare(name) is null,

        // Recorded before it is removed: Uninstall shows it as a share to recreate by hand.
        RecordWith = b =>
        {
            using var share = Shares.Find(name) ?? throw new InvalidOperationException("The share is gone.");
            b!.TrackReplacedShare(name, StateReader.Prop(share, "Path") as string, StateReader.Prop(share, "Description") as string,
                StateReader.Prop(share, "SecurityDescriptor") as string);
            return BackupWrite.Recorded;
        },
        Do = () => Shares.Delete(name),
        Check = () => ReadShare(name) is null,
    };

    private static LambdaStep OldShare(SetupRun run, string name) => new LambdaStep
    {
        Read = () => ReadShare(name) is null,
        Do = () =>
        {
            var share = ReadShare(name)!;
            var config = ConfigReader.Parse(ConfigWriter.Serialize(run.Book.Root));
            if (!IsOurs(run, share, config))
            {
                throw new InvalidOperationException("The share " + name + " is not CPFS's; it is left alone.");
            }

            Shares.CloseSessions(run.User);
            Shares.Delete(name);
        },
        Check = () => ReadShare(name) is null,
    };

    private static bool IsAdminShare(string name) =>
        string.Equals(name, "ADMIN$", StringComparison.OrdinalIgnoreCase) || (name.Length == 2 && name[1] == '$' && name[0] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'));

    private static LambdaStep AdminShares()
    {
        static List<string> Present()
        {
            var list = new List<string>();
            foreach (var s in PlanReader.Shares())
            {
                if (IsAdminShare(s.Name))
                {
                    list.Add(s.Name);
                }
            }

            return list;
        }

        return new LambdaStep
        {
            Read = () => Present().Count == 0,
            Do = () =>
            {
                foreach (var name in Present())
                {
                    Shares.Delete(name);
                }
            },
            Check = () => Present().Count == 0,
        };
    }

    private static bool UserHasFull(SetupRun run)
    {
        var sid = run.UserSid().Value;
        foreach (var e in PlanReader.ShareAccess(run.ShareName) ?? new List<ShareAccessEntry>())
        {
            if (string.Equals(e.Sid, sid, StringComparison.OrdinalIgnoreCase) && e.ControlType == 0 && e.Right == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static LambdaStep ShareAccess(SetupRun run) => new LambdaStep
    {
        Read = () => UserHasFull(run),
        Do = () => Shares.Access(run.ShareName, "GrantAccess", run.UserAccount(), 0),
        Check = () => UserHasFull(run),
    };

    private static ShareAccessEntry? Entry(SetupRun run, string id)
    {
        foreach (var e in PlanReader.ShareAccess(run.ShareName) ?? new List<ShareAccessEntry>())
        {
            if (string.Equals(e.Sid ?? e.Account, id, StringComparison.OrdinalIgnoreCase))
            {
                return e;
            }
        }

        return null;
    }

    // Someone else's allow entry is revoked; the SMB account's own deny entry is unblocked.
    private static LambdaStep ShareEntry(SetupRun run, string id) => new LambdaStep
    {
        Read = () => Entry(run, id) is null,
        Do = () =>
        {
            var e = Entry(run, id)!;
            Shares.Access(run.ShareName, e.ControlType == 0 ? "RevokeAccess" : "UnblockAccess", e.Account);
        },
        Check = () => Entry(run, id) is null,
    };

    private static BackupWrite Many(params BackupWrite[] writes)
    {
        var result = BackupWrite.NotNeeded;
        foreach (var w in writes)
        {
            if (w == BackupWrite.NoBackup)
            {
                return BackupWrite.NoBackup;
            }

            if (w == BackupWrite.Recorded || (w == BackupWrite.AlreadyRecorded && result == BackupWrite.NotNeeded))
            {
                result = w;
            }
        }

        return result;
    }

    private static LambdaStep ServerStart(SetupRun run, PlanItem item) => new LambdaStep
    {
        Read = () => PlanReader.ServerStart() == 2,
        RecordWith = b => item.BackupNeeded
            ? b!.Backup("ServerStart", PlanReader.ServerStart() switch { 3 => "Manual", 4 => "Disabled", _ => null })
            : BackupWrite.NotNeeded,
        Do = () =>
        {
            Service.SetStart(Service.Server, 2);
            Service.Start(Service.Server);
        },
        Check = () => PlanReader.ServerStart() == 2,
    };

    private static LambdaStep ServerFlag(SetupRun run, PlanItem item, string property, string? backupField, Func<SmbServerInfo, bool?> read, bool target) =>
        new LambdaStep
        {
            Read = () => read(PlanReader.SmbServer()) == target,
            RecordWith = b => item.BackupNeeded && backupField is not null && read(PlanReader.SmbServer()) is { } current
                ? b!.Backup(backupField, current)
                : BackupWrite.NotNeeded,
            Do = () => SmbServer.Set(property, target),
            Check = () => read(PlanReader.SmbServer()) == target,
        };

    private static LambdaStep Smb1(SetupRun run)
    {
        var restart = false;
        return new LambdaStep
        {
            Read = () => PlanReader.Smb1Feature() != 1,
            Do = () =>
            {
                restart = Smb1Feature.Disable();
                run.RestartNeeded |= restart;
            },

            // With a restart pending, the feature can still read as enabled until Windows restarts.
            Check = () => restart || PlanReader.Smb1Feature() != 1,
        };
    }

    // Written only when the value is not already CPFS's or stricter: an existing stricter setting is never loosened.
    private static LambdaStep Hardening(string name)
    {
        HardeningValue? found = null;
        foreach (var h in Planner.Hardening)
        {
            if (string.Equals(h.Name, name, StringComparison.Ordinal))
            {
                found = h;
            }
        }

        var value = found ?? throw new NotSupportedException("Unknown hardening value " + name);
        bool AtTarget()
        {
            var current = Reg.ReadDword(value.Key, value.Name);
            return current == value.Target || (current is { } c && value.IsStricter(c));
        }

        return new LambdaStep
        {
            Read = AtTarget,
            Do = () => Reg.SetDword(value.Key, value.Name, value.Target),
            Check = AtTarget,
        };
    }

    private static LambdaStep Guest() => new LambdaStep
    {
        Read = () => PlanReader.GuestEnabled() != true,
        Do = () => Accounts.SetFlags(Accounts.GuestName() ?? throw new InvalidOperationException("No account with RID 501."), f => f | WinApi.UfAccountDisable),
        Check = () => PlanReader.GuestEnabled() != true,
    };

    // Switched on only when it is off (threshold 0); the duration and window stay as they are.
    private static LambdaStep LockoutStep(SetupRun run) => new LambdaStep
    {
        Read = () => PlanReader.Lockout().Threshold != 0,
        RecordWith = b =>
        {
            var current = PlanReader.Lockout();
            return Many(b!.Backup("Lockout", (long)current.Threshold), b.Backup("LockoutDuration", (long)current.DurationSeconds),
                b.Backup("LockoutWindow", (long)current.WindowSeconds));
        },
        Do = () =>
        {
            var current = PlanReader.Lockout();
            Lockout.Set(Planner.LockoutThreshold, current.DurationSeconds, current.WindowSeconds);
        },
        Check = () => PlanReader.Lockout().Threshold == Planner.LockoutThreshold,
    };

    private static LambdaStep AuditStep(SetupRun run) => new LambdaStep
    {
        Read = () => PlanReader.AuditLogon().Failure,
        RecordWith = b =>
        {
            var current = PlanReader.AuditLogon();
            return Many(b!.Backup("AuditLogonSuccess", current.Success), b.Backup("AuditLogonFailure", current.Failure)) switch
            {
                BackupWrite.NoBackup => BackupWrite.NotNeeded,
                var w => w,
            };
        },
        Do = () => Audit.SetLogon(PlanReader.AuditLogon().Success, true),
        Check = () => PlanReader.AuditLogon().Failure,
    };

    private static List<FirewallRuleInfo> Named(string name)
    {
        var list = new List<FirewallRuleInfo>();
        foreach (var r in PlanReader.OwnRules())
        {
            if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(r);
            }
        }

        return list;
    }

    private static LambdaStep Rule(SetupRun run, PlanItem item, string name)
    {
        RuleSpec? spec = null;
        foreach (var s in run.Rules)
        {
            if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                spec = s;
            }
        }

        if (spec is null)
        {
            // Planned for removal: the VPN rule when no VPN adapter is chosen.
            return RemoveRules(name);
        }

        bool AtTarget()
        {
            var rules = Named(name);
            return rules.Count == 1 && FirewallRules.Differences(spec, rules[0]).Count == 0;
        }

        return new LambdaStep
        {
            Read = AtTarget,
            Do = () =>
            {
                // Written disabled, read back, enabled only when it matches, and read back once more.
                var rules = Named(name);
                if (rules.Count == 1)
                {
                    Firewall.Update(spec, rules[0]);
                }
                else
                {
                    if (rules.Count > 1)
                    {
                        Firewall.RemoveAll(name);
                    }

                    Firewall.Create(spec);
                }

                Staged(spec, enabled: false);
                Firewall.SetEnabled(name, true);
                Staged(spec, enabled: true);
            },
            Check = AtTarget,
        };
    }

    // A rule that does not read back as written is removed, never left to open 445 somewhere it must not.
    private static void Staged(RuleSpec spec, bool enabled)
    {
        var problems = FirewallRules.StagedProblems(spec, Named(spec.Name), enabled);
        if (problems.Count == 0)
        {
            return;
        }

        var fields = string.Join(", ", problems);
        AppLog.Write("apply", "Firewall rule " + spec.Name + " read back differently (" + fields + "); removing it");
        string after;
        try
        {
            Firewall.RemoveAll(spec.Name);
            after = "it was removed, so it opens nothing";
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            after = "removing it failed (" + ex.Message + ")";
            try
            {
                Firewall.SetEnabled(spec.Name, false);
                after += "; it was turned off";
            }
            catch (Exception ex2) when (State.IsExpected(ex2))
            {
                after += "; turning it off failed too (" + ex2.Message + "). Remove it in Windows Defender Firewall";
            }
        }

        throw new InvalidOperationException("Windows did not take the firewall rule " + spec.Name + " as written (" + fields + "): " + after + ".");
    }

    private static LambdaStep RemoveRules(string name) => new LambdaStep
    {
        Read = () => Named(name).Count == 0,
        Do = () => Firewall.RemoveAll(name),
        Check = () => Named(name).Count == 0,
    };

    // Recorded before the rule is turned off: re-enabling a rule that is still on does no harm, a disabled rule
    // nobody recorded would stay off after Uninstall.
    private static LambdaStep BuiltIn(SetupRun run, string id) => new LambdaStep
    {
        Read = () => Firewall.BuiltInEnabled(id) != true,
        RecordWith = b => b!.TrackFirewallRule(id) ? BackupWrite.Recorded : BackupWrite.AlreadyRecorded,
        Do = () => Firewall.SetBuiltIn(id, enable: false),
        Check = () => Firewall.BuiltInEnabled(id) != true,
    };

    private static bool NonePublic(string name)
    {
        foreach (var n in PlanReader.Networks())
        {
            if (string.Equals(n.Name, name, StringComparison.Ordinal) && n.Category == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static LambdaStep Network(SetupRun run, string name) => new LambdaStep
    {
        Read = () => NonePublic(name),
        RecordWith = b =>
        {
            var added = false;
            foreach (var id in Networks.Ids(name))
            {
                added |= b!.TrackNetwork(name, id);
            }

            return added ? BackupWrite.Recorded : BackupWrite.AlreadyRecorded;
        },
        Do = () => Networks.SetCategory(name, 0, 1),
        Check = () => NonePublic(name),
    };
}
