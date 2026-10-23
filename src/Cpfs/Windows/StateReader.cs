using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using Cpfs.Core.Config;
using Cpfs.Core.Drives;
using Cpfs.Core.Text;
using Microsoft.Win32;

namespace Cpfs.Windows;

// Reads, never writes: the state of an existing install as far as this process may see it. Every read catches its
// own failure, so one denied source shows a "needs administrator" badge instead of hiding the rest.
internal sealed class StateReader
{
    public static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CPFS");

    public static readonly string ConfigPath = Path.Combine(DataFolder, "config.json");

    // The script's hardening values (legacy inventory, changes 1-8) and the value CPFS sets.
    private static readonly (string Key, string Name, int CpfsValue)[] Hardening =
    {
        (@"SYSTEM\CurrentControlSet\Control\Lsa", "forceguest", 0),
        (@"SYSTEM\CurrentControlSet\Control\Lsa", "LmCompatibilityLevel", 5),
        (@"SYSTEM\CurrentControlSet\Control\Lsa", "NoLMHash", 1),
        (@"SYSTEM\CurrentControlSet\Control\Lsa", "RestrictAnonymous", 1),
        (@"SYSTEM\CurrentControlSet\Control\Lsa", "RestrictAnonymousSAM", 1),
        (@"SYSTEM\CurrentControlSet\Control\Lsa", "EveryoneIncludesAnonymous", 0),
        (@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "RestrictNullSessAccess", 1),
        (@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "AutoShareWks", 0),
    };

    private static readonly string[] TaskNames = { "CPFS-AutoPrivate", "CPFS-RunAsUser", "CyShare-AutoPrivate" };

    private static bool _firewallPropertiesLogged;

    public ConfigDocument? Config { get; private set; }

    // Page order. Config is read first, since every other section compares with it; the rest run side by side and
    // each is reported as soon as it is done, so one slow source doesn't hold up the others.
    public static IReadOnlyList<string> SectionIds { get; } = new[]
    {
        "Config", "Computer", "User", "Share", "Firewall", "Tasks", "Hardening", "Networks", "Drive", "Shortcut", "DataFolder",
    };

    public async Task ReadAllAsync(Task<LetterScan> letters, Action<int, StateSection> done)
    {
        var total = Stopwatch.StartNew();
        done(0, Run("Config", ReadConfig));

        var readers = new Func<StateSection, Task>?[]
        {
            null,
            Sync(ReadComputerName),
            Sync(ReadSmbUser),
            Sync(ReadShare),
            Sync(ReadFirewall),
            Sync(ReadTasks),
            Sync(ReadHardening),
            Sync(ReadNetworks),
            async s => ReadDrive(s, await letters.ConfigureAwait(false)),
            Sync(ReadShortcut),
            Sync(ReadDataFolder),
        };

        var tasks = new List<Task>();
        for (var i = 1; i < readers.Length; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () => done(index, await RunAsync(SectionIds[index], readers[index]!).ConfigureAwait(false))));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        AppLog.Write("state", "All sections in " + Ms(total));
    }

    private static Func<StateSection, Task> Sync(Action<StateSection> read) => s =>
    {
        read(s);
        return Task.CompletedTask;
    };

    private static StateSection Run(string id, Action<StateSection> read) =>
        RunAsync(id, Sync(read)).GetAwaiter().GetResult();

    private static async Task<StateSection> RunAsync(string id, Func<StateSection, Task> read)
    {
        var section = new StateSection(id);
        var watch = Stopwatch.StartNew();
        try
        {
            await read(section).ConfigureAwait(false);
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            section.Fail(ex);
        }

        AppLog.Write("state", id + ": " + section.Status + (section.Error is null ? "" : " (" + section.Error + ")") + ", "
            + section.Rows.Count.ToString(CultureInfo.InvariantCulture) + " rows in " + Ms(watch));
        foreach (var row in section.Rows)
        {
            if (row.Status != ReadStatus.Ok)
            {
                AppLog.Write("state", "  " + (row.LabelKey ?? row.Label) + ": " + row.Status + " " + row.Error);
            }
        }

        return section;
    }

    internal static string Ms(Stopwatch watch) => watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms";

    private void ReadConfig(StateSection s)
    {
        s.Rows.Add(StateRow.Data("Setup_Config_Path", ConfigPath));
        var info = new FileInfo(ConfigPath);
        if (!info.Exists)
        {
            s.Status = ReadStatus.Missing;
            s.Rows.Add(StateRow.Word("Setup_Config_State", "Setup_Config_NotFound"));
            s.Summarize("Setup_Config_NotFound");
            return;
        }

        // Read-only open with sharing, so a running script or editor is not disturbed and nothing is written.
        byte[] bytes;
        using (var fs = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[fs.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = fs.Read(bytes, read, bytes.Length - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }
        }

        s.Rows.Add(StateRow.Data("Setup_Config_Modified", Formats.Stamp(info.LastWriteTime)));
        s.Rows.Add(StateRow.Data("Setup_Config_Size", bytes.Length.ToString(CultureInfo.InvariantCulture)));

        ConfigDocument doc;
        try
        {
            doc = ConfigReader.Parse(bytes, Encoding.Default);
        }
        catch (Exception ex) when (ex is ConfigFormatException or DecoderFallbackException)
        {
            s.Status = ReadStatus.Failed;
            s.Error = ex.Message;
            return;
        }

        Config = doc;
        s.Rows.Add(StateRow.Word("Setup_Config_Encoding", "Encoding_" + doc.Encoding));
        s.Rows.Add(StateRow.Word("Setup_Config_Shape", doc.Shape == ConfigShape.V01 ? "Setup_Config_ShapeV01" : "Setup_Config_ShapeV02"));
        s.Rows.Add(StateRow.Data("Setup_Config_Version", doc.Version));
        s.Rows.Add(StateRow.Word("Setup_Config_Backup", "Setup_Config_Backup" + doc.Backup));
        s.Rows.Add(StateRow.YesNo("Setup_Config_Partial", doc.Partial));
        s.Summarize("Setup_Config_Summary", (object?)doc.Version ?? new LocKey("State_NotSet"), new LocKey("Setup_Config_Backup" + doc.Backup));
        foreach (var pair in ConfigJson.Flatten(doc.Root))
        {
            s.Rows.Add(new StateRow { Label = pair.Key, Value = pair.Value });
        }
    }

    private void ReadComputerName(StateSection s)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters");
        var current = key?.GetValue("Hostname") as string;
        var pending = key?.GetValue("NV Hostname") as string;
        s.Rows.Add(StateRow.Data("Setup_Computer_Current", current));
        s.Rows.Add(StateRow.Data("Setup_Computer_Pending", pending));
        s.Rows.Add(StateRow.YesNo("Setup_Computer_RenamePending",
            current is null || pending is null ? null : !string.Equals(current, pending, StringComparison.OrdinalIgnoreCase)));
        if (Config?.HostName is { } configured)
        {
            s.Rows.Add(StateRow.Data("Setup_Computer_Configured", configured));
        }

        if (current is not null && pending is not null && !string.Equals(current, pending, StringComparison.OrdinalIgnoreCase))
        {
            s.Summarize("Setup_Computer_SummaryRename", current, pending);
        }
        else
        {
            s.Summarize("Setup_Computer_Summary", (object?)current ?? new LocKey("State_Unknown"));
        }
    }

    private void ReadSmbUser(StateSection s)
    {
        var name = Config?.SmbUser;
        if (name is null)
        {
            s.Status = ReadStatus.Missing;
            s.Rows.Add(StateRow.Word("Setup_User_Name", "Setup_NoConfigValue"));
            s.Summarize("Setup_NoConfigValue");
            return;
        }

        s.Rows.Add(StateRow.Data("Setup_User_Name", name));
        var user = LocalAccounts.GetUser(name);
        if (user is null)
        {
            s.Status = ReadStatus.Missing;
            s.Rows.Add(StateRow.Word("Setup_User_Exists", "Common_No"));
            s.Summarize("Setup_User_SummaryMissing", name);
        }
        else
        {
            s.Rows.Add(StateRow.Word("Setup_User_Exists", "Common_Yes"));
            s.Rows.Add(StateRow.Data("Setup_User_Sid", user.Sid.Value));
            s.Rows.Add(StateRow.YesNo("Setup_User_Enabled", user.Enabled));
            s.Rows.Add(StateRow.YesNo("Setup_User_PasswordNeverExpires", user.PasswordNeverExpires));
            s.Rows.Add(Membership(user, "Setup_User_Admin", "S-1-5-32-544", out var admin));
            s.Rows.Add(Membership(user, "Setup_User_Users", "S-1-5-32-545", out var users));
            s.Summarize(
                user.Enabled == false ? "Setup_User_SummaryDisabled"
                    : admin == true ? "Setup_User_SummaryAdmin"
                    : users == true ? "Setup_User_SummaryUsers"
                    : users == false ? "Setup_User_SummaryNotUsers"
                    : "Setup_User_SummaryExists",
                name);
        }

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList");
            var hidden = key?.GetValue(name);
            s.Rows.Add(hidden is null ? StateRow.Word("Setup_User_Hidden", "State_NotSet") : StateRow.Data("Setup_User_Hidden", Convert.ToString(hidden, CultureInfo.InvariantCulture)));
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            s.Rows.Add(StateRow.Failure("Setup_User_Hidden", ex));
        }

        foreach (var tracked in Config!.TrackedUsers)
        {
            s.Rows.Add(StateRow.Word("Setup_User_Tracked", "Setup_User_TrackedValue", tracked.Name ?? "?",
                Word(tracked.Existed), Word(tracked.WasAdmin)));
        }
    }

    private static StateRow Membership(LocalUser user, string labelKey, string groupSid, out bool? member)
    {
        member = null;
        try
        {
            member = LocalAccounts.IsMember(user.Sid, groupSid);
            return member is null ? StateRow.Word(labelKey, "State_Unknown") : StateRow.YesNo(labelKey, member);
        }
        catch (Exception ex) when (State.IsExpected(ex) || ex is IdentityNotMappedException)
        {
            return StateRow.Failure(labelKey, ex);
        }
    }

    private void ReadShare(StateSection s)
    {
        var name = Config?.ShareName;
        var path = Config?.SharePath;
        s.Rows.Add(StateRow.Data("Setup_Share_Name", name));
        s.Rows.Add(StateRow.Data("Setup_Share_ConfigPath", path));
        if (path is not null)
        {
            s.Rows.Add(StateRow.YesNo("Setup_Share_FolderExists", Directory.Exists(path)));
        }

        var found = false;
        string? foundPath = null;
        foreach (var share in Query(@"root\Microsoft\Windows\SMB", "SELECT Name, Path, Description, EncryptData, FolderEnumerationMode, CachingMode, Special FROM MSFT_SmbShare"))
        {
            using (share)
            {
                var shareName = Prop(share, "Name") as string;
                var sharePath = Prop(share, "Path") as string;
                var sameName = name is not null && string.Equals(shareName, name, StringComparison.OrdinalIgnoreCase);
                var samePath = path is not null && sharePath is not null && string.Equals(sharePath.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                if (!sameName && !samePath)
                {
                    continue;
                }

                if (sameName)
                {
                    found = true;
                    foundPath = sharePath;
                }
                s.Rows.Add(new StateRow
                {
                    Label = shareName,
                    ValueKey = "Setup_Share_Found",
                    ValueArgs = new object[]
                    {
                        sharePath ?? "?",
                        Text(Prop(share, "EncryptData")),
                        EnumName(Prop(share, "FolderEnumerationMode"), "AccessBased", "Unrestricted"),
                        EnumName(Prop(share, "CachingMode"), "None", "Manual", "Documents", "Programs", "BranchCache", "Unknown"),
                    },
                });
            }
        }

        if (!found)
        {
            s.Status = ReadStatus.Missing;
            s.Rows.Add(StateRow.Word("Setup_Share_State", "Setup_Share_NotFound"));
        }

        if (name is null)
        {
            s.Summarize("Setup_NoConfigValue");
        }
        else if (found)
        {
            s.Summarize("Setup_Share_SummaryFound", name, foundPath ?? "?");
        }
        else
        {
            s.Summarize("Setup_Share_SummaryMissing", name);
        }
    }

    private void ReadFirewall(StateSection s)
    {
        var tracked = new HashSet<string>(Config?.TrackedFirewallRules ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var seenTracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int own = 0, ownEnabled = 0;
        foreach (var rule in Query(@"root\StandardCimv2", "SELECT * FROM MSFT_NetFirewallRule"))
        {
            using (rule)
            {
                if (!_firewallPropertiesLogged)
                {
                    // The class has no page on Microsoft Learn; the log shows its real property names.
                    _firewallPropertiesLogged = true;
                    var names = new List<string>();
                    foreach (var p in rule.Properties)
                    {
                        names.Add(p.Name);
                    }

                    AppLog.Write("state", "MSFT_NetFirewallRule properties: " + string.Join(", ", names));
                }

                var id = Prop(rule, "InstanceID", "Name") as string;
                var display = Prop(rule, "DisplayName", "ElementName") as string;
                var isOwn = display is not null && (display.StartsWith("CPFS", StringComparison.OrdinalIgnoreCase) || display.StartsWith("CyShare", StringComparison.OrdinalIgnoreCase));
                var isTracked = id is not null && tracked.Contains(id);
                if (!isOwn && !isTracked)
                {
                    continue;
                }

                if (isTracked)
                {
                    seenTracked.Add(id!);
                }
                else
                {
                    own++;
                    if (Prop(rule, "Enabled") is { } enabled && Convert.ToInt32(enabled, CultureInfo.InvariantCulture) == 1)
                    {
                        ownEnabled++;
                    }
                }

                s.Rows.Add(new StateRow
                {
                    Label = isTracked ? id : display,
                    ValueKey = isTracked ? "Setup_Firewall_TrackedRule" : "Setup_Firewall_OwnRule",
                    ValueArgs = new object[] { Enabled(Prop(rule, "Enabled")), Profiles(Prop(rule, "Profiles", "Profile")), Text(Prop(rule, "Direction")) },
                });
            }
        }

        foreach (var name in tracked)
        {
            if (!seenTracked.Contains(name))
            {
                s.Rows.Add(new StateRow { Label = name, ValueKey = "Setup_Firewall_TrackedMissing" });
            }
        }

        if (s.Rows.Count == 0)
        {
            s.Status = ReadStatus.Missing;
            s.Rows.Add(StateRow.Word("Setup_Firewall_State", "Setup_Firewall_None"));
            s.Summarize("Setup_Firewall_None");
            return;
        }

        s.Summarize("Setup_Firewall_Summary", own, ownEnabled, seenTracked.Count);
    }

    private static void ReadTasks(StateSection s)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        var service = Activator.CreateInstance(type)!;
        try
        {
            Invoke(service, "Connect");
            var folder = Invoke(service, "GetFolder", @"\")!;
            try
            {
                var found = 0;
                foreach (var name in TaskNames)
                {
                    object? task = null;
                    try
                    {
                        task = Invoke(folder, "GetTask", name);
                        var enabled = Get(task!, "Enabled") as bool?;
                        var state = Convert.ToInt32(Get(task!, "State"), CultureInfo.InvariantCulture);
                        found++;
                        s.Rows.Add(new StateRow
                        {
                            Label = name,
                            ValueKey = "Setup_Tasks_Found",
                            ValueArgs = new object[] { Word(enabled), TaskState(state) },
                        });
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is COMException { HResult: unchecked((int)0x80070002) } or FileNotFoundException)
                    {
                        s.Rows.Add(new StateRow { Label = name, ValueKey = "Setup_Tasks_NotFound" });
                    }
                    catch (Exception ex) when (State.IsExpected(ex))
                    {
                        s.Rows.Add(new StateRow
                        {
                            Label = name,
                            Status = State.IsAccessDenied(ex) ? ReadStatus.NeedsAdmin : ReadStatus.Failed,
                            Error = (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message,
                        });
                    }
                    finally
                    {
                        Release(task);
                    }
                }

                s.Summarize("Setup_Tasks_Summary", found, TaskNames.Length);
            }
            finally
            {
                Release(folder);
            }
        }
        finally
        {
            Release(service);
        }
    }

    private void ReadHardening(StateSection s)
    {
        var backup = new Dictionary<string, RegistryBackup>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Config?.BackupRegistry ?? Array.Empty<RegistryBackup>())
        {
            if (entry.Name is not null)
            {
                backup[entry.Name] = entry;
            }
        }

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        int atCpfs = 0, total = 0;
        foreach (var (keyPath, name, cpfsValue) in Hardening)
        {
            total++;
            try
            {
                using var key = hklm.OpenSubKey(keyPath);
                var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var current = value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
                if (string.Equals(current, cpfsValue.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    atCpfs++;
                }

                var original = backup.TryGetValue(name, out var b) ? (b.Exists == false ? "-" : b.Value?.ToString(CultureInfo.InvariantCulture) ?? "?") : "";
                s.Rows.Add(new StateRow
                {
                    Label = name,
                    ValueKey = "Setup_Hardening_Value",
                    ValueArgs = new object[] { current ?? "-", cpfsValue.ToString(CultureInfo.InvariantCulture), original },
                });
            }
            catch (Exception ex) when (State.IsExpected(ex))
            {
                s.Rows.Add(new StateRow { Label = name, Status = State.IsAccessDenied(ex) ? ReadStatus.NeedsAdmin : ReadStatus.Failed, Error = ex.GetType().Name + ": " + ex.Message });
            }
        }

        s.Summarize("Setup_Hardening_Summary", atCpfs, total);
        try
        {
            using var server = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer");
            s.Rows.Add(StateRow.Word("Setup_Hardening_ServerStart", "StartType_" + (server?.GetValue("Start") is int start ? start.ToString(CultureInfo.InvariantCulture) : "Unknown")));
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            s.Rows.Add(StateRow.Failure("Setup_Hardening_ServerStart", ex));
        }
    }

    private void ReadNetworks(StateSection s)
    {
        var connected = 0;
        try
        {
            foreach (var profile in Query(@"root\StandardCimv2", "SELECT Name, InterfaceAlias, NetworkCategory FROM MSFT_NetConnectionProfile"))
            {
                using (profile)
                {
                    connected++;
                    s.Rows.Add(new StateRow
                    {
                        Label = Prop(profile, "Name") as string,
                        ValueKey = "Setup_Networks_Connected",
                        ValueArgs = new object[] { Prop(profile, "InterfaceAlias") as string ?? "?", Category(Prop(profile, "NetworkCategory")) },
                    });
                }
            }
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            s.Rows.Add(StateRow.Failure("Setup_Networks_ConnectedLabel", ex));
        }

        var tracked = Config?.TrackedNetworks ?? Array.Empty<string>();
        s.Summarize("Setup_Networks_Summary", connected, tracked.Count);
        if (tracked.Count == 0)
        {
            s.Rows.Add(StateRow.Word("Setup_Networks_Tracked", "Setup_Networks_TrackedNone"));
            return;
        }

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var profiles = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles");
            foreach (var name in tracked)
            {
                var categories = new List<string>();
                foreach (var id in profiles?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var p = profiles!.OpenSubKey(id);
                    if (string.Equals(p?.GetValue("ProfileName") as string, name, StringComparison.Ordinal))
                    {
                        categories.Add(Category(p!.GetValue("Category")));
                    }
                }

                s.Rows.Add(new StateRow
                {
                    Label = name,
                    ValueKey = categories.Count == 0 ? "Setup_Networks_TrackedUnknown" : "Setup_Networks_TrackedValue",
                    ValueArgs = new object[] { string.Join(", ", categories) },
                });
            }
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            s.Rows.Add(StateRow.Failure("Setup_Networks_Tracked", ex));
        }
    }

    private void ReadDrive(StateSection s, LetterScan letters)
    {
        var configured = Config?.DriveLetter;
        s.Rows.Add(StateRow.Data("Setup_Drive_Configured", configured));
        if (Config?.DriveLabel is { } label)
        {
            s.Rows.Add(StateRow.Data("Setup_Drive_Label", label));
        }

        if (configured is { Length: 1 } letter)
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using (var dos = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\DOS Devices"))
                {
                    s.Rows.Add(StateRow.Data("Setup_Drive_DosDevice", dos?.GetValue(letter + ":", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string));
                }

                using (var icon = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\" + letter + @"\DefaultLabel"))
                {
                    s.Rows.Add(StateRow.Data("Setup_Drive_ExplorerLabel", icon?.GetValue("") as string));
                }
            }
            catch (Exception ex) when (State.IsExpected(ex))
            {
                s.Rows.Add(StateRow.Failure("Setup_Drive_DosDevice", ex));
            }
        }

        foreach (var pair in letters.Taken)
        {
            foreach (var use in pair.Value)
            {
                s.Rows.Add(new StateRow
                {
                    Label = pair.Key + ":",
                    ValueKey = use.Detail is null ? "Source_" + use.Source : "Setup_Drive_SourceDetail",
                    ValueArgs = use.Detail is null ? Array.Empty<object>() : new object[] { new LocKey("Source_" + use.Source), use.Detail },
                });
            }
        }

        foreach (var failed in letters.Failed)
        {
            s.Rows.Add(new StateRow { LabelKey = "Source_" + failed.Key, Status = ReadStatus.Failed, Error = failed.Value });
        }

        var own = configured is { Length: 1 } c ? c[0] : (char?)null;
        s.Rows.Add(StateRow.Data("Setup_Drive_Free", string.Join(" ", letters.Free(own))));
        s.Rows.Add(StateRow.Data("Setup_Drive_Default", letters.DefaultLetter(own)?.ToString() ?? "-"));
        var free = letters.Free(own).Count;
        if (own is { } d)
        {
            s.Summarize(letters.Taken.ContainsKey(char.ToUpperInvariant(d)) ? "Setup_Drive_SummaryInUse" : "Setup_Drive_SummaryFree", char.ToUpperInvariant(d) + ":", free);
        }
        else
        {
            s.Summarize("Setup_Drive_SummaryNone", free);
        }
    }

    private void ReadShortcut(StateSection s)
    {
        var path = Config?.Shortcut;
        s.Rows.Add(StateRow.Data("Setup_Shortcut_Path", path));
        if (path is null)
        {
            s.Summarize("Setup_NoConfigValue");
            return;
        }

        var exists = File.Exists(path);
        s.Rows.Add(StateRow.YesNo("Setup_Shortcut_Exists", exists));
        s.Summarize(exists ? "Setup_Shortcut_SummaryExists" : "Setup_Shortcut_SummaryMissing", Path.GetFileName(path));
    }

    private static void ReadDataFolder(StateSection s)
    {
        s.Rows.Add(StateRow.Data("Setup_DataFolder_Path", DataFolder));
        var dir = new DirectoryInfo(DataFolder);
        if (!dir.Exists)
        {
            s.Status = ReadStatus.Missing;
            s.Rows.Add(StateRow.Word("Setup_DataFolder_State", "Setup_Config_NotFound"));
            s.Summarize("Setup_Config_NotFound");
            return;
        }

        var security = dir.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        s.Rows.Add(StateRow.Data("Setup_DataFolder_Owner", Describe(owner)));
        var ownedByAdmins = owner?.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
        s.Rows.Add(StateRow.YesNo("Setup_DataFolder_OwnerIsAdmins", ownedByAdmins));
        if (ownedByAdmins == true)
        {
            s.Summarize("Setup_DataFolder_SummaryAdmins");
        }
        else
        {
            s.Summarize("Setup_DataFolder_SummaryOther", Describe(owner));
        }
        s.Rows.Add(StateRow.YesNo("Setup_DataFolder_Protected", security.AreAccessRulesProtected));
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            s.Rows.Add(new StateRow
            {
                Label = Describe(rule.IdentityReference as SecurityIdentifier),
                Value = rule.AccessControlType + " " + rule.FileSystemRights + (rule.IsInherited ? " (inherited)" : ""),
            });
        }
    }

    // The account name is for reading only (it is localized); the SID is what identifies it.
    private static string Describe(SecurityIdentifier? sid)
    {
        if (sid is null)
        {
            return "?";
        }

        try
        {
            return sid.Translate(typeof(NTAccount)).Value + " (" + sid.Value + ")";
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return sid.Value;
        }
    }

    private static List<ManagementBaseObject> Query(string scope, string wql)
    {
        var list = new List<ManagementBaseObject>();
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));
        using var results = searcher.Get();
        foreach (var item in results)
        {
            list.Add(item);
        }

        return list;
    }

    private static object? Prop(ManagementBaseObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                return obj[name];
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotFound)
            {
            }
        }

        return null;
    }

    private static object? Invoke(object target, string method, params object[] args) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    private static object? Get(object target, string property) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.ReleaseComObject(com);
        }
    }

    private static string Text(object? value) => value is null ? "?" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?";

    private static string EnumName(object? value, params string[] names)
    {
        if (value is null)
        {
            return "?";
        }

        var i = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        return i >= 0 && i < names.Length ? names[i] : i.ToString(CultureInfo.InvariantCulture);
    }

    // MSFT_NetFirewallRule.Enabled: 1 true, 2 false [unverified; shown raw as well].
    private static LocKey Enabled(object? value) => value is null
        ? new LocKey("State_Unknown")
        : Convert.ToInt32(value, CultureInfo.InvariantCulture) switch
        {
            1 => new LocKey("Common_Yes"),
            2 => new LocKey("Common_No"),
            var other => new LocKey("State_Raw", other),
        };

    // Domain 1, Private 2, Public 4; 0 is "any" [unverified for the WMI class; matches the firewall API's NET_FW_PROFILE_TYPE2].
    private static string Profiles(object? value)
    {
        if (value is null)
        {
            return "?";
        }

        var bits = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (bits == 0 || bits == 0x7FFFFFFF)
        {
            return "Any";
        }

        var names = new List<string>();
        if ((bits & 1) != 0)
        {
            names.Add("Domain");
        }

        if ((bits & 2) != 0)
        {
            names.Add("Private");
        }

        if ((bits & 4) != 0)
        {
            names.Add("Public");
        }

        return string.Join("+", names) + " (" + bits.ToString(CultureInfo.InvariantCulture) + ")";
    }

    // MSFT_NetConnectionProfile.NetworkCategory and NetworkList\Profiles\Category: 0 Public, 1 Private, 2 Domain.
    private static string Category(object? value)
    {
        if (value is null)
        {
            return "?";
        }

        var i = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        return i switch
        {
            0 => "Public",
            1 => "Private",
            2 => "Domain",
            _ => i.ToString(CultureInfo.InvariantCulture),
        };
    }

    // TASK_STATE (Microsoft Learn): 0 Unknown, 1 Disabled, 2 Queued, 3 Ready, 4 Running.
    private static LocKey TaskState(int state) => new("TaskState_" + (state is >= 0 and <= 4 ? state.ToString(CultureInfo.InvariantCulture) : "0"));

    private static LocKey Word(bool? value) => new(value is null ? "State_Unknown" : value.Value ? "Common_Yes" : "Common_No");
}

// A localized word inside a row's arguments, resolved when the row is rendered.
internal sealed class LocKey
{
    public LocKey(string key, params object[] args)
    {
        Key = key;
        Args = args;
    }

    public string Key { get; }

    public object[] Args { get; }
}
