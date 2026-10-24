using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cpfs.Core.Updates;

public enum UpdateStatus
{
    Latest,
    Newer,

    // GitHub answered 404: the repository has no published release yet.
    NoRelease,

    // No answer, an unexpected one, or a release page outside github.com/cyclone9/CPFS.
    Failed,
}

public sealed record UpdateResult(UpdateStatus Status, string? Version = null, string? ReleaseUrl = null);

// The update check: one GET of GitHub's public "latest release" for cyclone9/CPFS, and what its answer means. It
// only ever offers to open the release page; nothing is downloaded or installed.
public static class UpdateCheck
{
    public const string ApiUrl = "https://api.github.com/repos/cyclone9/CPFS/releases/latest";
    public const string Accept = "application/vnd.github+json";
    public const string ReleasePagePrefix = "https://github.com/cyclone9/CPFS/";

    public static string UserAgent(string runningVersion) => "CPFS/" + runningVersion;

    // The HTTP status and body (null when there was no answer), against the running version.
    public static UpdateResult FromResponse(int? status, string? body, string runningVersion)
    {
        if (status == 404)
        {
            return new UpdateResult(UpdateStatus.NoRelease);
        }

        if (status != 200 || body is null || !SemVersion.TryParse(runningVersion, out var running))
        {
            return new UpdateResult(UpdateStatus.Failed);
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return new UpdateResult(UpdateStatus.Failed);
        }

        var tag = Text(root?["tag_name"]);
        var url = Text(root?["html_url"]);
        if (tag is null || !SemVersion.TryParse(tag, out var latest) || url is null || !IsReleasePage(url))
        {
            return new UpdateResult(UpdateStatus.Failed);
        }

        return latest.CompareTo(running) > 0
            ? new UpdateResult(UpdateStatus.Newer, latest.ToString(), url)
            : new UpdateResult(UpdateStatus.Latest, latest.ToString());
    }

    // Only a page of this repository on github.com is ever opened.
    public static bool IsReleasePage(string url) =>
        url.StartsWith(ReleasePagePrefix, StringComparison.Ordinal)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && uri.UserInfo.Length == 0
        && uri.IsDefaultPort;

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}

// MAJOR.MINOR.PATCH with an optional pre-release ("0.6.0-dev5", "v1.0.0-beta.2"); build metadata after "+" is
// ignored. Ordered by semantic versioning: a pre-release comes before its release. Within a pre-release, an
// identifier of letters followed by digits ("dev5", "dev10") compares by its number, so dev10 follows dev5.
public readonly struct SemVersion
{
    private readonly string[] _pre;

    private SemVersion(int major, int minor, int patch, string[] pre)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _pre = pre;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public static bool TryParse(string? text, out SemVersion version)
    {
        version = default;
        var s = text?.Trim() ?? "";
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(1);
        }

        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            s = s.Substring(0, plus);
        }

        var dash = s.IndexOf('-');
        var core = dash < 0 ? s : s.Substring(0, dash);
        var pre = dash < 0 ? Array.Empty<string>() : s.Substring(dash + 1).Split('.');
        var parts = core.Split('.');
        if (parts.Length != 3 || !Number(parts[0], out var major) || !Number(parts[1], out var minor) || !Number(parts[2], out var patch))
        {
            return false;
        }

        foreach (var id in pre)
        {
            if (id.Length == 0 || !IsIdentifier(id))
            {
                return false;
            }
        }

        version = new SemVersion(major, minor, patch, pre);
        return true;
    }

    public int CompareTo(SemVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0)
        {
            c = Minor.CompareTo(other.Minor);
        }

        if (c == 0)
        {
            c = Patch.CompareTo(other.Patch);
        }

        if (c != 0)
        {
            return c;
        }

        var a = _pre ?? Array.Empty<string>();
        var b = other._pre ?? Array.Empty<string>();
        if (a.Length == 0 || b.Length == 0)
        {
            return b.Length.CompareTo(a.Length);
        }

        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            c = CompareIdentifier(a[i], b[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    public override string ToString() =>
        Major.ToString(CultureInfo.InvariantCulture) + "." + Minor.ToString(CultureInfo.InvariantCulture) + "." + Patch.ToString(CultureInfo.InvariantCulture)
        + ((_pre ?? Array.Empty<string>()).Length == 0 ? "" : "-" + string.Join(".", _pre!));

    private static int CompareIdentifier(string a, string b)
    {
        var an = Number(a, out var ai);
        var bn = Number(b, out var bi);
        if (an && bn)
        {
            return ai.CompareTo(bi);
        }

        if (an != bn)
        {
            return an ? -1 : 1;
        }

        SplitDigits(a, out var aPrefix, out var aNumber);
        SplitDigits(b, out var bPrefix, out var bNumber);
        if (aNumber is not null && bNumber is not null && string.Equals(aPrefix, bPrefix, StringComparison.Ordinal))
        {
            return aNumber.Value.CompareTo(bNumber.Value);
        }

        return string.CompareOrdinal(a, b);
    }

    private static void SplitDigits(string id, out string prefix, out long? number)
    {
        var i = id.Length;
        while (i > 0 && id[i - 1] is >= '0' and <= '9')
        {
            i--;
        }

        prefix = id.Substring(0, i);
        number = i < id.Length && i > 0 && long.TryParse(id.Substring(i), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static bool Number(string text, out int value)
    {
        value = 0;
        return text.Length > 0 && text.Length <= 9 && (text == "0" || text[0] != '0')
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsIdentifier(string id)
    {
        foreach (var c in id)
        {
            if (!(c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
