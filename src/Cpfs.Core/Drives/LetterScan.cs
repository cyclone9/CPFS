using System;
using System.Collections.Generic;

namespace Cpfs.Core.Drives;

// Where a taken letter was seen. The order is the order shown to the user, most specific first.
public enum LetterSource
{
    SystemDrive,

    // GetLogicalDrives in this process. Unelevated, it includes the user's own session letters.
    ThisProcess,

    // Passed on the command line by the unelevated instance (the user's session as that instance saw it).
    UserSessionReceived,

    // Persistent network drives: HKCU\Network (unelevated) or HKU\<desktop SID>\Network (elevated).
    UserNetwork,

    // HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\DOS Devices.
    DosDevices,

    // HKLM\SYSTEM\MountedDevices.
    MountedDevices,
}

public sealed record LetterUse(LetterSource Source, string? Detail);

public sealed class LetterScan
{
    private readonly SortedDictionary<char, List<LetterUse>> _taken = new();

    public IReadOnlyDictionary<char, List<LetterUse>> Taken => _taken;

    // Sources that could not be read, with the reason, so the page can say a letter may be offered although in use.
    public IList<KeyValuePair<LetterSource, string>> Failed { get; } = new List<KeyValuePair<LetterSource, string>>();

    public void Add(char letter, LetterSource source, string? detail = null)
    {
        letter = char.ToUpperInvariant(letter);
        if (letter is < 'A' or > 'Z')
        {
            return;
        }

        if (!_taken.TryGetValue(letter, out var uses))
        {
            uses = new List<LetterUse>();
            _taken[letter] = uses;
        }

        foreach (var u in uses)
        {
            if (u.Source == source && string.Equals(u.Detail, detail, StringComparison.Ordinal))
            {
                return;
            }
        }

        uses.Add(new LetterUse(source, detail));
        uses.Sort((a, b) => a.Source.CompareTo(b.Source));
    }

    public void AddAll(string letters, LetterSource source)
    {
        foreach (var c in letters ?? "")
        {
            Add(c, source);
        }
    }

    // A and B are never offered. ownLetter is the letter CPFS itself mapped (config DriveLetter): it counts as free
    // for CPFS, as in the scripts, unless the system drive is on it.
    public List<char> Free(char? ownLetter = null)
    {
        var own = ownLetter is { } o ? char.ToUpperInvariant(o) : '\0';
        var free = new List<char>();
        for (var c = 'C'; c <= 'Z'; c++)
        {
            if (!_taken.TryGetValue(c, out var uses))
            {
                free.Add(c);
                continue;
            }

            if (c == own && !uses.Exists(u => u.Source == LetterSource.SystemDrive))
            {
                free.Add(c);
            }
        }

        return free;
    }

    // Whether everything seen on a letter is CPFS's own mapping (one of ownTargets, "\??\D:\Folder"), so CPFS may
    // map it again. A volume, a network drive or another mapping on it is someone else's. What this process or the
    // user's session sees counts as CPFS's only when the DOS Devices value or the target this process reads is CPFS's.
    public bool IsHeldOnlyBy(char letter, IReadOnlyCollection<string> ownTargets)
    {
        if (!_taken.TryGetValue(char.ToUpperInvariant(letter), out var uses))
        {
            return true;
        }

        bool Own(string? target)
        {
            if (target is null)
            {
                return false;
            }

            foreach (var t in ownTargets)
            {
                if (string.Equals(t, target, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        var mapped = uses.Exists(u => u.Source is LetterSource.DosDevices or LetterSource.ThisProcess && Own(u.Detail));
        foreach (var use in uses)
        {
            var ours = use.Source switch
            {
                LetterSource.DosDevices or LetterSource.ThisProcess => Own(use.Detail),
                LetterSource.UserSessionReceived => mapped,
                _ => false,
            };
            if (!ours)
            {
                return false;
            }
        }

        return true;
    }

    // The previous letter if still free, then X if free, otherwise the last free letter; null when none is free.
    public char? DefaultLetter(char? ownLetter = null)
    {
        var free = Free(ownLetter);
        if (free.Count == 0)
        {
            return null;
        }

        if (ownLetter is { } own && free.IndexOf(char.ToUpperInvariant(own)) >= 0)
        {
            return char.ToUpperInvariant(own);
        }

        return free.IndexOf('X') >= 0 ? 'X' : free[free.Count - 1];
    }
}
