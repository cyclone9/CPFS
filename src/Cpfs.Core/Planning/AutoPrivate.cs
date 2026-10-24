using System;
using System.Collections.Generic;
using System.Text;
using Cpfs.Core.Config;

namespace Cpfs.Core.Planning;

// The CPFS-AutoPrivate task as Task Scheduler reports it. UserSid is the principal translated to a SID (null when it
// could not be); the numbers are the Task Scheduler 2.0 enum values.
public sealed record ScheduledTaskInfo(
    string? UserSid,
    int LogonType,
    int RunLevel,
    IReadOnlyList<TaskTriggerInfo> Triggers,
    IReadOnlyList<TaskActionInfo> Actions,
    string? ExecutionTimeLimit,
    int MultipleInstances);

public sealed record TaskTriggerInfo(int Type, string? Subscription, string? Delay);

// Type 0 is an exec action; Path and Arguments are null for any other type.
public sealed record TaskActionInfo(int Type, string? Path, string? Arguments);

// The exe copy the task runs in Program Files\CPFS (and whether it is the same build as the running exe), the 0.2.x
// set-private.ps1 in ProgramData\CPFS, and the copy the dev3 build put there (OldExe).
public sealed record AutoPrivateFiles(bool ExeExists, bool ExeIsCurrent, bool LegacyScript, bool OldExe = false);

// Program Files\CPFS. Locked: owned by Administrators, inheritance off, nobody but SYSTEM and Administrators can write.
// HasOtherEntries: anything in it besides Cpfs.exe.
public sealed record AutoPrivateFolderInfo(bool Exists, bool Locked, bool HasOtherEntries);

// The definition Setup registers, and the read-back that tells it apart from anything else under that name.
public static class AutoPrivateTaskSpec
{
    public const string SystemSid = "S-1-5-18";
    public const int LogonServiceAccount = 5;
    public const int RunLevelHighest = 1;
    public const int TriggerEvent = 0;
    public const int ActionExec = 0;
    public const int InstancesIgnoreNew = 2;
    public const string Arguments = "--auto-private";
    public const string ExecutionTimeLimit = "PT1M";
    public const string Delay = "PT5S";
    public const string Channel = "Microsoft-Windows-NetworkProfile/Operational";

    // Event 10000 of the NetworkProfile channel: a network connected (the 0.2.x task's trigger).
    public const string Subscription =
        "<QueryList><Query Id=\"0\" Path=\"" + Channel + "\"><Select Path=\"" + Channel + "\">"
        + "*[System[Provider[@Name='Microsoft-Windows-NetworkProfile'] and EventID=10000]]</Select></Query></QueryList>";

    // The fields that differ from what Setup registers for the copy at exePath; empty when it is CPFS's task as wanted.
    public static IReadOnlyList<string> Differences(ScheduledTaskInfo task, string exePath)
    {
        if (task is null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        var list = new List<string>();
        if (!string.Equals(task.UserSid, SystemSid, StringComparison.OrdinalIgnoreCase))
        {
            list.Add("Principal");
        }

        if (task.LogonType != LogonServiceAccount)
        {
            list.Add("LogonType");
        }

        if (task.RunLevel != RunLevelHighest)
        {
            list.Add("RunLevel");
        }

        if (task.Triggers.Count != 1 || task.Triggers[0].Type != TriggerEvent
            || !string.Equals(Normalized(task.Triggers[0].Subscription), Normalized(Subscription), StringComparison.Ordinal)
            || !string.Equals(task.Triggers[0].Delay, Delay, StringComparison.Ordinal))
        {
            list.Add("Trigger");
        }

        if (task.Actions.Count != 1 || task.Actions[0].Type != ActionExec
            || !SamePath(task.Actions[0].Path, exePath)
            || !string.Equals(task.Actions[0].Arguments, Arguments, StringComparison.Ordinal))
        {
            list.Add("Action");
        }

        if (!string.Equals(task.ExecutionTimeLimit, ExecutionTimeLimit, StringComparison.Ordinal))
        {
            list.Add("ExecutionTimeLimit");
        }

        if (task.MultipleInstances != InstancesIgnoreNew)
        {
            list.Add("MultipleInstances");
        }

        return list;
    }

    // Task Scheduler may hand the query back with other quotes or spacing.
    private static string Normalized(string? xml)
    {
        var sb = new StringBuilder();
        foreach (var c in xml ?? "")
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(c == '"' ? '\'' : c);
            }
        }

        return sb.ToString();
    }

    // Task Scheduler may hand the path back quoted.
    private static bool SamePath(string? a, string b) =>
        a is not null && string.Equals(a.Trim().Trim('"'), b, StringComparison.OrdinalIgnoreCase);
}

public enum AutoPrivateVerdict
{
    // Switched to Private.
    Switch,
    NotPublic,

    // In Tracked.Networks or Tracked.NetworkProfiles: Setup's own choice for it stands.
    Tracked,

    // Left Public at Setup (AutoPrivateSkip).
    LeftPublic,

    // In auto-private.log: CPFS switched it once already.
    Logged,
    Vpn,

    // No NetworkList profile of that name: not a saved network (an unidentified one, say).
    NotSaved,
}

public sealed record AutoPrivateDecision(string Name, string? InterfaceAlias, AutoPrivateVerdict Verdict);

// What the --auto-private verb may switch: only a Public network CPFS has never seen, never one on a VPN adapter, and
// only while config.json says Auto-Private is on. Exclusions compare case-insensitively (broader, so safer); a saved
// profile must match exactly.
public static class AutoPrivateRules
{
    public static bool Enabled(ConfigDocument? config) => config?.AutoPrivate == true;

    public static IReadOnlyList<AutoPrivateDecision> Decide(
        IReadOnlyList<NetworkInfo> profiles,
        ConfigDocument config,
        IReadOnlyList<string> loggedNames,
        IReadOnlyList<string> vpnAdapters,
        IReadOnlyList<string> savedNames)
    {
        if (profiles is null || config is null || loggedNames is null || vpnAdapters is null || savedNames is null)
        {
            throw new ArgumentNullException(profiles is null ? nameof(profiles) : config is null ? nameof(config)
                : loggedNames is null ? nameof(loggedNames) : vpnAdapters is null ? nameof(vpnAdapters) : nameof(savedNames));
        }

        var tracked = new List<string>(config.TrackedNetworks);
        foreach (var n in config.TrackedNetworkProfiles)
        {
            tracked.Add(n.Name);
        }

        var list = new List<AutoPrivateDecision>();
        foreach (var p in profiles)
        {
            var verdict =
                p.Category != 0 ? AutoPrivateVerdict.NotPublic
                : Has(tracked, p.Name) ? AutoPrivateVerdict.Tracked
                : Has(config.AutoPrivateSkip, p.Name) ? AutoPrivateVerdict.LeftPublic
                : Has(loggedNames, p.Name) ? AutoPrivateVerdict.Logged
                : p.InterfaceAlias is null || Has(vpnAdapters, p.InterfaceAlias) ? AutoPrivateVerdict.Vpn
                : !Exact(savedNames, p.Name) ? AutoPrivateVerdict.NotSaved
                : AutoPrivateVerdict.Switch;
            list.Add(new AutoPrivateDecision(p.Name, p.InterfaceAlias, verdict));
        }

        return list;
    }

    // Setup's new skip list: the names left Public before, plus the Public networks left unticked in this run, minus
    // those ticked now (they become Private and are tracked).
    public static IReadOnlyList<string> SkipList(IReadOnlyList<string> previous, Plan plan)
    {
        if (previous is null || plan is null)
        {
            throw new ArgumentNullException(previous is null ? nameof(previous) : nameof(plan));
        }

        var list = new List<string>();
        var ticked = new List<string>();
        foreach (var item in plan.Items)
        {
            if (item.Action == PlanAction.Ask && item.Key.StartsWith("net:", StringComparison.Ordinal) && item.Ticked)
            {
                ticked.Add(item.Key.Substring(4));
            }
        }

        foreach (var name in previous)
        {
            AddName(list, ticked, name);
        }

        foreach (var item in plan.Items)
        {
            if (item.Action == PlanAction.Ask && item.Key.StartsWith("net:", StringComparison.Ordinal) && !item.Ticked)
            {
                AddName(list, ticked, item.Key.Substring(4));
            }
        }

        return list;
    }

    public static bool SameNames(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var name in a)
        {
            if (!Exact(b, name))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddName(List<string> list, List<string> ticked, string name)
    {
        if (name.Trim().Length > 0 && !Exact(list, name) && !Exact(ticked, name))
        {
            list.Add(name);
        }
    }

    private static bool Has(IReadOnlyList<string> list, string value)
    {
        foreach (var item in list)
        {
            if (string.Equals(item?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Exact(IReadOnlyList<string> list, string value)
    {
        foreach (var item in list)
        {
            if (string.Equals(item, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

// auto-private.log: one network name per line. The 0.2.x task wrote it in the ANSI code page, the app writes UTF-8
// with a BOM; a wrong decoding matches no network, so a reader takes the names of both.
public static class AutoPrivateLog
{
    public const string FileName = "auto-private.log";
    public const string RunLogFileName = "auto-private-run.log";
    public const int RunLogLines = 200;

    private static readonly UTF8Encoding Utf8 = new(false);

    public static IReadOnlyList<string> Names(byte[] bytes, Encoding ansi)
    {
        if (bytes is null || ansi is null)
        {
            throw new ArgumentNullException(bytes is null ? nameof(bytes) : nameof(ansi));
        }

        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var names = new List<string>();
        var skip = bom ? 3 : 0;
        foreach (var text in new[] { ansi.GetString(bytes, skip, bytes.Length - skip), Utf8.GetString(bytes, skip, bytes.Length - skip) })
        {
            foreach (var line in text.Split('\n'))
            {
                var name = line.Trim('\r', ' ', '\t', '﻿');
                if (name.Length > 0 && !names.Contains(name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    // The bytes to append for one name: the BOM when the file is new, a line break first when the last line has none.
    public static byte[] Append(byte[]? existing, string name)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        var prefix = existing is null || existing.Length == 0 ? "﻿"
            : existing[existing.Length - 1] == (byte)'\n' ? ""
            : "\r\n";
        return Utf8.GetBytes(prefix + name + "\r\n");
    }

    // The run log after one more line, keeping the last RunLogLines.
    public static string RunLog(string? existing, string line)
    {
        var lines = new List<string>();
        foreach (var l in (existing ?? "").Split('\n'))
        {
            var t = l.TrimEnd('\r').TrimStart('﻿');
            if (t.Length > 0)
            {
                lines.Add(t);
            }
        }

        lines.Add(line);
        var start = Math.Max(0, lines.Count - RunLogLines);
        var sb = new StringBuilder();
        for (var i = start; i < lines.Count; i++)
        {
            sb.Append(lines[i]).Append("\r\n");
        }

        return sb.ToString();
    }
}
