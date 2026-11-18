using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cpfs.Core.Updates;

// A release CPFS may install: every field is the app's own or checked, the URLs are built from the version.
public sealed record UpdateOffer(
    string Version,
    string ExeName,
    string ShaName,
    string ExeUrl,
    string ShaUrl,
    long ExeSize,
    string? ExeDigest,
    string Notes,
    string ReleaseUrl);

// Why a newer release is announced but not offered for installing, or why a download is refused. Logged by name.
public enum UpdateRefusal
{
    None,
    NotJson,
    BadRunningVersion,
    TagNotPlain,
    NotGreater,
    Draft,
    Prerelease,
    BadReleasePage,
    ExeAssetMissing,
    ShaAssetMissing,
    BadSize,
    TooLarge,
    BadDigest,
    ChecksumFormat,
    ChecksumMismatch,
    SizeMismatch,
    DigestMismatch,
    ProductVersionMismatch,
    ProbeFailed,
}

// Where the running exe is, as far as an in-place update is concerned (updater design, section 3).
public enum UpdatePlace
{
    // A local folder: download, check, stage beside the exe, swap.
    InPlace,

    // A network drive or a UNC path: the verified file goes where the user saves it.
    SaveAs,

    // The background copy in Program Files\CPFS: never updated here.
    BackgroundCopy,

    // An administrator window: no update at all (U2).
    Elevated,
}

// What the old process does after starting the new exe (step 9).
public enum RestartOutcome
{
    // The new window said it is up.
    Started,

    // The new exe could not be started, or ended with a non-zero code before it said so: roll back.
    RollBack,

    // It ended with code 0 (another start took over) or did not answer in time (a slow PC): keep it.
    Keep,
}

// The updater's rules without Windows calls: the strict release rules, URLs built from the version, the redirect rule,
// the checks on the downloaded bytes, the notes clean-up, where the exe may be updated, the rollback decision, and the
// names CPFS uses beside its exe. Contract for later releases: the asset names, the .sha256 line, the probe verb and its
// code, and --after-update stay as they are.
public static class UpdateRules
{
    public const string DownloadPrefix = "https://github.com/cyclone9/CPFS/releases/download/v";

    public const long MaxExeBytes = 64L * 1024 * 1024;
    public const int MaxShaBytes = 1024;
    public const int MaxNotesChars = 8000;
    public const int MaxRedirects = 3;

    public const int HeadersTimeoutMs = 15000;
    public const int ReadTimeoutMs = 30000;
    public static readonly TimeSpan TotalLimit = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ProbeLimit = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StartedLimit = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan TrayStopLimit = TimeSpan.FromSeconds(3);

    // "Cpfs.exe --update-probe X.Y.Z": loads everything the window needs, compares its own version, opens nothing.
    public const string ProbeVerb = "--update-probe";

    // Not 0 or 1, so an exe that ignores the verb (or crashes) can never pass for one that ran the probe.
    public const int ProbeOk = 0x43; // 67
    public const int ProbeVersionMismatch = 0x44;
    public const int ProbeFailed = 0x45;

    public const string AfterUpdateOption = "--after-update";
    public const string UpdateFailedOption = "--update-failed";

    // Set by the new window once it has rendered; no data. Both ends are unelevated processes of the same user.
    public const string StartedEventName = @"Local\CPFS-CyClonePowerFileShare-Updated";

    public const string OldSuffix = ".cpfs-old";
    public const string FailedSuffix = ".cpfs-failed";
    public const string StagedSuffix = ".cpfs-update.exe";

    private static readonly Regex PlainTag = new("^v(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionArg = new("^[0-9A-Za-z.-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new("^sha256:([0-9a-f]{64})$", RegexOptions.CultureInvariant);

    public static string ExeName(string version) => "Cpfs-" + version + ".exe";

    public static string ShaName(string version) => ExeName(version) + ".sha256";

    public static string ExeUrl(string version) => DownloadPrefix + version + "/" + ExeName(version);

    public static string ShaUrl(string version) => DownloadPrefix + version + "/" + ShaName(version);

    // The release answer (already accepted by UpdateCheck as Newer), against the running version: an offer, or why not.
    public static UpdateOffer? ReadOffer(string? body, string runningVersion, out UpdateRefusal refusal)
    {
        refusal = UpdateRefusal.None;
        if (!SemVersion.TryParse(runningVersion, out var running))
        {
            refusal = UpdateRefusal.BadRunningVersion;
            return null;
        }

        JsonObject? root;
        try
        {
            root = body is null ? null : JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is null)
        {
            refusal = UpdateRefusal.NotJson;
            return null;
        }

        var tag = Text(root["tag_name"]);
        if (tag is null || !PlainTag.IsMatch(tag) || !SemVersion.TryParse(tag, out var latest))
        {
            refusal = UpdateRefusal.TagNotPlain;
            return null;
        }

        if (latest.CompareTo(running) <= 0)
        {
            refusal = UpdateRefusal.NotGreater;
            return null;
        }

        // Both must be present and false: a field GitHub stopped sending is no reason to guess.
        if (Flag(root["draft"]) != false)
        {
            refusal = UpdateRefusal.Draft;
            return null;
        }

        if (Flag(root["prerelease"]) != false)
        {
            refusal = UpdateRefusal.Prerelease;
            return null;
        }

        var page = Text(root["html_url"]);
        if (page is null || !UpdateCheck.IsReleasePage(page))
        {
            refusal = UpdateRefusal.BadReleasePage;
            return null;
        }

        var version = latest.ToString();
        var exe = Asset(root, ExeName(version));
        if (exe is null)
        {
            refusal = UpdateRefusal.ExeAssetMissing;
            return null;
        }

        if (Asset(root, ShaName(version)) is null)
        {
            refusal = UpdateRefusal.ShaAssetMissing;
            return null;
        }

        if (Number(exe["size"]) is not { } size || size <= 0)
        {
            refusal = UpdateRefusal.BadSize;
            return null;
        }

        if (size > MaxExeBytes)
        {
            refusal = UpdateRefusal.TooLarge;
            return null;
        }

        string? digest = null;
        var digestNode = exe["digest"];
        if (digestNode is not null && digestNode.GetValueKind() != JsonValueKind.Null)
        {
            var match = Text(digestNode) is { } d ? DigestPattern.Match(d) : Match.Empty;
            if (!match.Success)
            {
                refusal = UpdateRefusal.BadDigest;
                return null;
            }

            digest = match.Groups[1].Value;
        }

        return new UpdateOffer(version, ExeName(version), ShaName(version), ExeUrl(version), ShaUrl(version), size, digest,
            CleanNotes(Text(root["body"])), page);
    }

    // The one line release-check.sh writes and checks: "<64 lower-case hex>  Cpfs-X.Y.Z.exe", a final newline allowed.
    public static string? ParseChecksum(byte[] body, string version)
    {
        if (body is null || body.Length == 0 || body.Length > MaxShaBytes)
        {
            return null;
        }

        foreach (var b in body)
        {
            if (b > 0x7E || (b < 0x20 && b != (byte)'\n'))
            {
                return null;
            }
        }

        var text = Encoding.ASCII.GetString(body);
        var pattern = "^([0-9a-f]{64})  " + Regex.Escape(ExeName(version)) + "\n?$";
        var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);

        // $ also matches before a final "\n"; a second "\n" fails the length check below.
        return match.Success && match.Length == text.Length ? match.Groups[1].Value : null;
    }

    public static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(bytes));
    }

    public static string Hex(byte[] hash)
    {
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    // Step 5: every check on the downloaded bytes. Returns None when all hold; computed is the bytes' SHA-256.
    public static UpdateRefusal Verify(UpdateOffer offer, byte[] exe, byte[] sha, out string computed, out string? published)
    {
        computed = Sha256Hex(exe ?? Array.Empty<byte>());
        published = ParseChecksum(sha, offer.Version);
        if (published is null)
        {
            return UpdateRefusal.ChecksumFormat;
        }

        if (!string.Equals(computed, published, StringComparison.Ordinal))
        {
            return UpdateRefusal.ChecksumMismatch;
        }

        if (exe!.LongLength != offer.ExeSize)
        {
            return UpdateRefusal.SizeMismatch;
        }

        if (offer.ExeDigest is not null && !string.Equals(offer.ExeDigest, computed, StringComparison.Ordinal))
        {
            return UpdateRefusal.DigestMismatch;
        }

        return UpdateRefusal.None;
    }

    // The staged file's ProductVersion, without the SDK's "+<commit>", must be the release's version exactly.
    public static bool ProductVersionMatches(string? productVersion, string version) =>
        productVersion is not null && string.Equals(Cpfs.Core.Text.VersionText.Number(productVersion), version, StringComparison.Ordinal);

    // The probe compares its own version with the argument the same way.
    public static int ProbeCode(string? ownInformationalVersion, string? expected) =>
        expected is not null && ProductVersionMatches(ownInformationalVersion, expected) ? ProbeOk : ProbeVersionMismatch;

    // Takes "--after-update <version>" and "--update-failed <version>" out of a command line; a value that is not a
    // version is dropped. The rest goes on to RelaunchArgs.
    public static string[] TakeRestartOptions(IReadOnlyList<string> args, out string? afterUpdate, out string? updateFailed)
    {
        afterUpdate = null;
        updateFailed = null;
        var rest = new List<string>();
        for (var i = 0; i < (args?.Count ?? 0); i++)
        {
            var option = args![i];
            var after = string.Equals(option, AfterUpdateOption, StringComparison.Ordinal);
            if (!after && !string.Equals(option, UpdateFailedOption, StringComparison.Ordinal))
            {
                rest.Add(option);
                continue;
            }

            var value = i + 1 < args.Count ? args[++i] : null;
            var ok = value is not null && value.Length <= 64 && VersionArg.IsMatch(value) && SemVersion.TryParse(value, out _);
            if (after)
            {
                afterUpdate = ok ? value : null;
            }
            else
            {
                updateFailed = ok ? value : null;
            }
        }

        return rest.ToArray();
    }

    // A download hop: https on the default port, no user info, github.com or a host under githubusercontent.com.
    public static bool IsAllowedHop(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
        {
            return false;
        }

        var host = uri.Host;
        return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
            || (host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase) && host.Length > ".githubusercontent.com".Length
                && host[host.Length - ".githubusercontent.com".Length - 1] != '.');
    }

    // A Location header, resolved against the URL that sent it; null when it is not a usable absolute URL.
    public static Uri? NextHop(Uri current, string? location)
    {
        if (current is null || string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        return Uri.TryCreate(current, location!.Trim(), out var next) ? next : null;
    }

    // Plain text for a read-only box: line breaks kept, tabs as spaces, other control and direction-override characters
    // dropped, at most MaxNotesChars characters (never half a surrogate pair). Markdown stays as typed, links are text.
    public static string CleanNotes(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return "";
        }

        var sb = new StringBuilder(Math.Min(body!.Length, MaxNotesChars));
        for (var i = 0; i < body.Length && sb.Length < MaxNotesChars; i++)
        {
            var c = body[i];
            if (c == '\r')
            {
                if (i + 1 < body.Length && body[i + 1] == '\n')
                {
                    continue;
                }

                c = '\n';
            }

            if (c == '\t')
            {
                c = ' ';
            }

            if ((char.IsControl(c) && c != '\n') || c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '‎' or '‏' or '؜')
            {
                continue;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= body.Length || !char.IsLowSurrogate(body[i + 1]))
                {
                    continue;
                }

                if (sb.Length + 2 > MaxNotesChars)
                {
                    break;
                }

                sb.Append(c).Append(body[++i]);
                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    // Section 3. exePath is the running exe; driveIsNetwork is DriveInfo's answer for its drive (false when unknown).
    public static UpdatePlace Place(string exePath, bool elevated, bool driveIsNetwork, string backgroundCopyPath)
    {
        if (elevated)
        {
            return UpdatePlace.Elevated;
        }

        if (SamePath(exePath, backgroundCopyPath))
        {
            return UpdatePlace.BackgroundCopy;
        }

        if (exePath.StartsWith(@"\\", StringComparison.Ordinal) || exePath.StartsWith("//", StringComparison.Ordinal) || driveIsNetwork)
        {
            return UpdatePlace.SaveAs;
        }

        return UpdatePlace.InPlace;
    }

    // Step 9. exitCode is null while the new process still runs (or when that could not be told).
    public static RestartOutcome AfterStart(bool startFailed, bool startedSignal, int? exitCode)
    {
        if (startFailed)
        {
            return RestartOutcome.RollBack;
        }

        if (startedSignal)
        {
            return RestartOutcome.Started;
        }

        return exitCode is { } code && code != 0 ? RestartOutcome.RollBack : RestartOutcome.Keep;
    }

    // <exe>.cpfs-old: the previous exe, kept until the new one has shown its window.
    public static string OldPath(string exePath) => exePath + OldSuffix;

    // <exe>.cpfs-failed: a new exe that did not start, set aside by the rollback.
    public static string FailedPath(string exePath) => exePath + FailedSuffix;

    // <folder>\<stem>.cpfs-update.exe: the verified download, waiting for its probe.
    public static string StagedPath(string exePath)
    {
        var cut = Math.Max(exePath.LastIndexOf('\\'), exePath.LastIndexOf('/'));
        var name = exePath.Substring(cut + 1);
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name.Substring(0, dot) : name;
        return exePath.Substring(0, cut + 1) + stem + StagedSuffix;
    }

    // The only files the clean-up at start may delete, for the exe at exePath.
    public static IReadOnlyList<string> LeftoverPaths(string exePath) => new[] { OldPath(exePath), FailedPath(exePath), StagedPath(exePath) };

    // The copy in Program Files\CPFS should not be replaced by an older exe without asking (section 4).
    public static bool IsDowngrade(string? copyProductVersion, string runningVersion) =>
        SemVersion.TryParse(Cpfs.Core.Text.VersionText.Number(copyProductVersion), out var copy) && SemVersion.TryParse(runningVersion, out var running)
        && copy.CompareTo(running) > 0;

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('/', '\\').TrimEnd('\\'), b.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static JsonObject? Asset(JsonObject root, string name)
    {
        if (root["assets"] is not JsonArray assets)
        {
            return null;
        }

        JsonObject? found = null;
        foreach (var node in assets)
        {
            if (node is JsonObject asset && string.Equals(Text(asset["name"]), name, StringComparison.Ordinal))
            {
                if (found is not null)
                {
                    // Two assets of one name: GitHub doesn't allow it, so the answer is not GitHub's.
                    return null;
                }

                found = asset;
            }
        }

        return found;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static bool? Flag(JsonNode? node) => node is JsonValue value ? value.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    }
    : null;

    private static long? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<long>(out var n) ? n : null;
}
