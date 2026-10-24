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
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Cpfs.Core.Apply;
using Cpfs.Core.Planning;
using Microsoft.Win32;

namespace Cpfs.Windows.Apply;

// Every call in CPFS that changes Windows is in this file and QuickAccessWrites.cs (and the --user-session verb, which
// maps the drive in the user's own session). Only the executor's steps (SetupSteps, UninstallSteps) call them, after
// the user pressed Apply and confirmed.
internal static class Accounts
{
    public const string AdminsSid = "S-1-5-32-544";
    public const string UsersSid = "S-1-5-32-545";
    public const string Comment = "CPFS - SMB file sharing only";

    // The password goes from the SecureString into unmanaged memory for the call and is zeroed straight after.
    public static void Create(string name, SecureString password)
    {
        var secret = Marshal.SecureStringToGlobalAllocUnicode(password);
        try
        {
            var info = new WinApi.UserInfo1
            {
                Name = name,
                Password = secret,
                Priv = WinApi.UserPrivUser,
                Comment = Comment,
                Flags = WinApi.UfScript | WinApi.UfNormalAccount | WinApi.UfDontExpirePasswd | WinApi.UfPasswdCantChange,
            };
            Check(WinApi.NetUserAdd(null, 1, ref info, out _), "NetUserAdd");
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(secret);
        }
    }

    public static void SetPassword(string name, SecureString password)
    {
        var secret = Marshal.SecureStringToGlobalAllocUnicode(password);
        try
        {
            var info = new WinApi.UserInfo1003 { Password = secret };
            Check(WinApi.NetUserSetInfo(null, name, 1003, ref info, out _), "NetUserSetInfo 1003");
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(secret);
        }
    }

    // UF_LOCKOUT can only be cleared, so it is never written back: every flags change also unlocks the account,
    // which the plan wants anyway.
    public static void SetFlags(string name, Func<uint, uint> change)
    {
        var user = LocalAccounts.GetUser(name) ?? throw new InvalidOperationException("The account does not exist.");
        var info = new WinApi.UserInfo1008 { Flags = (change(user.Flags) | WinApi.UfScript) & ~WinApi.UfLockout };
        Check(WinApi.NetUserSetInfo(null, name, 1008, ref info, out _), "NetUserSetInfo 1008");
    }

    public static void AddToGroup(SecurityIdentifier user, string groupSid) => Member(user, groupSid, add: true);

    public static void RemoveFromGroup(SecurityIdentifier user, string groupSid) => Member(user, groupSid, add: false);

    public static void Delete(string name)
    {
        var status = WinApi.NetUserDel(null, name);
        if (status != WinApi.NerrUserNotFound)
        {
            Check(status, "NetUserDel");
        }
    }

    // The account with RID 501, by the name Windows gives it (localized); null when there is none.
    public static string? GuestName()
    {
        var resume = 0u;
        var status = WinApi.NetUserEnum(null, 20, 0x0002, out var buffer, 0xFFFFFFFF, out var read, out _, ref resume);
        try
        {
            if (status != 0 && status != 234)
            {
                throw new Win32Exception(status);
            }

            var size = Marshal.SizeOf<UserInfo20>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<UserInfo20>(buffer + (i * size));
                if (info.UserId == 501)
                {
                    return Marshal.PtrToStringUni(info.Name);
                }
            }

            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _ = WinApi.NetApiBufferFree(buffer);
            }
        }
    }

    public static string GroupName(string groupSid)
    {
        var account = new SecurityIdentifier(groupSid).Translate(typeof(NTAccount)).Value;
        return account.Substring(account.LastIndexOf('\\') + 1);
    }

    private static void Member(SecurityIdentifier user, string groupSid, bool add)
    {
        var bytes = new byte[user.BinaryLength];
        user.GetBinaryForm(bytes, 0);
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var info = new WinApi.LocalGroupMembersInfo0 { Sid = handle.AddrOfPinnedObject() };
            var group = GroupName(groupSid);
            var status = add
                ? WinApi.NetLocalGroupAddMembers(null, group, 0, ref info, 1)
                : WinApi.NetLocalGroupDelMembers(null, group, 0, ref info, 1);
            if (status != (add ? WinApi.ErrorMemberInAlias : WinApi.ErrorMemberNotInAlias))
            {
                Check(status, add ? "NetLocalGroupAddMembers" : "NetLocalGroupDelMembers");
            }
        }
        finally
        {
            handle.Free();
        }
    }

    internal static void Check(int status, string call)
    {
        if (status != WinApi.NerrSuccess)
        {
            throw new Win32Exception(status, call + ": " + new Win32Exception(status).Message);
        }
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
}

internal static class Reg
{
    public const string UserList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";
    public const string NetworkProfiles = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles";

    public static int? ReadDword(string subKey, string name)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(subKey);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is int value ? value : null;
    }

    public static bool Exists(string subKey, string name)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(subKey);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null;
    }

    public static void SetDword(string subKey, string name, int value)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.CreateSubKey(subKey, writable: true) ?? throw new IOException("Could not open HKLM\\" + subKey);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    public static void Delete(string subKey, string name)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    // The saved networks (connected or not) with this profile name, as NetworkList\Profiles subkey names.
    public static List<string> ProfilesNamed(string profileName)
    {
        var list = new List<string>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var profiles = hklm.OpenSubKey(NetworkProfiles);
        foreach (var id in profiles?.GetSubKeyNames() ?? Array.Empty<string>())
        {
            using var p = profiles!.OpenSubKey(id);
            if (string.Equals(p?.GetValue("ProfileName") as string, profileName, StringComparison.Ordinal))
            {
                list.Add(id);
            }
        }

        return list;
    }
}

internal static class Folders
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    // icacls "<path>" /grant "<user>:(OI)(CI)M", as the scripts grant it. Only this one entry is added.
    public static void GrantModify(string path, SecurityIdentifier sid)
    {
        var dir = new DirectoryInfo(path);
        var security = dir.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify, Inherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(security);
    }

    // The account's own entries on the shared folder itself; what children inherit from it goes with them.
    public static void RemoveExplicit(string path, SecurityIdentifier sid)
    {
        var dir = new DirectoryInfo(path);
        var security = dir.GetAccessControl(AccessControlSections.Access);
        security.PurgeAccessRules(sid);
        dir.SetAccessControl(security);
    }

    public static bool HasExplicit(string path, SecurityIdentifier sid)
    {
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            if (sid.Equals(rule.IdentityReference))
            {
                return true;
            }
        }

        return false;
    }

    // ProgramData\CPFS: owned by Administrators (an owner can always rewrite the ACL), SYSTEM and Administrators full,
    // Users read, nothing inherited. What is inside goes back to inheriting only that.
    public static void LockDataFolder(string path)
    {
        Directory.CreateDirectory(path);
        var dir = new DirectoryInfo(path);

        var owner = new DirectorySecurity();
        owner.SetOwner(Admins);
        dir.SetAccessControl(owner);

        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(acl);

        foreach (var entry in Walk(path, includeRoot: false))
        {
            if (entry is DirectoryInfo d)
            {
                var o = new DirectorySecurity();
                o.SetOwner(Admins);
                d.SetAccessControl(o);
                var reset = new DirectorySecurity();
                reset.SetAccessRuleProtection(false, false);
                d.SetAccessControl(reset);
            }
            else if (entry is FileInfo f)
            {
                var o = new FileSecurity();
                o.SetOwner(Admins);
                f.SetAccessControl(o);
                var reset = new FileSecurity();
                reset.SetAccessRuleProtection(false, false);
                f.SetAccessControl(reset);
            }
        }
    }

    // Gives what the SMB account owns to the desktop user; nothing else is touched. Needs SeRestorePrivilege.
    public static (int Changed, int Failed) GiveOwnership(string root, SecurityIdentifier from, SecurityIdentifier to)
    {
        int changed = 0, failed = 0;
        foreach (var entry in Walk(root, includeRoot: true))
        {
            try
            {
                var owner = entry is DirectoryInfo d0
                    ? d0.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))
                    : ((FileInfo)entry).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
                if (!from.Equals(owner))
                {
                    continue;
                }

                if (entry is DirectoryInfo d)
                {
                    var s = new DirectorySecurity();
                    s.SetOwner(to);
                    d.SetAccessControl(s);
                }
                else
                {
                    var s = new FileSecurity();
                    s.SetOwner(to);
                    ((FileInfo)entry).SetAccessControl(s);
                }

                changed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
            {
                failed++;
            }
        }

        return (changed, failed);
    }

    // Every file and folder below root; junctions and symbolic links are listed but never followed out of the tree.
    private static IEnumerable<FileSystemInfo> Walk(string root, bool includeRoot)
    {
        var top = new DirectoryInfo(root);
        if (includeRoot)
        {
            yield return top;
        }

        var todo = new Stack<DirectoryInfo>();
        todo.Push(top);
        while (todo.Count > 0)
        {
            var dir = todo.Pop();
            FileSystemInfo[] children;
            try
            {
                children = dir.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                yield return child;
                if (child is DirectoryInfo sub && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    todo.Push(sub);
                }
            }
        }
    }
}

internal static class Service
{
    public const string Server = "LanmanServer";

    // 2 automatic, 3 manual, 4 disabled.
    public static void SetStart(string name, int start)
    {
        var manager = WinApi.OpenSCManager(null, null, WinApi.ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager");
        }

        try
        {
            var service = WinApi.OpenService(manager, name, WinApi.ServiceChangeConfig | WinApi.ServiceQueryConfig);
            if (service == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenService " + name);
            }

            try
            {
                if (!WinApi.ChangeServiceConfig(service, WinApi.ServiceNoChange, (uint)start, WinApi.ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ChangeServiceConfig " + name);
                }
            }
            finally
            {
                _ = WinApi.CloseServiceHandle(service);
            }
        }
        finally
        {
            _ = WinApi.CloseServiceHandle(manager);
        }
    }

    public static void Start(string name)
    {
        using var controller = new ServiceController(name);
        if (controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            return;
        }

        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }
}

internal static class Lockout
{
    public static void Set(uint threshold, uint durationSeconds, uint windowSeconds)
    {
        var info = new WinApi.UserModalsInfo3 { Duration = durationSeconds, ObservationWindow = windowSeconds, Threshold = threshold };
        Accounts.Check(WinApi.NetUserModalsSet(null, 3, ref info, out _), "NetUserModalsSet 3");
    }
}

internal static class Audit
{
    private static readonly Guid Logon = new("0CCE9215-69AE-11D9-BED3-505054503030");

    // POLICY_AUDIT_EVENT_SUCCESS 1, FAILURE 2, NONE 4 [Learn: AuditSetSystemPolicy]. Needs SeSecurityPrivilege.
    public static void SetLogon(bool success, bool failure)
    {
        var flags = (success ? 1u : 0u) | (failure ? 2u : 0u);
        var info = new WinApi.AuditPolicyInformation { SubCategory = Logon, AuditingInformation = flags == 0 ? 4u : flags };
        if (!WinApi.AuditSetSystemPolicy(ref info, 1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AuditSetSystemPolicy");
        }
    }
}

internal static class Smb1Feature
{
    // dism.exe because the DISM API is a large surface for one call; only its exit code is read, never its localized
    // output. Returns true when Windows needs a restart to finish.
    public static bool Disable()
    {
        var dism = Path.Combine(Environment.SystemDirectory, "dism.exe");
        using var process = Process.Start(new ProcessStartInfo(dism, "/online /disable-feature /featurename:SMB1Protocol /norestart /quiet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("dism.exe did not start");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit((int)TimeSpan.FromMinutes(15).TotalMilliseconds))
        {
            throw new System.TimeoutException("dism.exe did not finish within 15 minutes");
        }

        AppLog.Write("apply", "dism.exe exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture));
        return process.ExitCode switch
        {
            0 => false,
            3010 => true,
            var code => throw new InvalidOperationException("dism.exe ended with exit code " + code.ToString(CultureInfo.InvariantCulture)),
        };
    }
}

internal static class Wmi
{
    public const string Smb = @"root\Microsoft\Windows\SMB";
    public const string Std = @"root\StandardCimv2";

    public static string Quote(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    public static List<ManagementObject> All(string scope, string wql)
    {
        var list = new List<ManagementObject>();
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));
        using var results = searcher.Get();
        foreach (ManagementObject o in results)
        {
            list.Add(o);
        }

        return list;
    }

    public static void Dispose(IEnumerable<ManagementObject> objects)
    {
        foreach (var o in objects)
        {
            o.Dispose();
        }
    }

    // Sets an in-parameter or property in its CIM type: a uint32 given as an int is refused by WMI otherwise.
    public static void Set(ManagementBaseObject target, string name, object? value)
    {
        var property = target.Properties[name];
        if (value is null || property.IsArray)
        {
            target[name] = value;
            return;
        }

        target[name] = property.Type switch
        {
            CimType.UInt8 => Convert.ToByte(value, CultureInfo.InvariantCulture),
            CimType.UInt16 => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
            CimType.UInt32 => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
            CimType.UInt64 => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
            CimType.SInt16 => Convert.ToInt16(value, CultureInfo.InvariantCulture),
            CimType.SInt32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            CimType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            CimType.String => Convert.ToString(value, CultureInfo.InvariantCulture),
            _ => value,
        };
    }

    // A ReturnValue other than 0 is an error; a missing one (as ForceClose showed on 25H2) is left to the caller's
    // check by reading again.
    public static void CheckReturn(ManagementBaseObject? result, string call)
    {
        if (result is null)
        {
            return;
        }

        foreach (PropertyData p in result.Properties)
        {
            if (string.Equals(p.Name, "ReturnValue", StringComparison.OrdinalIgnoreCase) && p.Value is not null)
            {
                var code = Convert.ToInt64(p.Value, CultureInfo.InvariantCulture);
                if (code != 0)
                {
                    throw new InvalidOperationException(call + " returned " + code.ToString(CultureInfo.InvariantCulture)
                        + " (0x" + code.ToString("X", CultureInfo.InvariantCulture) + ")");
                }
            }
        }
    }

    public static bool IsUnsupported(ManagementException ex) => ex.ErrorCode is ManagementStatus.NotSupported or ManagementStatus.InvalidMethod
        or ManagementStatus.MethodNotImplemented or ManagementStatus.InvalidMethodParameters or ManagementStatus.NotFound;
}

internal static class Shares
{
    public static ManagementObject? Find(string name)
    {
        var all = Wmi.All(Wmi.Smb, "SELECT * FROM MSFT_SmbShare WHERE Name = '" + Wmi.Quote(name) + "'");
        for (var i = 1; i < all.Count; i++)
        {
            all[i].Dispose();
        }

        return all.Count > 0 ? all[0] : null;
    }

    // CreateShare(Name, Path, Description, FolderEnumerationMode 0 AccessBased, CachingMode 0 None, EncryptData,
    // FullAccess[]) [Learn: MSFT_SmbShare CreateShare].
    public static void Create(string name, string path, bool encrypt, string fullAccessAccount)
    {
        using var type = new ManagementClass(Wmi.Smb + ":MSFT_SmbShare");
        using var args = type.GetMethodParameters("CreateShare");
        Wmi.Set(args, "Name", name);
        Wmi.Set(args, "Path", path);
        Wmi.Set(args, "Description", Planner.ShareDescription);
        Wmi.Set(args, "FolderEnumerationMode", 0);
        Wmi.Set(args, "CachingMode", 0);
        Wmi.Set(args, "EncryptData", encrypt);
        Wmi.Set(args, "FullAccess", new[] { fullAccessAccount });
        using var result = type.InvokeMethod("CreateShare", args, null);
        Wmi.CheckReturn(result, "MSFT_SmbShare.CreateShare");
    }

    // In place, as Set-SmbShare does: the share stays, and so do its sessions.
    public static void Update(string name, bool encrypt)
    {
        using var share = Find(name) ?? throw new InvalidOperationException("The share is gone.");
        Wmi.Set(share, "EncryptData", encrypt);
        Wmi.Set(share, "FolderEnumerationMode", 0);
        Wmi.Set(share, "CachingMode", 0);
        Wmi.Set(share, "Description", Planner.ShareDescription);
        share.Put(new PutOptions { Type = PutType.UpdateOnly });
    }

    public static void Delete(string name)
    {
        using var share = Find(name);
        share?.Delete();
    }

    // GrantAccess(AccountName[], AccessRight 0 Full), RevokeAccess and UnblockAccess(AccountName[]).
    public static void Access(string share, string method, string account, int? right = null)
    {
        using var obj = Find(share) ?? throw new InvalidOperationException("The share is gone.");
        using var args = obj.GetMethodParameters(method);
        Wmi.Set(args, "AccountName", new[] { account });
        if (right is { } r)
        {
            Wmi.Set(args, "AccessRight", r);
        }

        using var result = obj.InvokeMethod(method, args, null);
        Wmi.CheckReturn(result, "MSFT_SmbShare." + method);
    }

    // Closes the sessions of one account before its share is removed, as the scripts do.
    public static int CloseSessions(string user)
    {
        var closed = 0;
        var sessions = Wmi.All(Wmi.Smb, "SELECT * FROM MSFT_SmbSession");
        try
        {
            foreach (var s in sessions)
            {
                var client = StateReader.Prop(s, "ClientUserName") as string ?? "";
                if (string.Equals(client.Substring(client.LastIndexOf('\\') + 1), user, StringComparison.OrdinalIgnoreCase))
                {
                    using var _ = s.InvokeMethod("ForceClose", null, null);
                    closed++;
                }
            }
        }
        finally
        {
            Wmi.Dispose(sessions);
        }

        return closed;
    }
}

internal static class SmbServer
{
    // The static SetConfiguration, the counterpart of the static GetConfiguration this machine answered on
    // [unverified: Learn documents an instance method, and this class enumerates no instances on 25H2].
    public static void Set(string property, bool value)
    {
        try
        {
            using var type = new ManagementClass(Wmi.Smb + ":MSFT_SmbServerConfiguration");
            using var args = type.GetMethodParameters("SetConfiguration");
            Wmi.Set(args, property, value);
            using var result = type.InvokeMethod("SetConfiguration", args, null);
            Wmi.CheckReturn(result, "MSFT_SmbServerConfiguration.SetConfiguration");
        }
        catch (ManagementException ex) when (Wmi.IsUnsupported(ex))
        {
            throw new StepUnsupportedException(ex.Message, ex);
        }
    }
}

// CPFS's own rules are written disabled; the caller reads them back and enables them only when they match
// (SetupSteps.Rule), so a property Windows did not take never leaves 445 open on the wrong adapter or profile.
internal static class Firewall
{
    public static void Create(RuleSpec spec)
    {
        var policy = Policy();
        object? rules = null;
        object? rule = null;
        try
        {
            rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
            Put(rule, "Name", spec.Name);
            Put(rule, "Description", Planner.ShareDescription);
            Put(rule, "Protocol", spec.Protocol);
            Put(rule, "LocalPorts", spec.LocalPorts);
            Put(rule, "RemoteAddresses", spec.RemoteAddresses.Count == 0 ? "*" : string.Join(",", spec.RemoteAddresses));
            Put(rule, "Direction", FirewallRules.Inbound);
            Put(rule, "Action", FirewallRules.Allow);
            Put(rule, "Profiles", spec.Profiles);
            if (spec.Interfaces.Count > 0)
            {
                Put(rule, "Interfaces", Names(spec.Interfaces));
            }

            Put(rule, "Enabled", false);
            rules = StateReader.Get(policy, "Rules");
            StateReader.Invoke(rules!, "Add", rule);
        }
        finally
        {
            StateReader.Release(rule);
            StateReader.Release(rules);
            StateReader.Release(policy);
        }
    }

    // Sets the fields of the one rule with this name in place. A changed protocol, or interfaces to clear, can't be
    // set on an existing rule, so that rule is replaced.
    public static void Update(RuleSpec spec, FirewallRuleInfo current)
    {
        if (current.Protocol != spec.Protocol || (spec.Interfaces.Count == 0 && current.Interfaces.Count > 0))
        {
            RemoveAll(spec.Name);
            Create(spec);
            return;
        }

        Each(spec.Name, rule =>
        {
            // Off first: the fields below take effect one by one on the live rule.
            Put(rule, "Enabled", false);
            Put(rule, "LocalPorts", spec.LocalPorts);
            Put(rule, "RemoteAddresses", spec.RemoteAddresses.Count == 0 ? "*" : string.Join(",", spec.RemoteAddresses));
            Put(rule, "Direction", FirewallRules.Inbound);
            Put(rule, "Action", FirewallRules.Allow);
            Put(rule, "Profiles", spec.Profiles);
            if (spec.Interfaces.Count > 0)
            {
                Put(rule, "Interfaces", Names(spec.Interfaces));
            }
        });
    }

    public static void SetEnabled(string name, bool enabled) => Each(name, rule => Put(rule, "Enabled", enabled));

    // INetFwRules.Remove takes a name; whether it removes one or every rule of that name is not documented, so it
    // is called until none is left.
    public static void RemoveAll(string name)
    {
        for (var i = 0; i < 64 && Count(name) > 0; i++)
        {
            var policy = Policy();
            object? rules = null;
            try
            {
                rules = StateReader.Get(policy, "Rules");
                StateReader.Invoke(rules!, "Remove", name);
            }
            finally
            {
                StateReader.Release(rules);
                StateReader.Release(policy);
            }
        }

        if (Count(name) > 0)
        {
            throw new InvalidOperationException("Firewall rules named " + name + " could not all be removed.");
        }
    }

    public static int Count(string name)
    {
        var n = 0;
        Each(name, _ => n++);
        return n;
    }

    // Windows' own File and Printer Sharing rules by rule id, through MSFT_NetFirewallRule's Enable and Disable
    // [unverified: which policy store a plain WMI call reaches; the check reads the rule again].
    public static void SetBuiltIn(string id, bool enable)
    {
        var rules = Wmi.All(Wmi.Std, "SELECT * FROM MSFT_NetFirewallRule WHERE InstanceID = '" + Wmi.Quote(id) + "'");
        try
        {
            if (rules.Count == 0)
            {
                throw new InvalidOperationException("The firewall rule " + id + " is not on this PC.");
            }

            foreach (var rule in rules)
            {
                using var result = rule.InvokeMethod(enable ? "Enable" : "Disable", null, null);
                Wmi.CheckReturn(result, "MSFT_NetFirewallRule." + (enable ? "Enable" : "Disable"));
            }
        }
        finally
        {
            Wmi.Dispose(rules);
        }
    }

    // Null when the rule is not on this PC.
    public static bool? BuiltInEnabled(string id)
    {
        var rules = Wmi.All(Wmi.Std, "SELECT InstanceID, Enabled FROM MSFT_NetFirewallRule WHERE InstanceID = '" + Wmi.Quote(id) + "'");
        try
        {
            bool? enabled = null;
            foreach (var rule in rules)
            {
                enabled = (enabled ?? false) || Convert.ToInt32(StateReader.Prop(rule, "Enabled") ?? 2, CultureInfo.InvariantCulture) == 1;
            }

            return enabled;
        }
        finally
        {
            Wmi.Dispose(rules);
        }
    }

    private static object Policy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;

    private static object[] Names(IReadOnlyList<string> names)
    {
        var array = new object[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            array[i] = names[i];
        }

        return array;
    }

    private static void Each(string name, Action<object> action)
    {
        var policy = Policy();
        object? rules = null;
        try
        {
            rules = StateReader.Get(policy, "Rules");
            foreach (var rule in (IEnumerable)rules!)
            {
                try
                {
                    if (string.Equals(StateReader.Get(rule, "Name") as string, name, StringComparison.OrdinalIgnoreCase))
                    {
                        action(rule);
                    }
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
    }

    private static void Put(object target, string property, object value) =>
        target.GetType().InvokeMember(property, BindingFlags.SetProperty, null, target, new[] { value }, CultureInfo.InvariantCulture);
}

internal static class Networks
{
    // MSFT_NetConnectionProfile.NetworkCategory 0 Public, 1 Private, through ModifyInstance as Set-NetConnectionProfile
    // does [unverified: the class page was not reachable; the check reads the profile again]. Returns the profiles'
    // InstanceIDs.
    public static List<string?> SetCategory(string name, int from, int to)
    {
        var ids = new List<string?>();
        var profiles = Wmi.All(Wmi.Std, "SELECT * FROM MSFT_NetConnectionProfile");
        try
        {
            foreach (var p in profiles)
            {
                if (!string.Equals(StateReader.Prop(p, "Name") as string, name, StringComparison.Ordinal)
                    || Convert.ToInt32(StateReader.Prop(p, "NetworkCategory") ?? -1, CultureInfo.InvariantCulture) != from)
                {
                    continue;
                }

                ids.Add(StateReader.Prop(p, "InstanceID") as string);
                Wmi.Set(p, "NetworkCategory", to);
                p.Put(new PutOptions { Type = PutType.UpdateOnly });
            }
        }
        finally
        {
            Wmi.Dispose(profiles);
        }

        return ids;
    }

    public static List<string?> Ids(string name)
    {
        var ids = new List<string?>();
        var profiles = Wmi.All(Wmi.Std, "SELECT * FROM MSFT_NetConnectionProfile");
        try
        {
            foreach (var p in profiles)
            {
                if (string.Equals(StateReader.Prop(p, "Name") as string, name, StringComparison.Ordinal))
                {
                    ids.Add(StateReader.Prop(p, "InstanceID") as string);
                }
            }
        }
        finally
        {
            Wmi.Dispose(profiles);
        }

        return ids;
    }

    // A saved network that is not connected now: its NetworkList\Profiles entry, as the scripts' Uninstall does.
    public static void SetSavedPublic(string name)
    {
        foreach (var id in Reg.ProfilesNamed(name))
        {
            var key = Reg.NetworkProfiles + "\\" + id;
            if (Reg.ReadDword(key, "Category") == 1)
            {
                Reg.SetDword(key, "Category", 0);
            }
        }
    }

    public static bool AnySavedPrivate(string name)
    {
        foreach (var id in Reg.ProfilesNamed(name))
        {
            if (Reg.ReadDword(Reg.NetworkProfiles + "\\" + id, "Category") == 1)
            {
                return true;
            }
        }

        return false;
    }
}

internal static class Profiles
{
    public static string? PathOf(string sid)
    {
        var profiles = Wmi.All(@"root\cimv2", "SELECT * FROM Win32_UserProfile WHERE SID = '" + Wmi.Quote(sid) + "'");
        try
        {
            return profiles.Count > 0 ? StateReader.Prop(profiles[0], "LocalPath") as string : null;
        }
        finally
        {
            Wmi.Dispose(profiles);
        }
    }

    // Deletes the profile folder and its registry data; only for an account CPFS created, when the user ticked it.
    public static void Delete(string sid)
    {
        var profiles = Wmi.All(@"root\cimv2", "SELECT * FROM Win32_UserProfile WHERE SID = '" + Wmi.Quote(sid) + "'");
        try
        {
            foreach (var p in profiles)
            {
                p.Delete();
            }
        }
        finally
        {
            Wmi.Dispose(profiles);
        }
    }
}
