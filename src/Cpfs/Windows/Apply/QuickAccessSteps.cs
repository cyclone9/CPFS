using System;
using System.IO;
using Cpfs.Core.Apply;
using Cpfs.Core.Config;
using Cpfs.Core.Planning;
using Cpfs.Core.Text;

namespace Cpfs.Windows.Apply;

// The drive letter, its name and the desktop shortcut, for Setup and Uninstall. Each step reads again first, and
// removes or replaces only what config.json recorded as CPFS's and still finds as CPFS left it.
internal static class QuickAccessSteps
{
    // Setup: the previous letter's name, removed, or put back to what it was before CPFS.
    public static LambdaStep OldLabel(SetupRun run, PlanItem item)
    {
        var letter = run.OldLetter ?? throw new CpfsException("Err_NoOldLetter");
        var original = run.Original!.BackupDriveLabel(letter);
        bool AtTarget() => original is null ? DriveNames.Read(letter) is null : string.Equals(DriveNames.Read(letter), original, StringComparison.Ordinal);
        return new LambdaStep
        {
            Read = AtTarget,
            Do = () =>
            {
                var current = DriveNames.Read(letter);
                if (!Ours(current, run.Original))
                {
                    throw new CpfsException("Err_DriveNameChanged", letter);
                }

                if (original is null)
                {
                    DriveNames.Delete(letter);
                }
                else
                {
                    DriveNames.Set(letter, original);
                }
            },
            TrackWith = b => b!.SetField("DriveLabel", null),
            Check = AtTarget,
        };
    }

    // Setup: the previous mapping in the user's session. The verb checks the letter and folder against config.json,
    // so the folder it pointed to is recorded first (an install the scripts made has only SharePath).
    public static LambdaStep OldSession(SetupRun run)
    {
        var letter = run.OldLetter ?? throw new CpfsException("Err_NoOldLetter");
        var path = run.Original!.OwnDrivePath ?? throw new CpfsException("Err_NoOldDriveFolder");
        return new LambdaStep
        {
            RecordWith = b =>
            {
                if (run.Original.TrackedDrivePath is not null)
                {
                    return BackupWrite.NotNeeded;
                }

                b!.TrackCreated("DrivePath", path);
                return BackupWrite.Recorded;
            },
            Do = () => SessionDrive.Unmap(letter, path),
        };
    }

    // Setup: the previous DOS Devices value, only while it still points at the folder CPFS recorded.
    public static LambdaStep OldMap(SetupRun run)
    {
        var letter = run.OldLetter ?? throw new CpfsException("Err_NoOldLetter");
        var target = Planner.DosTarget(run.Original!.OwnDrivePath ?? throw new CpfsException("Err_NoOldDriveFolder"));
        return new LambdaStep
        {
            Read = () => DosDrives.Read(letter) is null,
            Do = () =>
            {
                if (!Same(DosDrives.Read(letter), target))
                {
                    throw new CpfsException("Err_DriveForeign", letter);
                }

                DosDrives.Delete(letter);
                run.DriveRestartNeeded = true;
            },
            TrackWith = b =>
            {
                b!.SetField("DriveLetter", null);
                b.TrackCreated("DrivePath", null);
            },
            Check = () => DosDrives.Read(letter) is null,
        };
    }

    // Setup: the DOS Devices value, recorded in config.json before it is written.
    public static LambdaStep Map(SetupRun run)
    {
        var letter = run.Letter;
        var target = Planner.DosTarget(run.SharePath);
        return new LambdaStep
        {
            Read = () => Same(DosDrives.Read(letter), target),
            RecordWith = b =>
            {
                b!.SetField("DriveLetter", letter);
                b.TrackCreated("DrivePath", run.SharePath);
                return BackupWrite.Recorded;
            },
            Do = () =>
            {
                if (DosDrives.Read(letter) is { } other)
                {
                    throw new CpfsException("Err_DriveAppeared", letter, other);
                }

                DosDrives.Set(letter, target);
            },
            Check = () => Same(DosDrives.Read(letter), target),
        };
    }

    // Setup: the same mapping in the user's session now, so File Explorer shows it without a restart. Nothing an
    // elevated process can read tells whether it worked; the verb's exit code does.
    public static LambdaStep Session(SetupRun run) => new LambdaStep
    {
        Do = () => SessionDrive.Map(run.Letter, run.SharePath),
    };

    // Setup: the drive's name. One CPFS did not set is saved in Backup.DriveLabels first.
    public static LambdaStep Label(SetupRun run, PlanItem item)
    {
        var letter = run.Letter;
        var name = run.DriveName;
        return new LambdaStep
        {
            Read = () => string.Equals(DriveNames.Read(letter), name, StringComparison.Ordinal),
            RecordWith = b =>
            {
                if (item.BackupNeeded && DriveNames.Read(letter) is { } before && b!.BackupDriveLabel(letter, before) == BackupWrite.NoBackup)
                {
                    return BackupWrite.NoBackup;
                }

                b!.SetField("DriveLabel", name);
                return BackupWrite.Recorded;
            },
            Do = () =>
            {
                DriveNames.Set(letter, name);
                AppLog.Write("apply", DriveNames.VolumeLabelOf(run.SharePath) is { } volume
                    ? "Drive name " + letter + ": set to '" + name + "'; the folder's disk is named '" + volume + "', and File Explorer may show that name instead"
                    : "Drive name " + letter + ": set to '" + name + "'");
            },
            Check = () => string.Equals(DriveNames.Read(letter), name, StringComparison.Ordinal),
        };
    }

    // Setup: the previous letter's CPFS icon, removed, or put back to the icon it had before CPFS.
    public static LambdaStep OldIcon(SetupRun run)
    {
        var letter = run.OldLetter ?? throw new CpfsException("Err_NoOldLetter");
        var original = run.Original!.BackupDriveIcon(letter);
        bool AtTarget() => original is null ? DriveIcons.Read(letter) is null : string.Equals(DriveIcons.Read(letter), original, StringComparison.OrdinalIgnoreCase);
        return new LambdaStep
        {
            Read = AtTarget,
            Do = () =>
            {
                if (!Planner.IsOwnIcon(DriveIcons.Read(letter), CpfsIconFile.Path))
                {
                    throw new CpfsException("Err_DriveIconChanged", letter);
                }

                if (original is null)
                {
                    DriveIcons.Delete(letter);
                }
                else
                {
                    DriveIcons.Set(letter, original);
                }
            },
            TrackWith = b => b!.TrackCreated("DriveIcon", null),
            Check = AtTarget,
        };
    }

    // Setup: the CPFS icon for the drive, from the icon file in the data folder. One CPFS did not set is replaced
    // only when the user ticked it, and saved in Backup.DriveIcons first.
    public static LambdaStep Icon(SetupRun run, PlanItem item)
    {
        var letter = run.Letter;
        var icon = CpfsIconFile.Path;
        bool AtTarget() => Planner.IsOwnIcon(DriveIcons.Read(letter), icon) && File.Exists(icon);
        return new LambdaStep
        {
            Read = AtTarget,
            RecordWith = b =>
            {
                var before = DriveIcons.Read(letter);
                if (before is not null && !Planner.IsOwnIcon(before, icon))
                {
                    if (item.Action != PlanAction.Ask)
                    {
                        throw new CpfsException("Err_DriveIconAppeared", letter);
                    }

                    if (item.BackupNeeded && b!.BackupDriveIcon(letter, before) == BackupWrite.NoBackup)
                    {
                        return BackupWrite.NoBackup;
                    }
                }

                b!.TrackCreated("IconFile", icon);
                b.TrackCreated("DriveIcon", icon);
                return BackupWrite.Recorded;
            },
            Do = () =>
            {
                CpfsIconFile.Write();
                DriveIcons.Set(letter, icon);
            },
            Check = AtTarget,
        };
    }

    // Setup: CPFS's entry in File Explorer's navigation pane, in the hive of the desktop user this instance found
    // itself. Recorded in config.json before it is written; removed (the switch is off) only while it opens the
    // recorded folder.
    public static LambdaStep Explorer(SetupRun run, PlanItem item)
    {
        var sid = DesktopUser.Current.Sid ?? throw new CpfsException(SessionDrive.NoDesktopUserKey);
        var recorded = run.Original?.TrackedExplorerEntry;
        if (item.Action == PlanAction.RemoveIfOurs)
        {
            var old = recorded?.Folder ?? throw new CpfsException("Err_NoExplorerEntry");
            bool Gone() => ExplorerEntryKeys.Read(sid).Entry is null;
            return new LambdaStep
            {
                Read = Gone,
                Do = () =>
                {
                    if (ExplorerEntryKeys.Read(sid).Entry is { } now && !ExplorerEntry.SameFolder(now.TargetFolderPath, old))
                    {
                        throw new StepUnsupportedException("Err_ExplorerOtherFolder");
                    }

                    ExplorerEntryKeys.Delete(sid);
                },
                TrackWith = b => b!.TrackCreated("ExplorerEntry", null),
                Check = Gone,
            };
        }

        var name = run.DriveName;
        var folder = run.SharePath;
        var icon = CpfsIconFile.Path;
        bool AtTarget() => ExplorerEntryKeys.Read(sid).Entry is { } e && ExplorerEntry.IsAtTarget(e, name, folder, icon) && File.Exists(icon);
        return new LambdaStep
        {
            Read = AtTarget,
            RecordWith = b =>
            {
                var now = ExplorerEntryKeys.Read(sid);
                if (!now.Loaded)
                {
                    throw new CpfsException("Err_UserSettingsNotLoaded");
                }

                var ours = now.Entry is { } e && recorded is not null && ExplorerEntry.SameFolder(e.TargetFolderPath, recorded.Folder);
                if (now.Entry is not null && !ours && item.Action != PlanAction.Ask)
                {
                    throw new CpfsException("Err_ExplorerAppeared");
                }

                b!.TrackExplorerEntry(sid, folder, name);
                b.TrackCreated("IconFile", icon);
                return BackupWrite.Recorded;
            },
            Do = () =>
            {
                CpfsIconFile.Write();
                ExplorerEntryKeys.Write(sid, name, folder, icon);
                AppLog.Write("apply", "File Explorer entry " + ExplorerEntry.Clsid + " written for " + sid);
            },
            Check = AtTarget,
        };
    }

    // Setup: CPFS's own shortcut, made before it carried an icon, gets the CPFS icon.
    public static LambdaStep ShortcutIcon(SetupRun run)
    {
        var file = FindShortcutFile(run) ?? throw new CpfsException("Err_ShortcutNoPath");
        var icon = CpfsIconFile.Path;
        bool AtTarget() => string.Equals(Shortcuts.IconOf(file), icon, StringComparison.OrdinalIgnoreCase) && File.Exists(icon);
        return new LambdaStep
        {
            Read = AtTarget,
            RecordWith = b =>
            {
                b!.TrackCreated("IconFile", icon);
                return BackupWrite.Recorded;
            },
            Do = () =>
            {
                if (Shortcuts.IconOf(file) is not { Length: 0 })
                {
                    throw new StepUnsupportedException("Err_ShortcutOwnIcon", Path.GetFileName(file));
                }

                CpfsIconFile.Write();
                Shortcuts.SetIcon(file, icon);
            },
            Check = AtTarget,
        };
    }

    private static string? FindShortcutFile(SetupRun run)
    {
        foreach (var other in run.Plan.Items)
        {
            if (other.Key == "shortcut.file" && other.Subject.Data is { Length: > 0 } path)
            {
                return path;
            }
        }

        return null;
    }

    // Setup: the shortcut recorded under another name. It goes only while it still points at the folder it was made for.
    public static LambdaStep OldShortcut(SetupRun run)
    {
        var file = run.Original?.Shortcut ?? throw new CpfsException("Err_NoOldShortcut");
        return new LambdaStep
        {
            Read = () => !File.Exists(file),
            Do = () =>
            {
                var target = Shortcuts.TargetOf(file);
                if (!SamePath(target, run.Original!.SharePath) && !SamePath(target, run.SharePath))
                {
                    throw new StepUnsupportedException("Err_ShortcutElsewhere", Path.GetFileName(file));
                }

                Shortcuts.Delete(file);
            },
            TrackWith = b => b!.SetField("Shortcut", null),
            Check = () => !File.Exists(file),
        };
    }

    // Setup: the shortcut, recorded before it is created; a file CPFS did not record is never overwritten.
    public static LambdaStep Shortcut(SetupRun run, PlanItem item)
    {
        var file = item.Subject.Data ?? throw new CpfsException("Err_ShortcutNoPath");
        bool AtTarget() => File.Exists(file) && SamePath(Shortcuts.TargetOf(file), run.SharePath);
        if (item.Action == PlanAction.Change)
        {
            return new LambdaStep
            {
                Read = AtTarget,
                Do = () =>
                {
                    var target = Shortcuts.TargetOf(file);
                    if (!SamePath(target, run.Original?.SharePath))
                    {
                        throw new StepUnsupportedException("Err_ShortcutOldElsewhere", Path.GetFileName(file));
                    }

                    Shortcuts.Retarget(file, run.SharePath, run.ShortcutDescription);
                },
                Check = AtTarget,
            };
        }

        return new LambdaStep
        {
            Read = () =>
            {
                if (!File.Exists(file))
                {
                    return false;
                }

                if (AtTarget() && string.Equals(run.Original?.Shortcut, file, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                throw new CpfsException("Err_FileAppeared", Path.GetFileName(file));
            },
            RecordWith = b =>
            {
                b!.SetField("Shortcut", file);
                b.TrackCreated("IconFile", CpfsIconFile.Path);
                return BackupWrite.Recorded;
            },
            Do = () =>
            {
                CpfsIconFile.Write();
                Shortcuts.Create(file, run.SharePath, run.ShortcutDescription, CpfsIconFile.Path);
            },
            Check = () => AtTarget() && string.Equals(Shortcuts.IconOf(file), CpfsIconFile.Path, StringComparison.OrdinalIgnoreCase),
        };
    }

    // Uninstall: the drive's name, removed or put back.
    public static LambdaStep UninstallLabel(UninstallRun run)
    {
        var letter = RecordedLetter(run.Config);
        var original = run.Config!.BackupDriveLabel(letter);
        bool AtTarget() => original is null ? DriveNames.Read(letter) is null : string.Equals(DriveNames.Read(letter), original, StringComparison.Ordinal);
        return new LambdaStep
        {
            Read = AtTarget,
            Do = () =>
            {
                var current = DriveNames.Read(letter);
                if (original is not null && (current is null || Ours(current, run.Config)))
                {
                    DriveNames.Set(letter, original);
                    return;
                }

                if (original is not null || !Ours(current, run.Config))
                {
                    throw new CpfsException("Err_DriveNameChanged", letter);
                }

                DriveNames.Delete(letter);
            },
            Check = AtTarget,
        };
    }

    // Uninstall: the CPFS icon removed, or the icon from before CPFS put back; anyone else's icon is left alone.
    public static LambdaStep UninstallIcon(UninstallRun run)
    {
        var letter = RecordedLetter(run.Config);
        var original = run.Config!.BackupDriveIcon(letter);
        bool AtTarget() => original is null ? DriveIcons.Read(letter) is null : string.Equals(DriveIcons.Read(letter), original, StringComparison.OrdinalIgnoreCase);
        return new LambdaStep
        {
            Read = AtTarget,
            Do = () =>
            {
                var current = DriveIcons.Read(letter);
                if (current is not null && !Planner.IsOwnIcon(current, CpfsIconFile.Path))
                {
                    throw new CpfsException("Err_DriveIconChanged", letter);
                }

                if (original is null)
                {
                    DriveIcons.Delete(letter);
                }
                else
                {
                    DriveIcons.Set(letter, original);
                }
            },
            Check = AtTarget,
        };
    }

    // Setup and Uninstall: a CPFS-RunAsUser task left from a run that stopped. CPFS's own exists only inside the
    // session step, which runs after this one.
    public static LambdaStep StaleTask() => new LambdaStep
    {
        Read = () => !RunAsUser.StaleExists(),
        Do = RunAsUser.DeleteStale,
        Check = () => !RunAsUser.StaleExists(),
    };

    // Uninstall: the mapping in the user's session (config.json is still there for the verb to check).
    public static LambdaStep UninstallSession(UninstallRun run)
    {
        var letter = RecordedLetter(run.Config);
        var path = run.Config!.OwnDrivePath ?? throw new CpfsException("Err_NoDriveFolder");
        return new LambdaStep { Do = () => SessionDrive.Unmap(letter, path) };
    }

    // Uninstall: the DOS Devices value, only while it points at the recorded folder.
    public static LambdaStep UninstallMap(UninstallRun run)
    {
        var letter = RecordedLetter(run.Config);
        var target = Planner.DosTarget(run.Config!.OwnDrivePath ?? throw new CpfsException("Err_NoDriveFolder"));
        return new LambdaStep
        {
            Read = () => DosDrives.Read(letter) is null,
            Do = () =>
            {
                if (!Same(DosDrives.Read(letter), target))
                {
                    throw new CpfsException("Err_DriveForeign", letter);
                }

                DosDrives.Delete(letter);
                run.DriveRestartNeeded = true;
            },
            Check = () => DosDrives.Read(letter) is null,
        };
    }

    // Uninstall: the File Explorer entry, from the hive of the user it was recorded for, only while it opens the
    // recorded folder.
    public static LambdaStep UninstallExplorer(UninstallRun run)
    {
        var recorded = run.Config?.TrackedExplorerEntry;
        var sid = recorded?.Sid ?? throw new CpfsException("Err_NoExplorerEntry");
        var folder = recorded.Folder ?? throw new CpfsException("Err_NoExplorerFolder");
        bool Gone() => ExplorerEntryKeys.Read(sid) is { Loaded: true, Entry: null };
        return new LambdaStep
        {
            Read = Gone,
            Do = () =>
            {
                var now = ExplorerEntryKeys.Read(sid);
                if (!now.Loaded)
                {
                    throw new StepUnsupportedException("Err_ExplorerUserAway");
                }

                if (now.Entry is { } e && !ExplorerEntry.SameFolder(e.TargetFolderPath, folder))
                {
                    throw new StepUnsupportedException("Err_ExplorerOtherFolder");
                }

                ExplorerEntryKeys.Delete(sid);
            },
            Check = Gone,
        };
    }

    // Uninstall: the recorded shortcut, only while it points at the shared folder.
    public static LambdaStep UninstallShortcut(UninstallRun run)
    {
        var file = run.Config?.Shortcut ?? throw new CpfsException("Err_NoShortcut");
        return new LambdaStep
        {
            Read = () => !File.Exists(file),
            Do = () =>
            {
                if (!SamePath(Shortcuts.TargetOf(file), run.SharePath))
                {
                    throw new StepUnsupportedException("Err_ShortcutElsewhere", Path.GetFileName(file));
                }

                Shortcuts.Delete(file);
            },
            Check = () => !File.Exists(file),
        };
    }

    private static char RecordedLetter(ConfigDocument? config) =>
        config?.DriveLetter is { Length: 1 } l ? char.ToUpperInvariant(l[0]) : throw new CpfsException("Err_NoLetterRecorded");

    private static bool Ours(string? label, ConfigDocument? config) =>
        label is not null && config?.OwnDriveLabel is { } own && string.Equals(label, own, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
