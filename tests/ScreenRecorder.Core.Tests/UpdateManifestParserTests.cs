using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class UpdateManifestParserTests
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ValidUrl = "https://ktysne.info/screen-recorder/archives/ScreenRecorder-0.2.0-win-x64.zip";

    private static string Manifest(string schema = "1", string version = "\"0.2.0\"", string url = $"\"{ValidUrl}\"", string sha = $"\"{ValidSha}\"", string releasedAt = "\"2026-10-01\"") =>
        $$"""{ "schema": {{schema}}, "latest": { "version": {{version}}, "url": {{url}}, "sha256": {{sha}}, "releasedAt": {{releasedAt}} } }""";

    [Fact]
    public void ValidManifestIsAccepted()
    {
        var result = UpdateManifestParser.Parse(Manifest());
        Assert.Null(result.Error);
        Assert.Equal(new UpdateVersion(0, 2, 0), result.Manifest!.Version);
        Assert.Equal(ValidUrl, result.Manifest.Url);
        Assert.Equal(ValidSha, result.Manifest.Sha256);
        Assert.Equal(new DateOnly(2026, 10, 1), result.Manifest.ReleasedAt);
    }

    [Fact]
    public void UppercaseShaIsAcceptedAndLowercased()
    {
        var result = UpdateManifestParser.Parse(Manifest(sha: $"\"{ValidSha.ToUpperInvariant()}\""));
        Assert.Equal(ValidSha, result.Manifest!.Sha256);
    }

    [Fact]
    public void MissingOrUnreadableReleaseDateDoesNotRejectManifest()
    {
        Assert.Null(UpdateManifestParser.Parse(Manifest(releasedAt: "\"10/01/2026\"")).Manifest!.ReleasedAt);
        Assert.Null(UpdateManifestParser.Parse(Manifest(releasedAt: "null")).Manifest!.ReleasedAt);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("\"1\"")]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("null")]
    public void SchemaOtherThanIntegerOneIsRejected(string schema)
    {
        var result = UpdateManifestParser.Parse(Manifest(schema: schema));
        Assert.Null(result.Manifest);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("\"1.2\"")]
    [InlineData("\"1.2.3.4\"")]
    [InlineData("\"v1.2.3\"")]
    [InlineData("\"1.2.3-beta\"")]
    [InlineData("\" 1.2.3\"")]
    [InlineData("\"1.2.3 \"")]
    [InlineData("\"1..3\"")]
    [InlineData("\"１.２.３\"")]
    [InlineData("\"1.2.+3\"")]
    [InlineData("\"99999999999.0.0\"")]
    [InlineData("\"\"")]
    [InlineData("123")]
    public void VersionOtherThanDigitsXyzIsRejected(string version)
    {
        Assert.Null(UpdateManifestParser.Parse(Manifest(version: version)).Manifest);
    }

    [Theory]
    [InlineData("http://ktysne.info/screen-recorder/archives/a.zip")]
    [InlineData("HTTPS://ktysne.info/screen-recorder/archives/a.zip")]
    [InlineData("https://ktysne.info@example.com/a.zip")]
    [InlineData("https://user:pass@ktysne.info/a.zip")]
    [InlineData("https://KTYSNE.INFO/a.zip")]
    [InlineData("https://Ktysne.info/a.zip")]
    [InlineData("https://ktysne.info:443/a.zip")]
    [InlineData("https://ktysne.info:8443/a.zip")]
    [InlineData("https://sub.ktysne.info/a.zip")]
    [InlineData("https://ktysne.info.example.com/a.zip")]
    [InlineData("https://example.com/ktysne.info/a.zip")]
    [InlineData("https://ktysne.info\\@example.com/a.zip")]
    [InlineData("https://ktysne.info./a.zip")]
    [InlineData("https:// ktysne.info/a.zip")]
    [InlineData("ftp://ktysne.info/a.zip")]
    [InlineData("")]
    public void UrlOutsideDistributionHostIsRejected(string url)
    {
        Assert.Null(UpdateManifestParser.Parse(Manifest(url: $"\"{url.Replace("\\", "\\\\")}\"")).Manifest);
    }

    [Theory]
    [InlineData("https://ktysne.info/screen-recorder/a.zip")]
    [InlineData("https://ktysne.info?x=1")]
    [InlineData("https://ktysne.info#a")]
    public void UrlWithExactHostIsAccepted(string url)
    {
        Assert.True(UpdateManifestParser.IsAllowedDownloadUrl(url));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef 123456789abcdef0123456789abcdef")]
    [InlineData("")]
    public void ShaOtherThanSixtyFourHexCharactersIsRejected(string sha)
    {
        Assert.Null(UpdateManifestParser.Parse(Manifest(sha: $"\"{sha}\"")).Manifest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{ \"schema\": 1 }")]
    [InlineData("{ \"schema\": 1, \"latest\": \"0.2.0\" }")]
    public void MalformedDocumentIsRejected(string json)
    {
        var result = UpdateManifestParser.Parse(json);
        Assert.Null(result.Manifest);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void MissingFieldsAreRejected()
    {
        Assert.Null(UpdateManifestParser.Parse("""{ "schema": 1, "latest": { "url": "https://ktysne.info/a.zip", "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" } }""").Manifest);
        Assert.Null(UpdateManifestParser.Parse("""{ "schema": 1, "latest": { "version": "0.2.0", "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" } }""").Manifest);
        Assert.Null(UpdateManifestParser.Parse("""{ "schema": 1, "latest": { "version": "0.2.0", "url": "https://ktysne.info/a.zip" } }""").Manifest);
    }
}
