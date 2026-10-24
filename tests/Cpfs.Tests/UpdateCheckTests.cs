using Cpfs.Core.Updates;
using Xunit;

namespace Cpfs.Tests;

public class UpdateCheckTests
{
    // The repository's own release page prefix comes from UpdateCheck, so no account name is written here.
    private static readonly string Page = UpdateCheck.ReleasePagePrefix + "releases/tag/v0.6.0";

    private static string Json(string tag, string? url) =>
        "{\"url\": \"https://api.github.com/x\", \"tag_name\": \"" + tag + "\"" + (url is null ? "" : ", \"html_url\": \"" + url + "\"")
        + ", \"draft\": false, \"prerelease\": false}";

    private static readonly string Release = Json("v0.6.0", Page);

    [Theory]
    [InlineData("0.6.0", "0.6.0-dev5", 1)]
    [InlineData("0.6.0-dev10", "0.6.0-dev5", 1)]
    [InlineData("0.6.1-dev1", "0.6.0", 1)]
    [InlineData("1.0.0", "0.10.0", 1)]
    [InlineData("v0.6.0", "0.6.0", 0)]
    [InlineData("0.6.0+abc123", "0.6.0", 0)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11", -1)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    public void VersionsCompareBySemanticVersioning(string a, string b, int sign)
    {
        Assert.True(SemVersion.TryParse(a, out var va));
        Assert.True(SemVersion.TryParse(b, out var vb));
        Assert.Equal(sign, System.Math.Sign(va.CompareTo(vb)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.6")]
    [InlineData("0.6.0.1")]
    [InlineData("0.06.0")]
    [InlineData("0.6.x")]
    [InlineData("0.6.0-")]
    [InlineData("0.6.0-dev..5")]
    [InlineData("0.6.0-dev_5")]
    public void NotAVersion(string text)
    {
        Assert.False(SemVersion.TryParse(text, out _));
    }

    [Fact]
    public void AReleaseIsNewerThanADevBuildOfTheSameVersion()
    {
        var result = UpdateCheck.FromResponse(200, Release, "0.6.0-dev5");

        Assert.Equal(UpdateStatus.Newer, result.Status);
        Assert.Equal("0.6.0", result.Version);
        Assert.Equal(Page, result.ReleaseUrl);
    }

    [Theory]
    [InlineData("0.6.0")]
    [InlineData("0.6.1-dev1")]
    public void TheSameOrALaterBuildIsTheLatest(string running)
    {
        var result = UpdateCheck.FromResponse(200, Release, running);

        Assert.Equal(UpdateStatus.Latest, result.Status);
        Assert.Null(result.ReleaseUrl);
    }

    [Fact]
    public void NotFoundMeansNoReleaseYet()
    {
        Assert.Equal(UpdateStatus.NoRelease, UpdateCheck.FromResponse(404, "{\"message\": \"Not Found\"}", "0.6.0-dev5").Status);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(403, "{\"message\": \"API rate limit exceeded\"}")]
    [InlineData(500, "")]
    [InlineData(200, "not json")]
    [InlineData(200, "[]")]
    public void AnythingElseIsAFailedCheck(int? status, string? body)
    {
        Assert.Equal(UpdateStatus.Failed, UpdateCheck.FromResponse(status, body, "0.6.0-dev5").Status);
    }

    // Redirects are not followed, so a 3xx reaches FromResponse; even with a release body it is not an answer.
    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public void ARedirectIsAFailedCheck(int status)
    {
        Assert.Equal(UpdateStatus.Failed, UpdateCheck.FromResponse(status, Release, "0.6.0-dev5").Status);
        Assert.Equal(UpdateStatus.Failed, UpdateCheck.FromResponse(status, null, "0.6.0-dev5").Status);
    }

    [Fact]
    public void OnlyAReleasePageOfThisRepositoryWithAVersionTagIsAccepted()
    {
        var bodies = new[]
        {
            Json("latest", UpdateCheck.ReleasePagePrefix + "releases/tag/latest"),
            Json("v9.0.0", UpdateCheck.ReleasePagePrefix.Replace("github.com", "example.com", System.StringComparison.Ordinal) + "releases/tag/v9.0.0"),
            Json("v9.0.0", "https://github.com/someone/CPFS/releases/tag/v9.0.0"),
            Json("v9.0.0", UpdateCheck.ReleasePagePrefix.Replace("https:", "http:", System.StringComparison.Ordinal) + "releases/tag/v9.0.0"),
            Json("v9.0.0", UpdateCheck.ReleasePagePrefix.Replace("github.com", "github.com:8443", System.StringComparison.Ordinal) + "releases/tag/v9.0.0"),
            Json("v9.0.0", null),
        };

        foreach (var body in bodies)
        {
            Assert.Equal(UpdateStatus.Failed, UpdateCheck.FromResponse(200, body, "0.6.0-dev5").Status);
        }
    }

    [Fact]
    public void TheRequestNamesTheApp()
    {
        Assert.Equal("CPFS/0.6.0-dev5", UpdateCheck.UserAgent("0.6.0-dev5"));
        Assert.StartsWith("https://api.github.com/repos/", UpdateCheck.ApiUrl, System.StringComparison.Ordinal);
        Assert.EndsWith("/CPFS/releases/latest", UpdateCheck.ApiUrl, System.StringComparison.Ordinal);
    }
}
