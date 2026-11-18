using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Cpfs.Core.Apply;
using Cpfs.Core.Planning;
using Cpfs.Core.Updates;
using Xunit;

namespace Cpfs.Tests;

// The updater's rules (updater design, sections 2-4 and 11). The fixture is GitHub's real answer for 0.8.0, trimmed,
// with the repository written as {REPO} so no account name sits in the tests.
public class UpdaterTests
{
    private static readonly string Repo = UpdateCheck.ReleasePagePrefix.Substring("https://github.com/".Length).TrimEnd('/');

    private const string RealHash = "78fe59ade10f30b298f57f941a6cb341eb1ba671e4992f162e045cfe54c79624";

    private static string Real() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "update", "latest-0.8.0.json")).Replace("{REPO}", Repo, StringComparison.Ordinal);

    // The real answer moved to another version, with an exe of the given bytes.
    private static string Release(string version, byte[] exe, Action<JsonObject>? change = null)
    {
        var root = JsonNode.Parse(Real())!.AsObject();
        root["tag_name"] = "v" + version;
        root["html_url"] = UpdateCheck.ReleasePagePrefix + "releases/tag/v" + version;
        var assets = root["assets"]!.AsArray();
        assets[0]!["name"] = "Cpfs-" + version + ".exe";
        assets[0]!["size"] = exe.Length;
        assets[0]!["digest"] = "sha256:" + UpdateRules.Sha256Hex(exe);
        assets[1]!["name"] = "Cpfs-" + version + ".exe.sha256";
        change?.Invoke(root);
        return root.ToJsonString();
    }

    private static readonly byte[] Exe = Encoding.ASCII.GetBytes("MZ pretend exe");

    private static byte[] Sha(byte[] exe, string version) => Encoding.ASCII.GetBytes(UpdateRules.Sha256Hex(exe) + "  Cpfs-" + version + ".exe\n");

    [Fact]
    public void TheReal080AnswerIsAnOfferForAnOlderRunningVersion()
    {
        var offer = UpdateRules.ReadOffer(Real(), "0.7.0", out var refusal);
        Assert.Equal(UpdateRefusal.None, refusal);
        Assert.NotNull(offer);
        Assert.Equal("0.8.0", offer!.Version);
        Assert.Equal(10072576, offer.ExeSize);
        Assert.Equal(RealHash, offer.ExeDigest);
        Assert.Equal("https://github.com/" + Repo + "/releases/download/v0.8.0/Cpfs-0.8.0.exe", offer.ExeUrl);
        Assert.Equal(offer.ExeUrl + ".sha256", offer.ShaUrl);
        Assert.StartsWith("First public release.", offer.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void UrlsComeFromTheVersionNotFromTheAnswer()
    {
        var json = Release("0.9.1", Exe, r => r["assets"]![0]!["browser_download_url"] = "https://evil.example/x.exe");
        var offer = UpdateRules.ReadOffer(json, "0.9.0", out _);
        Assert.Equal(UpdateRules.ExeUrl("0.9.1"), offer!.ExeUrl);
        Assert.StartsWith(UpdateRules.DownloadPrefix, offer.ExeUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.9.0-dev6", "0.9.0", true)]
    [InlineData("0.9.0-dev10", "0.9.0", true)]
    [InlineData("0.9.0", "0.9.1", true)]
    [InlineData("0.9.0", "0.9.0", false)]
    [InlineData("0.9.1", "0.9.0", false)]
    [InlineData("1.0.0", "0.10.0", false)]
    [InlineData("1.0.0-dev7", "1.0.0", true)]
    [InlineData("0.9.0-dev6", "1.0.0", true)]
    [InlineData("0.9.0", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "0.9.0", false)]
    public void OnlyAStrictlyGreaterReleaseIsOffered(string running, string release, bool offered)
    {
        var offer = UpdateRules.ReadOffer(Release(release, Exe), running, out var refusal);
        Assert.Equal(offered, offer is not null);
        Assert.Equal(offered ? UpdateRefusal.None : UpdateRefusal.NotGreater, refusal);
    }

    [Theory]
    [InlineData("v0.9.1-rc1")]
    [InlineData("0.9.1")]
    [InlineData("v0.9.1+abc")]
    [InlineData("v00.9.1")]
    [InlineData("v0.9")]
    [InlineData("V0.9.1")]
    [InlineData("v0.9.1 ")]
    public void TheTagMustBeAPlainVersion(string tag)
    {
        var json = Release("0.9.1", Exe, r => r["tag_name"] = tag);
        Assert.Null(UpdateRules.ReadOffer(json, "0.9.0", out var refusal));
        Assert.Equal(UpdateRefusal.TagNotPlain, refusal);
    }

    [Theory]
    [InlineData("draft", true, UpdateRefusal.Draft)]
    [InlineData("prerelease", true, UpdateRefusal.Prerelease)]
    [InlineData("draft", null, UpdateRefusal.Draft)]
    [InlineData("prerelease", null, UpdateRefusal.Prerelease)]
    public void DraftsAndPrereleasesAreRefused(string field, bool? value, UpdateRefusal expected)
    {
        var json = Release("0.9.1", Exe, r =>
        {
            if (value is null)
            {
                r.Remove(field);
            }
            else
            {
                r[field] = value;
            }
        });
        Assert.Null(UpdateRules.ReadOffer(json, "0.9.0", out var refusal));
        Assert.Equal(expected, refusal);
    }

    [Fact]
    public void AMissingAssetIsRefused()
    {
        Assert.Equal(UpdateRefusal.ShaAssetMissing, Refusal(Release("0.9.1", Exe, r => r["assets"]!.AsArray().RemoveAt(1))));
        Assert.Equal(UpdateRefusal.ExeAssetMissing, Refusal(Release("0.9.1", Exe, r => r["assets"]![0]!["name"] = "Cpfs.exe")));
    }

    [Fact]
    public void TwoAssetsOfOneNameAreRefused()
    {
        var json = Release("0.9.1", Exe, r => r["assets"]!.AsArray().Add(r["assets"]![0]!.DeepClone()));
        Assert.Equal(UpdateRefusal.ExeAssetMissing, Refusal(json));
    }

    [Fact]
    public void SizeOverTheCapOrMissingIsRefused()
    {
        Assert.Equal(UpdateRefusal.TooLarge, Refusal(Release("0.9.1", Exe, r => r["assets"]![0]!["size"] = UpdateRules.MaxExeBytes + 1)));
        Assert.Equal(UpdateRefusal.BadSize, Refusal(Release("0.9.1", Exe, r => r["assets"]![0]!.AsObject().Remove("size"))));
        Assert.Equal(UpdateRefusal.BadSize, Refusal(Release("0.9.1", Exe, r => r["assets"]![0]!["size"] = "10")));
    }

    [Fact]
    public void AMalformedDigestIsRefusedAndAMissingOneIsAllowed()
    {
        Assert.Equal(UpdateRefusal.BadDigest, Refusal(Release("0.9.1", Exe, r => r["assets"]![0]!["digest"] = "sha512:00")));
        Assert.Equal(UpdateRefusal.BadDigest, Refusal(Release("0.9.1", Exe, r => r["assets"]![0]!["digest"] = "sha256:" + new string('A', 64))));
        var offer = UpdateRules.ReadOffer(Release("0.9.1", Exe, r => r["assets"]![0]!.AsObject().Remove("digest")), "0.9.0", out _);
        Assert.Null(offer!.ExeDigest);
    }

    [Fact]
    public void AReleasePageElsewhereIsRefused()
    {
        Assert.Equal(UpdateRefusal.BadReleasePage, Refusal(Release("0.9.1", Exe, r => r["html_url"] = "https://github.com/someone/else/releases/tag/v0.9.1")));
    }

    [Fact]
    public void TheCheckAttachesTheOfferOnlyToANewerRelease()
    {
        var newer = UpdateCheck.FromResponse(200, Release("0.9.1", Exe), "0.9.0");
        Assert.Equal(UpdateStatus.Newer, newer.Status);
        Assert.NotNull(newer.Offer);

        var announcedOnly = UpdateCheck.FromResponse(200, Release("0.9.1", Exe, r => r["assets"] = new JsonArray()), "0.9.0");
        Assert.Equal(UpdateStatus.Newer, announcedOnly.Status);
        Assert.Null(announcedOnly.Offer);
        Assert.Equal(UpdateRefusal.ExeAssetMissing, announcedOnly.Refusal);

        Assert.Null(UpdateCheck.FromResponse(200, Real(), "0.9.0-dev5").Offer);
    }

    [Theory]
    [InlineData("{0}  Cpfs-0.9.1.exe\n", true)]
    [InlineData("{0}  Cpfs-0.9.1.exe", true)]
    [InlineData("{0}  Cpfs-0.9.1.exe\r\n", false)]
    [InlineData("{0}  Cpfs-0.9.1.exe\n\n", false)]
    [InlineData("{0} *Cpfs-0.9.1.exe\n", false)]
    [InlineData("{0} Cpfs-0.9.1.exe\n", false)]
    [InlineData("{0}  Cpfs-0.9.2.exe\n", false)]
    [InlineData("{0}  Cpfs-0.9.1.exe.sha256\n", false)]
    [InlineData(" {0}  Cpfs-0.9.1.exe\n", false)]
    public void TheChecksumIsTheOneLineReleaseCheckWrites(string format, bool ok)
    {
        var hash = UpdateRules.Sha256Hex(Exe);
        var parsed = UpdateRules.ParseChecksum(Encoding.ASCII.GetBytes(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, hash)), "0.9.1");
        Assert.Equal(ok ? hash : null, parsed);
    }

    [Fact]
    public void AnUpperCaseOrOverlongChecksumIsRefused()
    {
        var hash = UpdateRules.Sha256Hex(Exe);
        Assert.Null(UpdateRules.ParseChecksum(Encoding.ASCII.GetBytes(hash.ToUpperInvariant() + "  Cpfs-0.9.1.exe\n"), "0.9.1"));
        Assert.Null(UpdateRules.ParseChecksum(new byte[UpdateRules.MaxShaBytes + 1], "0.9.1"));
        Assert.Null(UpdateRules.ParseChecksum(Array.Empty<byte>(), "0.9.1"));
    }

    [Fact]
    public void The080ChecksumFileShapeParses()
    {
        // 81 bytes: 64 hex, two spaces, the 14-character name, a newline.
        var file = Encoding.ASCII.GetBytes(RealHash + "  Cpfs-0.8.0.exe\n");
        Assert.Equal(81, file.Length);
        Assert.Equal(RealHash, UpdateRules.ParseChecksum(file, "0.8.0"));
    }

    [Fact]
    public void VerifyPassesOnlyWhenHashSizeAndDigestAgree()
    {
        var offer = UpdateRules.ReadOffer(Release("0.9.1", Exe), "0.9.0", out _)!;
        Assert.Equal(UpdateRefusal.None, UpdateRules.Verify(offer, Exe, Sha(Exe, "0.9.1"), out var computed, out var published));
        Assert.Equal(computed, published);

        var other = Encoding.ASCII.GetBytes("MZ another exe");
        Assert.Equal(UpdateRefusal.ChecksumMismatch, UpdateRules.Verify(offer, other, Sha(Exe, "0.9.1"), out _, out _));
        Assert.Equal(UpdateRefusal.ChecksumFormat, UpdateRules.Verify(offer, Exe, Encoding.ASCII.GetBytes("nonsense"), out _, out _));
        Assert.Equal(UpdateRefusal.SizeMismatch, UpdateRules.Verify(offer with { ExeSize = Exe.Length + 1 }, Exe, Sha(Exe, "0.9.1"), out _, out _));
        Assert.Equal(UpdateRefusal.DigestMismatch, UpdateRules.Verify(offer with { ExeDigest = new string('0', 64) }, Exe, Sha(Exe, "0.9.1"), out _, out _));
        Assert.Equal(UpdateRefusal.None, UpdateRules.Verify(offer with { ExeDigest = null }, Exe, Sha(Exe, "0.9.1"), out _, out _));
    }

    [Theory]
    [InlineData("0.9.1", "0.9.1", true)]
    [InlineData("0.9.1+ecb7346abc", "0.9.1", true)]
    [InlineData("0.9.1-dev1", "0.9.1", false)]
    [InlineData("99.0.0", "0.9.1", false)]
    [InlineData(null, "0.9.1", false)]
    [InlineData("", "0.9.1", false)]
    public void TheFileVersionMustBeTheTag(string? product, string version, bool match)
    {
        Assert.Equal(match, UpdateRules.ProductVersionMatches(product, version));
        Assert.Equal(match ? UpdateRules.ProbeOk : UpdateRules.ProbeVersionMismatch, UpdateRules.ProbeCode(product, version));
    }

    [Fact]
    public void TheProbeCodesAreNeitherZeroNorOne()
    {
        Assert.DoesNotContain(UpdateRules.ProbeOk, new[] { 0, 1, UpdateRules.ProbeVersionMismatch, UpdateRules.ProbeFailed });
        Assert.Equal(UpdateRules.ProbeVersionMismatch, UpdateRules.ProbeCode("0.9.1", null));
    }

    [Theory]
    [InlineData("https://github.com/x/y", true)]
    [InlineData("https://release-assets.githubusercontent.com/a?sig=1", true)]
    [InlineData("https://objects.githubusercontent.com/a", true)]
    [InlineData("https://GITHUB.com/x", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://github.com:8443/x", false)]
    [InlineData("https://user:pw@github.com/x", false)]
    [InlineData("https://githubusercontent.com/x", false)]
    [InlineData("https://.githubusercontent.com/x", false)]
    [InlineData("https://evilgithubusercontent.com/x", false)]
    [InlineData("https://githubusercontent.com.evil.example/x", false)]
    [InlineData("https://api.github.com/x", false)]
    [InlineData("https://github.com.evil.example/x", false)]
    [InlineData("ftp://github.com/x", false)]
    public void RedirectsStayOnGitHub(string url, bool allowed)
    {
        Uri.TryCreate(url, UriKind.Absolute, out var uri);
        Assert.Equal(allowed, UpdateRules.IsAllowedHop(uri));
    }

    [Fact]
    public void ARelativeLocationIsResolvedAndAnEmptyOneIsNot()
    {
        var from = new Uri("https://github.com/a/b/releases/download/v1.0.0/Cpfs-1.0.0.exe");
        Assert.Equal("https://github.com/elsewhere", UpdateRules.NextHop(from, "/elsewhere")!.ToString());
        Assert.Null(UpdateRules.NextHop(from, null));
        Assert.Null(UpdateRules.NextHop(from, "  "));
        Assert.False(UpdateRules.IsAllowedHop(UpdateRules.NextHop(from, "http://github.com/x")));
    }

    [Fact]
    public void NotesArePlainCappedText()
    {
        Assert.Equal("a\nb\nc d", UpdateRules.CleanNotes("a\r\nb\rc\td\u0007"));
        Assert.Equal("left right", UpdateRules.CleanNotes("left\u202E \u2066right\u200F"));
        Assert.Equal("[link](https://x)", UpdateRules.CleanNotes("[link](https://x)"));
        Assert.Equal("", UpdateRules.CleanNotes(null));

        var longText = UpdateRules.CleanNotes(new string('x', UpdateRules.MaxNotesChars - 1) + "\U0001F600tail");
        Assert.Equal(UpdateRules.MaxNotesChars - 1, longText.Length);
        Assert.False(char.IsHighSurrogate(longText[longText.Length - 1]));
        Assert.Equal(UpdateRules.MaxNotesChars, UpdateRules.CleanNotes(new string('y', 20000)).Length);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\Cpfs-1.0.0.exe", false, false, UpdatePlace.InPlace)]
    [InlineData(@"C:\Users\me\Downloads\Cpfs-1.0.0.exe", true, false, UpdatePlace.Elevated)]
    [InlineData(@"C:\Users\me\Downloads\Cpfs-1.0.0.exe", false, null, UpdatePlace.SaveAs)]
    [InlineData(@"C:\Users\me\Downloads\Cpfs-1.0.0.exe", true, null, UpdatePlace.Elevated)]
    [InlineData(@"X:\projects\cpfs\Cpfs.exe", false, true, UpdatePlace.SaveAs)]
    [InlineData(@"\\server\share\Cpfs.exe", false, false, UpdatePlace.SaveAs)]
    [InlineData(@"C:\Program Files\CPFS\Cpfs.exe", false, false, UpdatePlace.BackgroundCopy)]
    [InlineData(@"c:\program files\cpfs\CPFS.EXE", false, false, UpdatePlace.BackgroundCopy)]
    [InlineData(@"C:\Program Files\CPFS\Cpfs.exe", true, false, UpdatePlace.Elevated)]
    public void WhereTheExeIsDecidesTheWay(string exe, bool elevated, bool? network, UpdatePlace expected)
    {
        Assert.Equal(expected, UpdateRules.Place(exe, elevated, network, @"C:\Program Files\CPFS\Cpfs.exe"));
    }

    [Theory]
    [InlineData(UpdatePlace.InPlace, false, false, true)]
    [InlineData(UpdatePlace.SaveAs, false, false, true)]
    [InlineData(UpdatePlace.InPlace, true, false, false)]
    [InlineData(UpdatePlace.SaveAs, true, false, false)]
    [InlineData(UpdatePlace.InPlace, true, true, true)]
    [InlineData(UpdatePlace.Elevated, false, false, false)]
    [InlineData(UpdatePlace.Elevated, true, true, false)]
    [InlineData(UpdatePlace.BackgroundCopy, false, false, false)]
    [InlineData(UpdatePlace.BackgroundCopy, false, true, false)]
    public void TheEngineRunsOnlyForItsTwoPlacesAndNeverElevated(UpdatePlace place, bool elevated, bool elevatedAllowed, bool runs)
    {
        Assert.Equal(runs, UpdateRules.EngineMayRun(place, elevated, elevatedAllowed));
    }

    [Theory]
    [InlineData(false, false, null, RestartOutcome.Keep)]
    [InlineData(false, false, 0, RestartOutcome.Keep)]
    [InlineData(false, false, 3, RestartOutcome.RollBack)]
    [InlineData(false, false, -1, RestartOutcome.RollBack)]
    [InlineData(false, true, 3, RestartOutcome.Started)]
    [InlineData(false, true, null, RestartOutcome.Started)]
    [InlineData(true, false, null, RestartOutcome.RollBack)]
    public void OnlyAFailedStartRollsBack(bool startFailed, bool signal, int? exit, RestartOutcome expected)
    {
        Assert.Equal(expected, UpdateRules.AfterStart(startFailed, signal, exit));
    }

    [Fact]
    public void TheNamesBesideTheExeAreFixed()
    {
        const string exe = @"C:\Users\me\Downloads\Cpfs-1.0.0.exe";
        Assert.Equal(new[]
        {
            @"C:\Users\me\Downloads\Cpfs-1.0.0.exe.cpfs-old",
            @"C:\Users\me\Downloads\Cpfs-1.0.0.exe.cpfs-failed",
            @"C:\Users\me\Downloads\Cpfs-1.0.0.cpfs-update.exe",
        }, UpdateRules.LeftoverPaths(exe));
        Assert.Equal(@"D:\Tools\CPFS.cpfs-update.exe", UpdateRules.StagedPath(@"D:\Tools\CPFS.exe"));
    }

    [Fact]
    public void RestartOptionsAreTakenOutAndChecked()
    {
        var rest = UpdateRules.TakeRestartOptions(new[] { "--after-update", "0.9.0-dev5", "--page", "Setup" }, out var after, out var failed);
        Assert.Equal("0.9.0-dev5", after);
        Assert.Null(failed);
        Assert.Equal(new[] { "--page", "Setup" }, rest);

        UpdateRules.TakeRestartOptions(new[] { "--update-failed", "0.9.1" }, out after, out failed);
        Assert.Null(after);
        Assert.Equal("0.9.1", failed);

        UpdateRules.TakeRestartOptions(new[] { "--after-update", "0.9.0\" --elevated" }, out after, out _);
        Assert.Null(after);
        UpdateRules.TakeRestartOptions(new[] { "--after-update" }, out after, out _);
        Assert.Null(after);
    }

    [Theory]
    [InlineData("0.9.1", "0.9.0", true)]
    [InlineData("0.9.1+abc", "0.9.0", true)]
    [InlineData("0.9.0", "0.9.0-dev6", true)]
    [InlineData("0.9.0", "0.9.0", false)]
    [InlineData("0.8.0", "0.9.0", false)]
    [InlineData(null, "0.9.0", false)]
    public void ANewerBackgroundCopyIsADowngrade(string? copy, string running, bool downgrade)
    {
        Assert.Equal(downgrade, UpdateRules.IsDowngrade(copy, running));
        var files = new AutoPrivateFiles(true, false, false, false, copy, running);
        Assert.Equal(downgrade, files.CopyIsNewer);
    }

    [Fact]
    public void ARestorePointIsUntickedOnlyWhenTheCopyIsTheOnlyChange()
    {
        Assert.False(ApplyList.RestorePointByDefault(new Plan(new[] { Item("auto.exe", PlanAction.Change), Item("share", PlanAction.Keep) })));
        Assert.True(ApplyList.RestorePointByDefault(new Plan(new[] { Item("auto.exe", PlanAction.Change), Item("share", PlanAction.Change) })));
        Assert.True(ApplyList.RestorePointByDefault(new Plan(new[] { Item("share", PlanAction.Change) })));
        Assert.True(ApplyList.RestorePointByDefault(new Plan(new[] { Item("auto.exe", PlanAction.RemoveIfOurs) })));
        Assert.True(ApplyList.RestorePointByDefault(new Plan(new[] { Item("share", PlanAction.Keep) })));
        Assert.False(ApplyList.RestorePointByDefault(new Plan(new[] { Item("auto.exe", PlanAction.Ask) with { Ticked = true } })));
    }

    private static PlanItem Item(string key, PlanAction action) => new() { Key = key, Section = PlanSection.AutoPrivate, Action = action, Subject = PlanValue.Of(key) };

    private static UpdateRefusal Refusal(string json)
    {
        UpdateRules.ReadOffer(json, "0.9.0", out var refusal);
        return refusal;
    }
}
