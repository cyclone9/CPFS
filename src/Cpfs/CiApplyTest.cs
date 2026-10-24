using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Cpfs.Core.Apply;
using Cpfs.Core.Config;
using Cpfs.Core.Drives;
using Cpfs.Core.Planning;
using Cpfs.Windows;
using Cpfs.Windows.Apply;
using Microsoft.Win32;

namespace Cpfs;

// "Cpfs.exe --ci-apply-test": Setup with test values, checks, Uninstall, and checks that everything is back, on a
// disposable GitHub Actions runner only. It refuses anywhere else: it creates an account, a share and firewall rules
// and changes security settings. The password is random and never printed. Exit 0 when every check passed, 1 when
// one failed, 2 when it refused to run.
internal static class CiApplyTest
{
    public const string Verb = "--ci-apply-test";

    private const string TestUser = "CpfsCiUser";
    private const string TestShare = "CPFS-CI";

    private static readonly List<(bool Pass, bool Skipped, string Check, string Detail)> Rows = new();

    private static char? _letter;
    private static string? _shortcut;

    // The desktop user the File Explorer entry goes to: on the runner, the runner's own account (its explorer.exe).
    private static string? _entrySid;

    public static int Run()
    {
        SelfTest.AttachToParentConsole();
        var o = Console.Out;
        try
        {
            o.WriteLine("CPFS apply test " + AppLog.VersionText());
            if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal))
            {
                return Refuse(o, "GITHUB_ACTIONS is not true: this test runs only on a GitHub Actions runner.");
            }

            if (PartOfDomain())
            {
                return Refuse(o, "This machine is a domain member.");
            }

            Startup.Init(Array.Empty<string>());
            if (!Startup.IsElevated)
            {
                return Refuse(o, "Not elevated.");
            }

            if (Directory.Exists(StateReader.DataFolder) || LocalAccounts.GetUser(TestUser) is not null)
            {
                return Refuse(o, "Not a clean machine: ProgramData\\CPFS or the test account already exists.");
            }

            var before = Machine.Read();
            var temp = Environment.GetEnvironmentVariable("RUNNER_TEMP") is { Length: > 0 } t ? t : Path.GetTempPath();
            var folder = Path.Combine(temp, "cpfs-ci", "Shared");
            try
            {
                SetupPhase(o, folder, temp);
            }
            finally
            {
                if (File.Exists(StateReader.ConfigPath))
                {
                    UninstallPhase(o);
                }
            }

            Compare(before, Machine.Read(), folder);
            o.WriteLine();
            o.WriteLine("Result:");
            foreach (var (pass, skipped, check, detail) in Rows)
            {
                o.WriteLine((skipped ? "SKIP  " : pass ? "PASS  " : "FAIL  ") + check + (detail.Length == 0 ? "" : "  (" + detail + ")"));
            }

            var failed = Rows.Count(r => !r.Pass);
            var skips = Rows.Count(r => r.Skipped);
            var ran = Rows.Count - skips;
            o.WriteLine((failed == 0 ? "All " + ran + " checks passed" : failed + " of " + ran + " checks failed") + (skips == 0 ? "" : ", " + skips + " skipped"));
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            o.WriteLine("Result: unexpected exception");
            o.WriteLine(ex.ToString());
            AppLog.Exception("ci-apply", ex);
            return 1;
        }
        finally
        {
            o.Flush();
        }
    }

    private static int Refuse(TextWriter o, string why)
    {
        o.WriteLine("Refused: " + why);
        return 2;
    }

    private static void Check(bool pass, string check, string detail = "") => Rows.Add((pass, false, check, detail));

    // Not run, and not a failure: the run's result stays a pass.
    private static void Skip(string check, string detail) => Rows.Add((true, true, check, detail));

    private static bool PartOfDomain()
    {
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT PartOfDomain FROM Win32_ComputerSystem");
        using var results = searcher.Get();
        foreach (ManagementBaseObject cs in results)
        {
            using (cs)
            {
                return cs["PartOfDomain"] as bool? ?? true;
            }
        }

        return true;
    }

    private static SystemSnapshot Read(string user, string path, string share) =>
        PlanReader.ReadAnswerParts(PlanReader.ReadStatic(DriveLetters.Scan(null), DesktopUser.Current.Sid), user, path, share);

    private static void SetupPhase(TextWriter o, string folder, string temp)
    {
        var snapshot = Read(TestUser, folder, TestShare);
        _letter = snapshot.Letters.DefaultLetter();
        Check(_letter is not null, "a free drive letter for the test", _letter?.ToString() ?? "none");
        _entrySid = DesktopUser.Current.Sid;
        if (_entrySid is null)
        {
            Skip("the File Explorer entry", "no desktop user on this runner: " + DesktopUser.Current.Describe());
        }

        var answers = new SetupAnswers
        {
            SharePath = folder,
            ShareName = TestShare,
            SmbUser = TestUser,
            Password = PasswordEntry.Valid,
            Drive = _letter is not null,
            DriveLetter = _letter,
            DriveName = SetupDefaults.DriveNameFor(TestShare),
            ExplorerEntry = _entrySid is not null,
            Shortcut = true,
            ShortcutName = SetupDefaults.ShortcutNameFor(TestShare),
        };

        var plan = Planner.Setup(snapshot, answers, null, PlanReader.ProtectedFolders());

        // Public networks are ticked, so switching to Private and back is exercised too.
        var choices = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in plan.Items)
        {
            if (item.Action == PlanAction.Ask && item.Key.StartsWith("net:", StringComparison.Ordinal))
            {
                choices[item.Key] = true;
            }
        }

        answers = answers with { Choices = choices };
        plan = Planner.Setup(snapshot, answers, null, PlanReader.ProtectedFolders());
        o.WriteLine("Setup plan:");
        foreach (var line in plan.LogLines())
        {
            o.WriteLine("  " + line);
        }

        var blocking = ApplyList.Blocking(plan);
        Check(blocking.Count == 0, "Setup plan has nothing that cannot be planned", string.Join(", ", blocking.Select(b => b.Key)));
        if (blocking.Count > 0)
        {
            return;
        }

        using var password = RandomPassword();
        var run = new SetupRun(answers, password, plan, ConfigBook.ForSetup(null));
        var result = run.Execute(r =>
        {
            if (r.Outcome != StepOutcome.Running)
            {
                o.WriteLine("  setup " + r.Outcome + " " + r.Key + (r.Error is null ? "" : ": " + r.Error));
            }
        });
        Check(result.Completed, "Setup ran to the end", result.Error ?? "");
        SessionCheck(result, "drive.session", "the drive is mapped in the desktop user's session");

        var bytes = File.Exists(StateReader.ConfigPath) ? File.ReadAllBytes(StateReader.ConfigPath) : null;
        Check(bytes is not null, "config.json written");
        if (bytes is null)
        {
            return;
        }

        File.WriteAllBytes(Path.Combine(temp, "cpfs-config-after-setup.json"), bytes);
        var config = ConfigReader.Parse(bytes);
        Check(config.Encoding == TextEncoding.Utf8Bom, "config.json is UTF-8 with a BOM");
        Check(config.Backup == BackupState.Present && config.BackupRegistry.Count == Planner.Hardening.Count, "config.json has the backup");
        Check(!config.Partial, "config.json is not marked Partial");
        Check(config.TrackedUsers.Any(u => u.Name == TestUser && u.Existed == false), "the account is tracked as created by CPFS");

        var user = LocalAccounts.GetUser(TestUser);
        Check(user is not null && user.Enabled && user.PasswordNeverExpires, "the account exists, enabled, password never expires");
        Check(Reg.ReadDword(Reg.UserList, TestUser) == 0, "the account is hidden from the sign-in screen");
        if (user is not null)
        {
            Check(LocalAccounts.IsMember(user.Sid, Accounts.UsersSid) == true, "the account is in Users");
            Check(LocalAccounts.IsMember(user.Sid, Accounts.AdminsSid) == false, "the account is not in Administrators");
            Check(PlanReader.Folder(folder, user.Sid.Value).UserHasModify, "the account can modify the folder");
        }

        var share = PlanReader.Shares().FirstOrDefault(s => s.Name == TestShare);
        Check(share is not null && string.Equals(share.Path?.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase)
            && share.FolderEnumerationMode == 0 && share.CachingMode == 0 && share.EncryptData == false, "the share exists as planned");
        var data = PlanReader.DataFolder();
        Check(data.Exists && data.OwnerIsAdministrators && data.Protected && !data.OthersCanWrite, "ProgramData\\CPFS is locked");
        foreach (var h in Planner.Hardening)
        {
            var v = Reg.ReadDword(h.Key, h.Name);
            Check(v == h.Target || (v is { } c && h.IsStricter(c)), "hardening " + h.Name, v?.ToString(CultureInfo.InvariantCulture) ?? "absent");
        }

        Check(PlanReader.Lockout().Threshold != 0, "account lockout is on");
        Check(PlanReader.AuditLogon().Failure, "failed logons are audited");
        var rules = PlanReader.OwnRules();
        Check(rules.Count(r => r.Name == FirewallRules.LanRule) == 1 && rules.Count(r => r.Name == FirewallRules.MdnsRule) == 1, "the CPFS LAN rules exist");

        if (_letter is { } letter)
        {
            var target = Planner.DosTarget(folder);
            Check(string.Equals(DosDrives.Read(letter), target, StringComparison.OrdinalIgnoreCase), "the DOS Devices mapping " + letter + ": exists", DosDrives.Read(letter) ?? "absent");
            Check(DriveNames.Read(letter) == SetupDefaults.DriveNameFor(TestShare), "the drive name is set", DriveNames.Read(letter) ?? "absent");
            Check(config.DriveLetter == letter.ToString() && config.DriveLabel == SetupDefaults.DriveNameFor(TestShare)
                && string.Equals(config.TrackedDrivePath, folder, StringComparison.OrdinalIgnoreCase), "config.json records the drive letter, its name and folder");
            Check(Planner.IsOwnIcon(DriveIcons.Read(letter), CpfsIconFile.Path), "the drive has the CPFS icon", DriveIcons.Read(letter) ?? "absent");
        }

        Check(File.Exists(CpfsIconFile.Path), "the CPFS icon file is in ProgramData\\CPFS", CpfsIconFile.Path);
        Check(string.Equals(config.TrackedIconFile, CpfsIconFile.Path, StringComparison.OrdinalIgnoreCase), "config.json records the icon file", config.TrackedIconFile ?? "null");

        if (_entrySid is { } sid)
        {
            EntryChecks(config, sid, folder);
        }

        _shortcut = config.Shortcut;
        var lnkTarget = _shortcut is null ? null : Shortcuts.TargetOf(_shortcut);
        Check(_shortcut is not null && string.Equals(Path.GetFileName(_shortcut), SetupDefaults.ShortcutNameFor(TestShare) + ".lnk", StringComparison.OrdinalIgnoreCase),
            "config.json records the shortcut", _shortcut ?? "null");
        Check(string.Equals(lnkTarget?.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase), "the shortcut points to the shared folder", lnkTarget ?? "absent");
        var lnkIcon = _shortcut is null ? null : Shortcuts.IconOf(_shortcut);
        Check(string.Equals(lnkIcon, CpfsIconFile.Path, StringComparison.OrdinalIgnoreCase), "the shortcut has the CPFS icon", lnkIcon ?? "absent");
        Check(config.Access == 3, "config.json records drive and shortcut in Access", config.Access?.ToString(CultureInfo.InvariantCulture) ?? "null");

        // Re-running with the same answers changes nothing.
        var again = Planner.Setup(Read(TestUser, folder, TestShare), answers with { Password = PasswordEntry.Empty, Choices = new Dictionary<string, bool>() },
            config, PlanReader.ProtectedFolders());
        var left = ApplyList.Setup(again).Where(e => !e.Deferred).Select(e => e.Item.Key).ToList();
        Check(left.Count == 0, "a second Setup with the same answers plans no change", string.Join(", ", left));
    }

    private static void UninstallPhase(TextWriter o)
    {
        var bytes = File.ReadAllBytes(StateReader.ConfigPath);
        var config = ConfigReader.Parse(bytes);
        var snapshot = Read(config.SmbUser ?? "", config.SharePath ?? "", config.ShareName ?? "");
        string self;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            self = identity.User!.Value;
        }

        var plan = UninstallPlanner.Uninstall(new UninstallInput
        {
            Snapshot = snapshot,
            Config = config,
            ProtectedSids = new[] { self },
            DesktopUser = self,
            ProtectedFolders = PlanReader.ProtectedFolders(),
        });
        o.WriteLine("Uninstall plan:");
        foreach (var line in plan.LogLines())
        {
            o.WriteLine("  " + line);
        }

        var userSid = snapshot.User.IsOk ? snapshot.User.Value?.Sid : null;
        var run = new UninstallRun(plan, config, bytes, userSid, self, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        var result = run.Execute(r =>
        {
            if (r.Outcome != StepOutcome.Running)
            {
                o.WriteLine("  uninstall " + r.Outcome + " " + r.Key + (r.Error is null ? "" : ": " + r.Error));
            }
        });
        Check(ApplyList.Blocking(plan).Count == 0, "Uninstall plan has nothing that cannot be planned");
        Check(result.Completed, "Uninstall ran to the end", result.Error ?? "");
        if (_letter is not null)
        {
            SessionCheck(result, "u.drive.session", "the drive is unmapped in the desktop user's session");
        }
    }

    // Every documented value of the navigation-pane entry, read back from the desktop user's hive, and the same class
    // key seen through HKEY_USERS\<SID>_Classes, which proves the write reached the user's per-user classes.
    private static void EntryChecks(ConfigDocument config, string sid, string folder)
    {
        var name = SetupDefaults.DriveNameFor(TestShare);
        var hive = ExplorerEntryKeys.Read(sid);
        var e = hive.Entry;
        Check(hive.Loaded && e is not null, "the File Explorer entry exists in the desktop user's hive", sid);
        if (e is null)
        {
            return;
        }

        Check(e.Name == name && e.NameSpaceName == name, "the File Explorer entry has the drive name", (e.Name ?? "absent") + " / " + (e.NameSpaceName ?? "absent"));
        Check(ExplorerEntry.SameFolder(e.TargetFolderPath, folder), "the File Explorer entry opens the shared folder", e.TargetFolderPath ?? "absent");
        Check(Planner.IsOwnIcon(e.Icon, CpfsIconFile.Path), "the File Explorer entry has the CPFS icon", e.Icon ?? "absent");
        Check(e.Pinned == ExplorerEntry.Pinned && e.SortOrder == ExplorerEntry.SortOrderIndex, "the File Explorer entry is pinned with its sort order");
        Check(string.Equals(e.InProcServer, ExplorerEntry.InProcServer, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.InstanceClsid, ExplorerEntry.InstanceClsid, StringComparison.OrdinalIgnoreCase), "the File Explorer entry uses shell32's folder instance");
        Check(e.BagAttributes == ExplorerEntry.BagAttributes && e.FolderValueFlags == ExplorerEntry.FolderValueFlags
            && e.ShellAttributes == ExplorerEntry.ShellAttributes, "the File Explorer entry has the documented flags");
        Check(e.HiddenOnDesktop == ExplorerEntry.HiddenOnDesktop, "the File Explorer entry is hidden on the desktop");
        Check(ExplorerEntry.IsComplete(e), "the File Explorer entry is complete");

        using (var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64))
        using (var classes = users.OpenSubKey(sid + "_Classes\\CLSID\\" + ExplorerEntry.Clsid))
        {
            Check(classes?.GetValue("") as string == name, "the File Explorer entry is in the user's classes hive", classes is null ? "absent" : "");
        }

        var t = config.TrackedExplorerEntry;
        Check(t is not null && t.Sid == sid && t.Clsid == ExplorerEntry.Clsid && ExplorerEntry.SameFolder(t.Folder, folder) && t.Name == name,
            "config.json records the File Explorer entry", t is null ? "null" : t.Sid + " " + t.Folder);
    }

    // The runner has no signed-in desktop user, so the step that changes the drive in that user's session reports
    // that and is skipped. That doesn't fail the run here, in CI, and nowhere else; with a desktop user it must be done.
    private static void SessionCheck(ExecutionResult result, string key, string check)
    {
        var report = result.Reports.FirstOrDefault(r => r.Key == key);
        if (report is null)
        {
            Check(false, check, "the step did not run");
        }
        else if (report.Outcome == StepOutcome.Done)
        {
            Check(true, check);
        }
        else if (report.Outcome == StepOutcome.Unsupported && report.Error?.StartsWith(SessionDrive.NoDesktopUser, StringComparison.Ordinal) == true)
        {
            Skip(check, "no interactive user on this runner");
        }
        else
        {
            Check(false, check, report.Outcome + (report.Error is null ? "" : ": " + report.Error));
        }
    }

    private static void Compare(Machine before, Machine after, string folder)
    {
        Check(LocalAccounts.GetUser(TestUser) is null, "the account is gone");
        Check(!Reg.Exists(Reg.UserList, TestUser), "the sign-in screen entry is gone");
        Check(PlanReader.Shares().All(s => s.Name != TestShare), "the share is gone");
        Check(PlanReader.OwnRules().Count == 0, "the CPFS firewall rules are gone");
        Check(!Directory.Exists(StateReader.DataFolder), "ProgramData\\CPFS is gone");
        Check(Directory.Exists(folder), "the shared folder is still there");
        if (_letter is { } letter)
        {
            Check(DosDrives.Read(letter) is null, "the DOS Devices mapping " + letter + ": is gone", DosDrives.Read(letter) ?? "");
            Check(DriveNames.Read(letter) is null, "the drive name is gone", DriveNames.Read(letter) ?? "");
            Check(DriveIcons.Read(letter) is null, "the drive icon is gone", DriveIcons.Read(letter) ?? "");
        }

        if (_shortcut is not null)
        {
            Check(!File.Exists(_shortcut), "the shortcut is gone", _shortcut);
        }

        if (_entrySid is { } sid)
        {
            var hive = ExplorerEntryKeys.Read(sid);
            Check(hive.Loaded && hive.Entry is null, "the File Explorer entry's class key is gone");
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            using (var ns = users.OpenSubKey(sid + "\\" + ExplorerEntry.NameSpaceKey))
            {
                Check(ns is null, "the File Explorer entry's NameSpace key is gone");
            }

            using (var hide = users.OpenSubKey(sid + "\\" + ExplorerEntry.HideDesktopIconsKey))
            {
                Check(hide?.GetValue(ExplorerEntry.Clsid) is null, "the File Explorer entry's HideDesktopIcons value is gone");
            }
        }

        foreach (var pair in before.Hardening)
        {
            Check(after.Hardening[pair.Key] == pair.Value, "restored " + pair.Key, Show(pair.Value) + " -> " + Show(after.Hardening[pair.Key]));
        }

        Check(after.Smb2 == before.Smb2 && after.Reject == before.Reject, "restored SMB2 and RejectUnencryptedAccess");
        Check(after.ServerStart == before.ServerStart, "restored the Server service start type", Show(before.ServerStart) + " -> " + Show(after.ServerStart));
        Check(after.Lockout == before.Lockout, "restored the lockout policy", before.Lockout + " -> " + after.Lockout);
        foreach (var pair in before.BuiltIn)
        {
            Check(after.BuiltIn.TryGetValue(pair.Key, out var now) && now == pair.Value, "restored built-in rule " + pair.Key);
        }

        foreach (var pair in before.Networks)
        {
            Check(!after.Networks.TryGetValue(pair.Key, out var now) || now == pair.Value, "restored network " + pair.Key);
        }
    }

    private static string Show(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "absent";

    // 24 characters with every character class, so a Windows Server password policy accepts it.
    private static SecureString RandomPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!#%+-=?";
        var all = upper + lower + digits + symbols;
        var secret = new SecureString();
        var bytes = new byte[24];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            var set = i switch { 0 => upper, 1 => lower, 2 => digits, 3 => symbols, _ => all };
            secret.AppendChar(set[bytes[i] % set.Length]);
        }

        Array.Clear(bytes, 0, bytes.Length);
        secret.MakeReadOnly();
        return secret;
    }

    // The settings Uninstall must put back exactly.
    private sealed class Machine
    {
        public Dictionary<string, int?> Hardening { get; } = new(StringComparer.Ordinal);

        public bool? Smb2 { get; private set; }

        public bool? Reject { get; private set; }

        public int? ServerStart { get; private set; }

        public LockoutInfo? Lockout { get; private set; }

        public Dictionary<string, bool> BuiltIn { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> Networks { get; } = new(StringComparer.Ordinal);

        public static Machine Read()
        {
            var m = new Machine();
            foreach (var h in Planner.Hardening)
            {
                m.Hardening[h.Name] = Reg.Exists(h.Key, h.Name) ? Reg.ReadDword(h.Key, h.Name) ?? -1 : null;
            }

            var smb = PlanReader.SmbServer();
            m.Smb2 = smb.Smb2;
            m.Reject = smb.RejectUnencrypted;
            m.ServerStart = PlanReader.ServerStart();
            m.Lockout = PlanReader.Lockout();
            foreach (var r in PlanReader.BuiltInRules())
            {
                m.BuiltIn[r.Id] = r.Enabled;
            }

            foreach (var n in PlanReader.Networks())
            {
                m.Networks[n.Name] = n.Category;
            }

            return m;
        }
    }
}
