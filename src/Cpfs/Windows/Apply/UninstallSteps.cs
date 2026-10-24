using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using Cpfs.Core.Apply;
using Cpfs.Core.Config;
using Cpfs.Core.Planning;
using Cpfs.Core.Text;

namespace Cpfs.Windows.Apply;

// What Uninstall's steps share. The SMB account's SID is taken before anything runs: the account may be gone by the
// time a later step needs it.
internal sealed class UninstallRun
{
    public UninstallRun(Plan plan, ConfigDocument? config, byte[]? configBytes, string? userSid, string? desktopSid, string? documents)
    {
        Plan = plan;
        Config = config;
        ConfigBytes = configBytes;
        UserSid = userSid is null ? null : new SecurityIdentifier(userSid);
        DesktopSid = desktopSid is null ? null : new SecurityIdentifier(desktopSid);
        Documents = documents;
    }

    public Plan Plan { get; }

    public ConfigDocument? Config { get; }

    public byte[]? ConfigBytes { get; }

    public SecurityIdentifier? UserSid { get; }

    public SecurityIdentifier? DesktopSid { get; }

    public string? Documents { get; }

    public string User => Config?.SmbUser?.Trim() ?? "";

    public string SharePath => Config?.SharePath?.Trim().TrimEnd('\\') ?? "";

    public string? SavedCopy { get; set; }

    // The set-aside config-ignored-*.json files copied to Documents; the data folder goes only when none is left out.
    public HashSet<string> IgnoredCopied { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Windows keeps the letter it mapped at start until the next restart.
    public bool DriveRestartNeeded { get; set; }

    // The name Windows takes back at the next restart, once the step ran.
    public string? RenamedTo { get; set; }

    public ExecutionResult Execute(Action<StepReport> progress)
    {
        foreach (var item in Plan.Items)
        {
            if (UninstallPlanner.IsUnknownOriginal(item))
            {
                AppLog.Write("uninstall", item.Key + ": the backup records no original value; left as it is");
            }
        }

        using var privileges = Privileges.Enable("SeSecurityPrivilege", "SeTakeOwnershipPrivilege", "SeRestorePrivilege");
        return Executor.Run(ApplyList.Uninstall(Plan), e => UninstallSteps.For(this, e.Item), null, null, r =>
        {
            if (r.Outcome != StepOutcome.Running)
            {
                AppLog.Write("uninstall", r.Outcome + " " + r.Key + (r.LogText is null ? "" : ": " + r.LogText));
            }

            progress(r);
        });
    }
}

internal static class UninstallSteps
{
    public static IChangeStep For(UninstallRun run, PlanItem item)
    {
        var key = item.Key;
        var colon = key.IndexOf(':');
        var family = colon < 0 ? key : key.Substring(0, colon);
        var arg = colon < 0 ? "" : key.Substring(colon + 1);
        return family switch
        {
            "u.share" => Share(run, arg),
            "u.task.runasuser" => QuickAccessSteps.StaleTask(),
            "u.task" => Task(arg),
            "u.auto.exe" => AutoCopy(),
            "u.auto.folder" => AutoFolder(),
            "u.drive.label" => QuickAccessSteps.UninstallLabel(run),
            "u.drive.icon" => QuickAccessSteps.UninstallIcon(run),
            "u.drive.session" => QuickAccessSteps.UninstallSession(run),
            "u.drive" => QuickAccessSteps.UninstallMap(run),
            "u.explorer" => QuickAccessSteps.UninstallExplorer(run),
            "u.shortcut" => QuickAccessSteps.UninstallShortcut(run),
            "u.owner" => Owner(run),
            "u.ace" => Ace(run),
            "u.user.profile" => Profile(run),
            "u.user.delete" => DeleteUser(run),
            "u.user.before" => Before(run),
            "u.user.admin" => Admin(run),
            "u.user.hidden" => Unhide(run),
            "u.fw" => Rules(arg),
            "u.builtin" => BuiltIn(arg),
            "u.reg" => Registry(run, arg),
            "u.smb2" => ServerFlag("EnableSMB2Protocol", s => s.Smb2, run.Config!.BackupSmb2!.Value),
            "u.reject" => ServerFlag("RejectUnencryptedAccess", s => s.RejectUnencrypted, run.Config!.BackupRejectUnencrypted!.Value),
            "u.serverstart" => ServerStart(run),
            "u.lockout" => LockoutStep(run),
            "u.revert" => Revert(),
            "u.net" => Network(arg),
            "u.computer" => Computer(run),
            "u.savecopy" => SaveCopy(run),
            UninstallPlanner.IgnoredCopyKey => SaveIgnored(run),
            "u.data" => Data(run),
            _ => throw new NotSupportedException("No step for plan item " + key),
        };
    }

    private static bool ShareGone(string name)
    {
        foreach (var s in PlanReader.Shares())
        {
            if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // The share goes; the folder and its files stay. Devices connected as the SMB account are disconnected first.
    private static LambdaStep Share(UninstallRun run, string name) => new LambdaStep
    {
        Read = () => ShareGone(name),
        Do = () =>
        {
            if (run.User.Length > 0)
            {
                AppLog.Write("uninstall", "Sessions closed: " + Shares.CloseSessions(run.User).ToString(CultureInfo.InvariantCulture));
            }

            Shares.Delete(name);
        },
        Check = () => ShareGone(name),
    };

    private static LambdaStep Owner(UninstallRun run) => new LambdaStep
    {
        Do = () =>
        {
            var from = run.UserSid ?? throw new CpfsException("Err_SmbSidUnknown");
            var to = run.DesktopSid ?? throw new CpfsException("Err_DesktopUserUnknown");
            var (changed, failed) = Folders.GiveOwnership(run.SharePath, from, to);
            AppLog.Write("uninstall", "Ownership: owner changed for " + changed.ToString(CultureInfo.InvariantCulture) + " items, "
                + failed.ToString(CultureInfo.InvariantCulture) + " could not be read or changed");
            if (failed > 0 && changed == 0)
            {
                throw new CpfsException("Err_OwnerNotChanged", changed, failed);
            }

            if (failed > 0)
            {
                throw new StepPartialException("Err_OwnerPartial", changed, failed);
            }
        },
    };

    private static LambdaStep Ace(UninstallRun run) => new LambdaStep
    {
        Read = () => run.UserSid is null || !Directory.Exists(run.SharePath) || !Folders.HasExplicit(run.SharePath, run.UserSid),
        Do = () => Folders.RemoveExplicit(run.SharePath, run.UserSid!),
        Check = () => !Folders.HasExplicit(run.SharePath, run.UserSid!),
    };

    private static LambdaStep Profile(UninstallRun run) => new LambdaStep
    {
        Read = () => run.UserSid is null || Profiles.PathOf(run.UserSid.Value) is null,
        Do = () => Profiles.Delete(run.UserSid!.Value),
        Check = () => Profiles.PathOf(run.UserSid!.Value) is null,
    };

    private static LambdaStep DeleteUser(UninstallRun run) => new LambdaStep
    {
        Read = () => LocalAccounts.GetUser(run.User) is null,
        Do = () =>
        {
            Accounts.Delete(run.User);
            Reg.Delete(Reg.UserList, run.User);
        },
        Check = () => LocalAccounts.GetUser(run.User) is null,
    };

    // The account CPFS took over gets back what it had: enabled or not, password expiry, group membership, and its
    // place on the sign-in screen.
    private static LambdaStep Before(UninstallRun run)
    {
        var before = run.Config!.TrackedUserBefore(run.User)!;
        bool Same()
        {
            var user = LocalAccounts.GetUser(run.User);
            if (user is null)
            {
                return true;
            }

            return (before.Enabled ?? user.Enabled) == user.Enabled
                && (before.PasswordNeverExpires ?? user.PasswordNeverExpires) == user.PasswordNeverExpires
                && (LocalAccounts.IsMember(user.Sid, Accounts.AdminsSid) == true) == Has(before.Groups, Accounts.AdminsSid)
                && (LocalAccounts.IsMember(user.Sid, Accounts.UsersSid) == true) == Has(before.Groups, Accounts.UsersSid)
                && Reg.ReadDword(Reg.UserList, run.User) == before.HiddenValue;
        }

        return new LambdaStep
        {
            Read = Same,
            Do = () =>
            {
                var user = LocalAccounts.GetUser(run.User)!;
                Accounts.SetFlags(run.User, f =>
                {
                    if (before.Enabled is { } enabled)
                    {
                        f = enabled ? f & ~WinApi.UfAccountDisable : f | WinApi.UfAccountDisable;
                    }

                    if (before.PasswordNeverExpires is { } never)
                    {
                        f = never ? f | WinApi.UfDontExpirePasswd : f & ~WinApi.UfDontExpirePasswd;
                    }

                    return f;
                });
                foreach (var group in new[] { Accounts.AdminsSid, Accounts.UsersSid })
                {
                    if (Has(before.Groups, group))
                    {
                        Accounts.AddToGroup(user.Sid, group);
                    }
                    else
                    {
                        Accounts.RemoveFromGroup(user.Sid, group);
                    }
                }

                if (before.HiddenValue is { } hidden)
                {
                    Reg.SetDword(Reg.UserList, run.User, hidden);
                }
                else
                {
                    Reg.Delete(Reg.UserList, run.User);
                }
            },
            Check = Same,
        };
    }

    private static bool Has(IReadOnlyList<string> list, string value)
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

    private static LambdaStep Admin(UninstallRun run) => new LambdaStep
    {
        Read = () => LocalAccounts.GetUser(run.User) is not { } u || LocalAccounts.IsMember(u.Sid, Accounts.AdminsSid) == true,
        Do = () => Accounts.AddToGroup(LocalAccounts.GetUser(run.User)!.Sid, Accounts.AdminsSid),
        Check = () => LocalAccounts.IsMember(LocalAccounts.GetUser(run.User)!.Sid, Accounts.AdminsSid) == true,
    };

    private static LambdaStep Unhide(UninstallRun run) => new LambdaStep
    {
        Read = () => !Reg.Exists(Reg.UserList, run.User),
        Do = () => Reg.Delete(Reg.UserList, run.User),
        Check = () => !Reg.Exists(Reg.UserList, run.User),
    };

    private static LambdaStep Rules(string name) => new LambdaStep
    {
        Read = () => Firewall.Count(name) == 0,
        Do = () => Firewall.RemoveAll(name),
        Check = () => Firewall.Count(name) == 0,
    };

    private static LambdaStep BuiltIn(string id) => new LambdaStep
    {
        Read = () => Firewall.BuiltInEnabled(id) != false,
        Do = () => Firewall.SetBuiltIn(id, enable: true),
        Check = () => Firewall.BuiltInEnabled(id) != false,
    };

    // Back to the value recorded before CPFS, written as a DWORD as the scripts do, or deleted when it did not exist;
    // never below a fresh Windows (HardeningValue.RestoreValue), the same rule the planner shows.
    private static LambdaStep Registry(UninstallRun run, string name)
    {
        RegistryBackup? entry = null;
        foreach (var r in run.Config!.BackupRegistry)
        {
            if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                entry = r;
            }
        }

        var location = entry?.Location;
        if (entry is null || location is null || location.Root != RegistryRoot.LocalMachine)
        {
            throw new CpfsException("Err_BackupNotRestorable", name);
        }

        // Present but without a value: the original is unknown, so nothing is written (the planner shows it as kept).
        if (entry.Exists == true && entry.Value is null)
        {
            return new LambdaStep { Read = () => true };
        }

        var recorded = entry.Exists == true ? (int?)entry.Value : null;
        var hardening = Planner.HardeningFor(name);
        var original = hardening is null ? recorded : (int?)hardening.RestoreValue(recorded);
        if (original != recorded)
        {
            AppLog.Write("uninstall", name + ": the recorded value is below the Windows default; the default " + original?.ToString(CultureInfo.InvariantCulture) + " is written instead");
        }

        bool Same() => original is { } o ? Reg.ReadDword(location.SubKey, name) == o : !Reg.Exists(location.SubKey, name);
        return new LambdaStep
        {
            Read = Same,
            Do = () =>
            {
                if (original is { } o)
                {
                    Reg.SetDword(location.SubKey, name, o);
                }
                else
                {
                    Reg.Delete(location.SubKey, name);
                }
            },
            Check = Same,
        };
    }

    private static LambdaStep ServerFlag(string property, Func<SmbServerInfo, bool?> read, bool original) => new LambdaStep
    {
        Read = () => read(PlanReader.SmbServer()) == original,
        Do = () => SmbServer.Set(property, original),
        Check = () => read(PlanReader.SmbServer()) == original,
    };

    private static LambdaStep ServerStart(UninstallRun run)
    {
        var start = run.Config!.BackupServerStart switch
        {
            "Automatic" => 2,
            "Manual" => 3,
            "Disabled" => 4,
            _ => throw new CpfsException("Err_StartTypeNotRestorable"),
        };
        return new LambdaStep
        {
            Read = () => PlanReader.ServerStart() == start,
            Do = () => Service.SetStart(Service.Server, start),
            Check = () => PlanReader.ServerStart() == start,
        };
    }

    private static LambdaStep LockoutStep(UninstallRun run)
    {
        var c = run.Config!;
        var threshold = (uint)c.BackupLockout!.Value;
        bool Same()
        {
            var now = PlanReader.Lockout();
            return now.Threshold == threshold && (c.BackupLockoutDuration is not { } d || now.DurationSeconds == d)
                && (c.BackupLockoutWindow is not { } w || now.WindowSeconds == w);
        }

        return new LambdaStep
        {
            Read = Same,
            Do = () =>
            {
                var now = PlanReader.Lockout();
                Lockout.Set(threshold, (uint)(c.BackupLockoutDuration ?? now.DurationSeconds), (uint)(c.BackupLockoutWindow ?? now.WindowSeconds));
            },
            Check = Same,
        };
    }

    private const string Lsa = @"SYSTEM\CurrentControlSet\Control\Lsa";
    private const string LanmanParameters = @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters";

    // An install without a backup (0.1): the scripts' Windows-defaults path.
    private static LambdaStep Revert()
    {
        static bool Defaults() => !Reg.Exists(LanmanParameters, "AutoShareWks") && !Reg.Exists(Lsa, "LmCompatibilityLevel")
            && Reg.ReadDword(Lsa, "RestrictAnonymous") == 0;
        return new LambdaStep
        {
            Read = Defaults,
            Do = () =>
            {
                Reg.Delete(LanmanParameters, "AutoShareWks");
                Reg.Delete(Lsa, "LmCompatibilityLevel");
                Reg.SetDword(Lsa, "RestrictAnonymous", 0);
            },
            Check = Defaults,
        };
    }

    private static bool NonePrivate(string name)
    {
        foreach (var n in PlanReader.Networks())
        {
            if (string.Equals(n.Name, name, StringComparison.Ordinal) && n.Category == 1)
            {
                return false;
            }
        }

        return !Networks.AnySavedPrivate(name);
    }

    private static LambdaStep Network(string name) => new LambdaStep
    {
        Read = () => NonePrivate(name),
        Do = () =>
        {
            Networks.SetCategory(name, 1, 0);
            Networks.SetSavedPublic(name);
        },
        Check = () => NonePrivate(name),
    };

    // Before anything changes: the backup must not vanish with ProgramData\CPFS (it holds no secret). A new file
    // every time, never over an existing one.
    private static LambdaStep SaveCopy(UninstallRun run) => new LambdaStep
    {
        Do = () =>
        {
            var folder = run.Documents ?? throw new CpfsException("Err_NoDocuments");
            var bytes = run.ConfigBytes ?? throw new CpfsException("Err_ConfigNotRead");
            var stem = Path.Combine(folder, "CPFS-config-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            run.SavedCopy = WriteNew(stem, bytes);
            AppLog.Write("uninstall", "A copy of config.json was saved to " + run.SavedCopy);
        },
        Check = () => run.SavedCopy is not null && File.Exists(run.SavedCopy),
    };

    // The settings files Setup set aside, each read without following a link (a link or hard link is refused) and
    // written as a new file in Documents. One that can't be copied is reported as not done; u.data then keeps the folder.
    private static LambdaStep SaveIgnored(UninstallRun run) => new LambdaStep
    {
        Do = () =>
        {
            var folder = run.Documents ?? throw new CpfsException("Err_NoDocuments");
            var failed = new List<string>();
            foreach (var name in IgnoredFiles())
            {
                try
                {
                    var bytes = PlainFiles.ReadAll(Path.Combine(StateReader.DataFolder, name));
                    var path = WriteNew(Path.Combine(folder, "CPFS-" + Path.GetFileNameWithoutExtension(name)), bytes);
                    run.IgnoredCopied.Add(name);
                    AppLog.Write("uninstall", "A copy of " + name + " was saved to " + path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add(name);
                    AppLog.Write("uninstall", "Could not copy " + name + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (failed.Count > 0)
            {
                throw new StepUnsupportedException("Err_IgnoredNotCopied", string.Join(", ", failed));
            }
        },
        Check = () => IgnoredFiles().All(run.IgnoredCopied.Contains),
    };

    private static List<string> IgnoredFiles()
    {
        var list = new List<string>();
        if (!Directory.Exists(StateReader.DataFolder))
        {
            return list;
        }

        foreach (var file in Directory.EnumerateFiles(StateReader.DataFolder))
        {
            var name = Path.GetFileName(file);
            if (ConfigTrust.IsIgnoredFileName(name))
            {
                list.Add(name);
            }
        }

        return list;
    }

    // A new file every time, never over an existing one: stem.json, else stem-1.json and so on.
    private static string WriteNew(string stem, byte[] bytes)
    {
        for (var n = 0; ; n++)
        {
            var path = n == 0 ? stem + ".json" : stem + "-" + n.ToString(CultureInfo.InvariantCulture) + ".json";
            FileStream file;
            try
            {
                file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path) && n < 20)
            {
                continue;
            }

            using (file)
            {
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }

            return path;
        }
    }

    // The Auto-Private tasks go before ProgramData\CPFS, which holds the program they run.
    private static LambdaStep Task(string name) => new LambdaStep
    {
        Read = () => !AutoPrivateTask.Exists(name),
        Do = () => AutoPrivateTask.Delete(name),
        Check = () => !AutoPrivateTask.Exists(name),
    };

    // The copy in Program Files\CPFS, after the task that ran it is gone; it may still be closing after its last run.
    private static LambdaStep AutoCopy() => new LambdaStep
    {
        Read = () => !File.Exists(AutoPrivateCopy.Path),
        Do = AutoPrivateCopy.Remove,
        Check = () => !File.Exists(AutoPrivateCopy.Path),
    };

    // The folder CPFS created for the copy, only when nothing is left in it; otherwise it stays and the rest goes on.
    private static LambdaStep AutoFolder() => new LambdaStep
    {
        Read = () => !Directory.Exists(AutoPrivateCopy.Folder),
        Do = () =>
        {
            if (Directory.EnumerateFileSystemEntries(AutoPrivateCopy.Folder).Any())
            {
                throw new StepPartialException("Err_FolderNotEmpty", AutoPrivateCopy.Folder);
            }

            AutoPrivateCopy.RemoveFolder();
        },
        Check = () => !Directory.Exists(AutoPrivateCopy.Folder),
    };

    // The name from the first Setup's backup, checked before Windows is asked for it; Windows takes it after a restart.
    private static LambdaStep Computer(UninstallRun run)
    {
        var original = run.Config?.BackupHostName?.Trim() ?? "";
        bool Same() => string.Equals(PlanReader.ComputerName(), original, StringComparison.OrdinalIgnoreCase);
        return new LambdaStep
        {
            Read = Same,
            Do = () =>
            {
                if (!Cpfs.Core.Validation.Validators.ComputerName(original).IsValid)
                {
                    throw new StepUnsupportedException("Err_BackupNameInvalid", original);
                }

                ComputerRename.Set(original);
                run.RenamedTo = original;
            },
            Check = () =>
            {
                AppLog.Write("uninstall", "Rename: NV Hostname now " + (PlanReader.ComputerName() ?? "absent"));
                return Same();
            },
        };
    }

    // The exe copy in it may still be closing after the task's last run. A set-aside settings file that wasn't copied
    // to Documents keeps the whole folder: reported as not done, nothing deleted.
    private static LambdaStep Data(UninstallRun run) => new LambdaStep
    {
        Read = () => !Directory.Exists(StateReader.DataFolder),
        Do = () =>
        {
            var left = IgnoredFiles().Where(n => !run.IgnoredCopied.Contains(n)).ToList();
            if (left.Count > 0)
            {
                throw new StepUnsupportedException("Err_DataKeptIgnored", StateReader.DataFolder, string.Join(", ", left));
            }

            AutoPrivateCopy.Retry(() => Directory.Delete(StateReader.DataFolder, recursive: true));
        },
        Check = () => !Directory.Exists(StateReader.DataFolder),
    };
}
