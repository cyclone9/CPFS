using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Cpfs.Core.Connect;
using Cpfs.Core.Drives;
using Cpfs.Core.Planning;
using Cpfs.Core.Validation;
using Microsoft.Win32;

namespace Cpfs.Windows;

// Reads, never writes: the state Setup's plan compares with. Each part catches its own failure, so a source this
// process may not read shows as "needs administrator to plan" instead of hiding the rest. The one call here that
// changes anything is AdjustTokenPrivileges on this process's own token (SeSecurityPrivilege, needed to read the
// audit policy), enabled for that read only and then put back.
internal static class PlanReader
{
    private const string SmbScope = @"root\Microsoft\Windows\SMB";
    private const string FirewallScope = @"root\StandardCimv2";
    private const string LogonSubcategory = "0CCE9215-69AE-11D9-BED3-505054503030";

    private static readonly string[] TaskNames = { Planner.AutoPrivateTask, Planner.RunAsUserTask, Planner.LegacyAutoPrivateTask };

    public static SystemSnapshot ReadStatic(LetterScan letters, string? desktopSid)
    {
        var watch = Stopwatch.StartNew();
        var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var snapshot = new SystemSnapshot
        {
            Elevated = Startup.IsElevated,
            WindowsUserNames = WindowsUserNames(desktopSid),
            ComputerName = Part("computer", ComputerName).Value,
            ComputerNames = Part("computer names", ComputerNames),
            Shares = Part("shares", Shares),
            SmbServer = Part("smb server", SmbServer),
            ServerStart = Part("server start", ServerStart),
            Hardening = Hardening(),
            Lockout = Part("lockout", Lockout),
            AuditLogon = Part("audit", AuditLogon),
            Smb1Feature = Part("smb1 feature", Smb1Feature),
            GuestEnabled = Part("guest", GuestEnabled),
            OwnRules = Part("own rules", OwnRules),
            BuiltInRules = Part("built-in rules", BuiltInRules),
            Networks = Part("networks", Networks),
            VpnAdapters = VpnAdapters(),
            Tasks = Part("tasks", Tasks),
            DataFolder = Part("data folder", DataFolder),
            DosDevices = Part("dos devices", DosDevices),
            DriveLabels = Part<IReadOnlyDictionary<char, string>>("drive labels", () => DriveIconValues("DefaultLabel")),
            DriveIcons = Part<IReadOnlyDictionary<char, string>>("drive icons", () => DriveIconValues("DefaultIcon")),
            CpfsIcon = Apply.CpfsIconFile.Path,
            Letters = letters,
            CommonDesktop = string.IsNullOrEmpty(commonDesktop) ? @"C:\Users\Public\Desktop" : commonDesktop,
            PublicDesktopFiles = Part<IReadOnlyList<string>>("public desktop", () => DesktopFiles(commonDesktop)),
            ShortcutIcons = Part<IReadOnlyDictionary<string, string>>("shortcut icons", () => Apply.Shortcuts.IconsOf(commonDesktop)),
        };
        AppLog.Write("plan", "Read the state for the plan in " + StateReader.Ms(watch));
        return snapshot;
    }

    // The parts that depend on the typed account name, folder and share name; re-read when those fields change.
    public static SystemSnapshot ReadAnswerParts(SystemSnapshot snapshot, string userName, string path, string shareName)
    {
        var user = Part("user", () => User(userName.Trim()));
        var sid = user.Value?.Sid;
        return snapshot with
        {
            User = user,
            Folder = Part("folder", () => Folder(path.Trim(), sid)),
            ShareAccess = Part<IReadOnlyList<ShareAccessEntry>?>("share access", () => ShareAccess(shareName)),
        };
    }

    public static ProtectedFolders ProtectedFolders()
    {
        var folders = new ProtectedFolders();
        foreach (var f in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        })
        {
            if (!string.IsNullOrEmpty(f))
            {
                folders.Trees.Add(f);
            }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            folders.Exact.Add(profile);
            if (Path.GetDirectoryName(profile) is { Length: > 0 } users)
            {
                folders.Exact.Add(users);
                folders.Exact.Add(Path.Combine(users, "Public"));
            }
        }

        return folders;
    }

    private static Reading<T> Part<T>(string name, Func<T> read)
    {
        try
        {
            return Reading.Ok(read());
        }
        catch (Exception ex) when (State.IsExpected(ex) || ex is IdentityNotMappedException)
        {
            var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
            var text = inner.GetType().Name + ": " + inner.Message;

            // Elevated, a refusal is a failure: offering "Continue as administrator" again would not help.
            var needsAdmin = State.IsAccessDenied(ex) && !Startup.IsElevated;
            AppLog.Write("plan", name + ": " + (needsAdmin ? "needs administrator" : "failed") + " (" + text + ")");
            return needsAdmin ? Reading.NeedsAdmin<T>(text) : Reading.Failed<T>(text);
        }
    }

    private static List<string> WindowsUserNames(string? desktopSid)
    {
        var names = new List<string> { Environment.UserName };
        if (desktopSid is not null)
        {
            try
            {
                var account = new SecurityIdentifier(desktopSid).Translate(typeof(NTAccount)).Value;
                names.Add(account.Substring(account.LastIndexOf('\\') + 1));
            }
            catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
            {
                AppLog.Write("plan", "Desktop user name: " + ex.Message);
            }
        }

        return names;
    }

    internal static string? ComputerName()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters");
        return key?.GetValue("NV Hostname") as string ?? key?.GetValue("Hostname") as string;
    }

    private static ComputerNameInfo ComputerNames()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters");
        var active = key?.GetValue("Hostname") as string;
        var next = key?.GetValue("NV Hostname") as string;
        var pending = string.IsNullOrEmpty(next) || string.Equals(next, active, StringComparison.OrdinalIgnoreCase) ? null : next;
        return new ComputerNameInfo(string.IsNullOrEmpty(active) ? null : active, pending);
    }

    internal static IReadOnlyList<ShareInfo> Shares()
    {
        var list = new List<ShareInfo>();
        foreach (var share in StateReader.Query(SmbScope, "SELECT Name, Path, Description, EncryptData, FolderEnumerationMode, CachingMode, Special FROM MSFT_SmbShare"))
        {
            using (share)
            {
                list.Add(new ShareInfo(
                    StateReader.Prop(share, "Name") as string ?? "",
                    StateReader.Prop(share, "Path") as string,
                    StateReader.Prop(share, "Description") as string,
                    StateReader.Prop(share, "EncryptData") as bool?,
                    ToInt(StateReader.Prop(share, "FolderEnumerationMode")),
                    ToInt(StateReader.Prop(share, "CachingMode")),
                    StateReader.Prop(share, "Special") as bool? ?? false));
            }
        }

        return list;
    }

    // MSFT_SmbServerConfiguration has no instances to enumerate; its static GetConfiguration returns the settings in
    // the out parameter Output. As with ForceClose, real systems may not match the documented signature, so a missing
    // Output falls back to the registry values the SMB cmdlets manage.
    internal static SmbServerInfo SmbServer()
    {
        string why;
        try
        {
            using var type = new ManagementClass(SmbScope + ":MSFT_SmbServerConfiguration");
            using var result = type.InvokeMethod("GetConfiguration", null, null);
            if (result is not null && StateReader.Prop(result, "Output") is ManagementBaseObject config)
            {
                using (config)
                {
                    AppLog.Write("plan", "smb server: read through MSFT_SmbServerConfiguration.GetConfiguration");
                    return new SmbServerInfo(
                        StateReader.Prop(config, "EnableSMB1Protocol") as bool?,
                        StateReader.Prop(config, "EnableSMB2Protocol") as bool?,
                        StateReader.Prop(config, "RejectUnencryptedAccess") as bool?);
                }
            }

            why = "GetConfiguration output: " + SmbMonitor.DescribeOutput(result);
        }
        catch (Exception ex) when (State.IsExpected(ex))
        {
            why = "GetConfiguration: " + ex.GetType().Name + ": " + ex.Message;
        }

        var fallback = SmbServerFromRegistry();
        AppLog.Write("plan", "smb server: read from the LanmanServer registry values (" + why + ")");
        return fallback;
    }

    // SMB1 and SMB2 are 1 (enabled) when absent [Learn: "Detect, enable, and disable SMBv1, SMBv2, and SMBv3"].
    // RejectUnencryptedAccess defaults to true as in Set-SmbServerConfiguration [unverified: Learn documents the
    // cmdlet default, not the registry value].
    private static SmbServerInfo SmbServerFromRegistry()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters")
            ?? throw new InvalidOperationException("LanmanServer\\Parameters is missing");
        bool Flag(string name) => key.GetValue(name) is int value ? value != 0 : true;
        return new SmbServerInfo(Flag("SMB1"), Flag("SMB2"), Flag("RejectUnencryptedAccess"));
    }

    // The permissions of one share through MSFT_SmbShare.GetAccessControlEntries, with the share's SDDL security
    // descriptor as the fallback when the method gives no Output. Account names are localized, so each one is
    // resolved to a SID; the planner compares SIDs only.
    internal static List<ShareAccessEntry>? ShareAccess(string shareName)
    {
        var name = Validators.ShareName(shareName);
        if (!name.IsValid)
        {
            return null;
        }

        var wql = "SELECT * FROM MSFT_SmbShare WHERE Name = '" + name.Value!.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        using var searcher = new ManagementObjectSearcher(new ManagementScope(SmbScope), new ObjectQuery(wql));
        using var results = searcher.Get();
        foreach (ManagementObject share in results)
        {
            using (share)
            {
                ManagementBaseObject? result = null;
                string why;
                try
                {
                    result = share.InvokeMethod("GetAccessControlEntries", null, null);
                    why = "output " + SmbMonitor.DescribeOutput(result);
                }
                catch (ManagementException ex) when (!State.IsAccessDenied(ex))
                {
                    why = ex.GetType().Name + ": " + ex.Message;
                }

                using var owned = result;
                if (result is not null && StateReader.Prop(result, "Output") is ManagementBaseObject[] entries)
                {
                    var list = new List<ShareAccessEntry>();
                    foreach (var entry in entries)
                    {
                        using (entry)
                        {
                            var account = StateReader.Prop(entry, "AccountName") as string ?? "?";
                            list.Add(new ShareAccessEntry(account, SidOf(account), ToInt(StateReader.Prop(entry, "AccessControlType")) ?? 1,
                                ToInt(StateReader.Prop(entry, "AccessRight")) ?? 3));
                        }
                    }

                    AppLog.Write("plan", "Share access: " + list.Count.ToString(CultureInfo.InvariantCulture) + " entries through GetAccessControlEntries");
                    return list;
                }

                var sddl = StateReader.Prop(share, "SecurityDescriptor") as string;
                AppLog.Write("plan", "Share access: GetAccessControlEntries " + why + "; " + (sddl is null ? "no security descriptor" : "read from the security descriptor"));
                return sddl is null ? throw new InvalidOperationException("No share permissions returned") : FromSddl(sddl);
            }
        }

        return null;
    }

    private static string? SidOf(string account)
    {
        try
        {
            return ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return null;
        }
    }

    private static string AccountOf(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return sid.Value;
        }
    }

    // Share rights as the SMB cmdlets name them: Full 0x1F01FF, Change 0x1301BF, Read 0x1200A9; anything else Custom.
    private static List<ShareAccessEntry> FromSddl(string sddl)
    {
        var list = new List<ShareAccessEntry>();
        var acl = new RawSecurityDescriptor(sddl).DiscretionaryAcl;
        if (acl is null)
        {
            return list;
        }

        foreach (var ace in acl)
        {
            if (ace is not CommonAce common || common.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
            {
                continue;
            }

            var right = common.AccessMask switch
            {
                0x1F01FF => 0,
                0x1301BF => 1,
                0x1200A9 => 2,
                _ => 3,
            };
            list.Add(new ShareAccessEntry(AccountOf(common.SecurityIdentifier), common.SecurityIdentifier.Value,
                common.AceQualifier == AceQualifier.AccessAllowed ? 0 : 1, right));
        }

        return list;
    }

    internal static int? ServerStart()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer");
        return key?.GetValue("Start") is int start ? start : null;
    }

    private static Dictionary<string, Reading<int?>> Hardening()
    {
        var result = new Dictionary<string, Reading<int?>>(StringComparer.OrdinalIgnoreCase);
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var h in Planner.Hardening)
        {
            result[h.Name] = Part("hardening " + h.Name, () =>
            {
                using var key = hklm.OpenSubKey(h.Key);
                return key?.GetValue(h.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is int value ? (int?)value : null;
            });
        }

        return result;
    }

    internal static LockoutInfo Lockout()
    {
        var status = NetUserModalsGet(null, 3, out var buffer);
        try
        {
            if (status != 0)
            {
                throw new Win32Exception(status);
            }

            var info = Marshal.PtrToStructure<UserModalsInfo3>(buffer);
            return new LockoutInfo(info.Threshold, info.Duration, info.ObservationWindow);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _ = NetApiBufferFree(buffer);
            }
        }
    }

    internal static AuditSetting AuditLogon()
    {
        using var privilege = Startup.IsElevated ? SecurityPrivilege.Enable() : null;
        var guid = new Guid(LogonSubcategory);
        if (!AuditQuerySystemPolicy(ref guid, 1, out var buffer))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            // Read as an array of structures; if the GUID does not match, the buffer starts with a pointer to it.
            var info = Marshal.PtrToStructure<AuditPolicyInformation>(buffer);
            if (info.SubCategory != guid)
            {
                info = Marshal.PtrToStructure<AuditPolicyInformation>(Marshal.ReadIntPtr(buffer));
            }

            if (info.SubCategory != guid)
            {
                throw new InvalidOperationException("AuditQuerySystemPolicy returned another subcategory");
            }

            return new AuditSetting((info.AuditingInformation & 1) != 0, (info.AuditingInformation & 2) != 0);
        }
        finally
        {
            AuditFree(buffer);
        }
    }

    internal static int? Smb1Feature()
    {
        foreach (var feature in StateReader.Query(@"root\cimv2", "SELECT InstallState FROM Win32_OptionalFeature WHERE Name = 'SMB1Protocol'"))
        {
            using (feature)
            {
                return ToInt(StateReader.Prop(feature, "InstallState"));
            }
        }

        return null;
    }

    internal static bool? GuestEnabled()
    {
        var resume = 0u;
        var status = NetUserEnum(null, 20, FilterNormalAccount, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
        try
        {
            if (status != 0 && status != ErrorMoreData)
            {
                throw new Win32Exception(status);
            }

            var size = Marshal.SizeOf<UserInfo20>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<UserInfo20>(buffer + (i * size));
                if (info.UserId == 501)
                {
                    return (info.Flags & LocalAccounts.UfAccountDisable) == 0;
                }
            }

            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _ = NetApiBufferFree(buffer);
            }
        }
    }

    // CPFS's own rules through the firewall API, which reports ports, addresses and interfaces with the rule.
    internal static IReadOnlyList<FirewallRuleInfo> OwnRules()
    {
        var list = new List<FirewallRuleInfo>();
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
        var policy = Activator.CreateInstance(type)!;
        object? rules = null;
        try
        {
            rules = StateReader.Get(policy, "Rules");
            foreach (var rule in (IEnumerable)rules!)
            {
                try
                {
                    var name = StateReader.Get(rule, "Name") as string;
                    if (!FirewallRules.IsOwnName(name))
                    {
                        continue;
                    }

                    var interfaces = new List<string>();
                    if (StateReader.Get(rule, "Interfaces") is object[] names)
                    {
                        foreach (var n in names)
                        {
                            if (n is string s && s.Length > 0 && s != "*")
                            {
                                interfaces.Add(s);
                            }
                        }
                    }

                    list.Add(new FirewallRuleInfo(
                        name!,
                        StateReader.Get(rule, "Enabled") as bool? ?? false,
                        ToInt(StateReader.Get(rule, "Direction")) ?? 0,
                        ToInt(StateReader.Get(rule, "Action")) ?? 0,
                        ToInt(StateReader.Get(rule, "Protocol")) ?? 0,
                        StateReader.Get(rule, "LocalPorts") as string,
                        ToInt(StateReader.Get(rule, "Profiles")) ?? 0,
                        StateReader.Get(rule, "RemoteAddresses") as string,
                        interfaces));
                }
                finally
                {
                    StateReader.Release(rule);
                }
            }
        }
        finally
        {
            StateReader.Release(rules);
            StateReader.Release(policy);
        }

        return list;
    }

    // The built-in File and Printer Sharing rules by their rule id (what Tracked.FirewallRules holds). Ports come from
    // MSFT_NetProtocolPortFilter, joined by InstanceID [unverified: Get-NetFirewallPortFilter documents a one-to-one
    // filter per rule; the shared InstanceID is what the CI self-test checks]. They are read only when a rule is
    // open on Public, since the filter query lists every rule's ports.
    internal static IReadOnlyList<BuiltInRuleInfo> BuiltInRules()
    {
        var rules = new List<(string Id, string? Display, bool Enabled, int Direction, int Profiles)>();
        foreach (var rule in StateReader.Query(FirewallScope,
                     "SELECT InstanceID, DisplayName, Enabled, Direction, Profiles FROM MSFT_NetFirewallRule WHERE RuleGroup = '" + FirewallRules.FileSharingGroup + "'"))
        {
            using (rule)
            {
                if (StateReader.Prop(rule, "InstanceID") is string id)
                {
                    rules.Add((id, StateReader.Prop(rule, "DisplayName") as string, ToInt(StateReader.Prop(rule, "Enabled")) == 1,
                        ToInt(StateReader.Prop(rule, "Direction")) ?? 0, ToInt(StateReader.Prop(rule, "Profiles")) ?? 0));
                }
            }
        }

        var exposed = rules.Exists(r => r.Enabled && r.Direction == FirewallRules.Inbound
            && (FirewallRules.IsAllProfiles(r.Profiles) || (r.Profiles & FirewallRules.ProfilePublic) != 0));
        Dictionary<string, string[]>? ports = null;
        if (exposed)
        {
            try
            {
                ports = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var filter in StateReader.Query(FirewallScope, "SELECT InstanceID, LocalPort FROM MSFT_NetProtocolPortFilter"))
                {
                    using (filter)
                    {
                        if (StateReader.Prop(filter, "InstanceID") is string id)
                        {
                            ports[id] = StateReader.Prop(filter, "LocalPort") as string[] ?? Array.Empty<string>();
                        }
                    }
                }
            }
            catch (Exception ex) when (State.IsExpected(ex))
            {
                AppLog.Write("plan", "Port filters: " + ex.GetType().Name + ": " + ex.Message);
                ports = null;
            }
        }

        var list = new List<BuiltInRuleInfo>();
        foreach (var r in rules)
        {
            string[]? localPorts = null;
            if (ports is not null && ports.TryGetValue(r.Id, out var p))
            {
                localPorts = p;
            }

            list.Add(new BuiltInRuleInfo(r.Id, r.Display, r.Enabled, r.Direction, r.Profiles, localPorts));
        }

        AppLog.Write("plan", "File and Printer Sharing rules: " + list.Count.ToString(CultureInfo.InvariantCulture)
            + (ports is null ? "" : ", port filters " + ports.Count.ToString(CultureInfo.InvariantCulture)));
        return list;
    }

    internal static IReadOnlyList<NetworkInfo> Networks()
    {
        var list = new List<NetworkInfo>();
        foreach (var profile in StateReader.Query(FirewallScope, "SELECT Name, InterfaceAlias, NetworkCategory FROM MSFT_NetConnectionProfile"))
        {
            using (profile)
            {
                list.Add(new NetworkInfo(
                    StateReader.Prop(profile, "Name") as string ?? "?",
                    StateReader.Prop(profile, "InterfaceAlias") as string,
                    ToInt(StateReader.Prop(profile, "NetworkCategory")) ?? 0));
            }
        }

        return list;
    }

    private static List<VpnAdapterInfo> VpnAdapters()
    {
        var list = new List<VpnAdapterInfo>();
        try
        {
            foreach (var a in NetworkAddresses.Read())
            {
                if (ConnectAddresses.IsVpn(a.Address))
                {
                    list.Add(new VpnAdapterInfo(a.Adapter, a.Address.ToString()));
                }
            }
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException)
        {
            AppLog.Write("plan", "Adapters: " + ex.Message);
        }

        return list;
    }

    internal static IReadOnlyDictionary<string, bool> Tasks()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        var service = Activator.CreateInstance(type)!;
        object? folder = null;
        try
        {
            StateReader.Invoke(service, "Connect");
            folder = StateReader.Invoke(service, "GetFolder", @"\")!;
            foreach (var name in TaskNames)
            {
                object? task = null;
                try
                {
                    task = StateReader.Invoke(folder, "GetTask", name);
                    result[name] = true;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is COMException { HResult: unchecked((int)0x80070002) } or FileNotFoundException)
                {
                    result[name] = false;
                }
                finally
                {
                    StateReader.Release(task);
                }
            }
        }
        finally
        {
            StateReader.Release(folder);
            StateReader.Release(service);
        }

        return result;
    }

    internal static DataFolderInfo DataFolder()
    {
        var dir = new DirectoryInfo(StateReader.DataFolder);
        if (!dir.Exists)
        {
            return new DataFolderInfo(false, false, false, false);
        }

        var security = dir.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var othersWrite = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & WriteRights) != 0
                && rule.IdentityReference is SecurityIdentifier sid && !IsTrustedWriter(sid))
            {
                othersWrite = true;
            }
        }

        return new DataFolderInfo(true, owner?.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) == true, security.AreAccessRulesProtected, othersWrite);
    }

    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // SYSTEM, Administrators, CREATOR OWNER (the owner is Administrators) and TrustedInstaller.
    private static bool IsTrustedWriter(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)
        || string.Equals(sid.Value, "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", StringComparison.Ordinal);

    private static IReadOnlyDictionary<char, string> DosDevices()
    {
        var result = new Dictionary<char, string>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\DOS Devices");
        foreach (var name in key?.GetValueNames() ?? Array.Empty<string>())
        {
            if (name.Length == 2 && name[1] == ':' && char.IsLetter(name[0])
                && key!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
            {
                result[char.ToUpperInvariant(name[0])] = value;
            }
        }

        return result;
    }

    // DriveIcons\<L>\DefaultLabel or \DefaultIcon, (Default) value, by letter.
    private static Dictionary<char, string> DriveIconValues(string subkey)
    {
        var result = new Dictionary<char, string>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var icons = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons");
        foreach (var name in icons?.GetSubKeyNames() ?? Array.Empty<string>())
        {
            if (name.Length != 1 || !char.IsLetter(name[0]))
            {
                continue;
            }

            using var value = icons!.OpenSubKey(name + "\\" + subkey);
            if (value?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string text)
            {
                result[char.ToUpperInvariant(name[0])] = text;
            }
        }

        return result;
    }

    private static List<string> DesktopFiles(string folder)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return list;
        }

        foreach (var file in Directory.GetFiles(folder))
        {
            list.Add(Path.GetFileName(file));
        }

        return list;
    }

    internal static UserInfo? User(string name)
    {
        if (name.Length == 0)
        {
            return null;
        }

        var user = LocalAccounts.GetUser(name);
        if (user is null)
        {
            return null;
        }

        bool? admin = null, users = null;
        try
        {
            admin = LocalAccounts.IsMember(user.Sid, "S-1-5-32-544");
            users = LocalAccounts.IsMember(user.Sid, "S-1-5-32-545");
        }
        catch (Exception ex) when (State.IsExpected(ex) || ex is IdentityNotMappedException)
        {
            AppLog.Write("plan", "Group membership: " + ex.GetType().Name + ": " + ex.Message);
        }

        int? hidden = null;
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList"))
        {
            if (key?.GetValue(name) is int value)
            {
                hidden = value;
            }
        }

        return new UserInfo(user.Sid.Value, user.Enabled, user.PasswordNeverExpires, user.Locked, admin, users, hidden);
    }

    internal static FolderInfo Folder(string path, string? userSid)
    {
        if (path.Length < 3 || !Directory.Exists(path))
        {
            return new FolderInfo(false, false);
        }

        if (userSid is null)
        {
            return new FolderInfo(true, false);
        }

        var sid = new SecurityIdentifier(userSid);
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
        var modify = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            // icacls "<user>:(OI)(CI)M", as the scripts grant it.
            if (rule.AccessControlType == AccessControlType.Allow && sid.Equals(rule.IdentityReference)
                && (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify
                && rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
            {
                modify = true;
            }
        }

        return new FolderInfo(true, modify);
    }

    private static int? ToInt(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    // Enables SeSecurityPrivilege on this process's token for one read and restores the previous state afterwards.
    private sealed class SecurityPrivilege : IDisposable
    {
        private readonly IntPtr _token;
        private readonly TokenPrivileges _previous;
        private readonly bool _changed;

        private SecurityPrivilege(IntPtr token, TokenPrivileges previous, bool changed)
        {
            _token = token;
            _previous = previous;
            _changed = changed;
        }

        public static SecurityPrivilege? Enable()
        {
            IntPtr token;
            using (var self = Process.GetCurrentProcess())
            {
                if (!OpenProcessToken(self.Handle, TokenAdjustPrivileges | TokenQuery, out token))
                {
                    token = IntPtr.Zero;
                }
            }

            if (token == IntPtr.Zero)
            {
                AppLog.Write("plan", "OpenProcessToken: " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
                return null;
            }

            if (!LookupPrivilegeValue(null, "SeSecurityPrivilege", out var luid))
            {
                _ = Native.CloseHandle(token);
                return null;
            }

            var wanted = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            var ok = AdjustTokenPrivileges(token, false, ref wanted, Marshal.SizeOf<TokenPrivileges>(), out var previous, out _);
            var error = Marshal.GetLastWin32Error();
            if (!ok || error == ErrorNotAllAssigned)
            {
                AppLog.Write("plan", "SeSecurityPrivilege not enabled: " + error.ToString(CultureInfo.InvariantCulture));
                _ = Native.CloseHandle(token);
                return null;
            }

            return new SecurityPrivilege(token, previous, previous.Count > 0);
        }

        public void Dispose()
        {
            if (_changed)
            {
                var restore = _previous;
                _ = AdjustTokenPrivileges(_token, false, ref restore, Marshal.SizeOf<TokenPrivileges>(), out _, out _);
            }

            _ = Native.CloseHandle(_token);
        }
    }

    private const int FilterNormalAccount = 0x0002;
    private const uint MaxPreferredLength = 0xFFFFFFFF;
    private const int ErrorMoreData = 234;
    private const int ErrorNotAllAssigned = 1300;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct UserModalsInfo3
    {
        public uint Duration;
        public uint ObservationWindow;
        public uint Threshold;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo20
    {
        public IntPtr Name;
        public IntPtr FullName;
        public IntPtr Comment;
        public uint Flags;
        public uint UserId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AuditPolicyInformation
    {
        public Guid SubCategory;
        public uint AuditingInformation;
        public Guid Category;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserModalsGet(string? serverName, int level, out IntPtr buffer);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserEnum(string? serverName, int level, int filter, out IntPtr buffer, uint preferredMaxLength,
        out int entriesRead, out int totalEntries, ref uint resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AuditQuerySystemPolicy(ref Guid subCategoryGuids, uint policyCount, out IntPtr auditPolicy);

    [DllImport("advapi32.dll")]
    private static extern void AuditFree(IntPtr buffer);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, int bufferLength,
        out TokenPrivileges previousState, out int returnLength);
}
