using System.Net;
using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class UpdateRedirectPolicyTests
{
    private static readonly Uri ManifestUri = new("https://ktysne.info/screen-recorder/update-v2.json");
    private static readonly Uri LegacyPackageUri = new("https://ktysne.info/screen-recorder/archives/a.zip");
    private static readonly Uri GitHubPackageUri = new("https://github.com/ktysne/screen-recorder/releases/download/v1.2.3/a.zip");

    [Theory]
    [InlineData("https://ktysne.info/releases/latest.zip", "https://ktysne.info/releases/latest.zip")]
    [InlineData("../archives/latest.zip", "https://ktysne.info/archives/latest.zip")]
    public void ManifestRedirectsKeepTheExistingExactHostPolicy(string location, string expected)
    {
        Assert.True(UpdateRedirectPolicy.TryResolve(ManifestUri, new Uri(location, UriKind.RelativeOrAbsolute), 0, out var target, out var error));
        Assert.Equal(new Uri(expected), target);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("http://ktysne.info/releases/latest.zip")]
    [InlineData("https://cdn.ktysne.info/releases/latest.zip")]
    [InlineData("https://ktysne.info.evil.example/releases/latest.zip")]
    [InlineData("https://user@ktysne.info/releases/latest.zip")]
    [InlineData("https://ktysne.info:8443/releases/latest.zip")]
    [InlineData("https://ktysne.info:443/releases/latest.zip")]
    [InlineData("HTTPS://KTYSNE.INFO/releases/latest.zip")]
    [InlineData("//KTYSNE.INFO/releases/latest.zip")]
    [InlineData("//ktysne.info/releases/latest.zip")]
    public void ManifestRedirectsKeepTheExistingRejectionRules(string location)
    {
        Assert.False(UpdateRedirectPolicy.TryResolve(ManifestUri, new Uri(location), 0, out var target, out var error));
        Assert.Null(target);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("https://github.com/ktysne/screen-recorder/releases/download/v1.2.3/a.zip?token=a+b%2Bc%3Bd")]
    [InlineData("https://release-assets.githubusercontent.com/path/%2Fasset?signature=x+y")]
    [InlineData("https://objects.githubusercontent.com/path?x=1")]
    public void AllowsHttpsRedirectsAcrossGitHubHostsAndPreservesTheLocationText(string location)
    {
        Assert.True(UpdateRedirectPolicy.TryResolvePackage(GitHubPackageUri, location, 0, out var target, out var error));
        Assert.Equal(location, target);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("//release-assets.githubusercontent.com/file")]
    [InlineData("http://release-assets.githubusercontent.com/file")]
    [InlineData("https://evilgithubusercontent.com/file")]
    [InlineData("https://githubusercontent.com/file")]
    [InlineData("https://github.com.evil.example/file")]
    [InlineData("https://user@github.com/file")]
    [InlineData("https://github.com:444/file")]
    [InlineData("https://github.com/file#fragment")]
    [InlineData("https://github.com/a b")]
    [InlineData("https://github.com/a\\b")]
    public void RejectsUnsafeGitHubRedirects(string location)
    {
        Assert.False(UpdateRedirectPolicy.TryResolvePackage(GitHubPackageUri, location, 0, out var target, out var error));
        Assert.Null(target);
        Assert.NotNull(error);
    }

    [Fact]
    public void RejectsControlCharactersInGitHubRedirects()
    {
        var location = "https://github.com/file\tname";

        Assert.False(UpdateRedirectPolicy.TryResolvePackage(GitHubPackageUri, location, 0, out _, out _));
    }

    [Theory]
    [InlineData("../archives/latest.zip", "https://ktysne.info/screen-recorder/archives/latest.zip")]
    [InlineData("https://ktysne.info/screen-recorder/next.zip", "https://ktysne.info/screen-recorder/next.zip")]
    public void LegacyPackageUrlMayKeepItsSameHostRedirects(string location, string expected)
    {
        Assert.True(UpdateRedirectPolicy.TryResolvePackage(LegacyPackageUri, location, 0, out var target, out _));
        Assert.Equal(expected, target);
    }

    [Fact]
    public void PackageRedirectsCannotCrossBetweenLegacyAndGitHubHosts()
    {
        Assert.False(UpdateRedirectPolicy.TryResolvePackage(LegacyPackageUri, "https://github.com/ktysne/screen-recorder/file", 0, out _, out _));
        Assert.False(UpdateRedirectPolicy.TryResolvePackage(GitHubPackageUri, "https://ktysne.info/file", 0, out _, out _));
    }

    [Fact]
    public void RedirectLimitAllowsFiveAndRejectsTheSixth()
    {
        const string next = "https://github.com/ktysne/screen-recorder/releases/download/v1.2.3/a.zip";
        Assert.True(UpdateRedirectPolicy.TryResolvePackage(GitHubPackageUri, next, 4, out _, out _));
        Assert.False(UpdateRedirectPolicy.TryResolvePackage(GitHubPackageUri, next, 5, out _, out var error));
        Assert.Contains("上限", error);
    }

    [Theory]
    [InlineData(301, true)]
    [InlineData(302, true)]
    [InlineData(303, true)]
    [InlineData(307, true)]
    [InlineData(308, true)]
    [InlineData(300, false)]
    [InlineData(304, false)]
    [InlineData(399, false)]
    public void RecognizesOnlySpecifiedPackageRedirectStatuses(int statusCode, bool expected)
    {
        Assert.Equal(expected, UpdateRedirectPolicy.IsRedirectStatus(statusCode));
    }

    [Fact]
    public async Task RequestUriKeepsTheSignedQueryText()
    {
        const string requestUrl = "https://release-assets.githubusercontent.com/file?signature=a+b%2Bc%3Bd";
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync(UpdateRedirectPolicy.CreateRequestUri(requestUrl));

        Assert.Equal(requestUrl, handler.RequestUri!.OriginalString);
        Assert.Equal("/file?signature=a+b%2Bc%3Bd", handler.RequestUri.PathAndQuery);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
