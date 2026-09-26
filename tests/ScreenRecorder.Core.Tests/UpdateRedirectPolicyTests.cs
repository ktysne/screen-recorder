using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public sealed class UpdateRedirectPolicyTests
{
    private static readonly Uri CurrentUri = new("https://ktysne.info/screen-recorder/update.json");

    [Theory]
    [InlineData("https://ktysne.info/releases/latest.zip", "https://ktysne.info/releases/latest.zip")]
    [InlineData("../archives/latest.zip", "https://ktysne.info/archives/latest.zip")]
    public void AllowsHttpsRedirectsToExactHost(string location, string expected)
    {
        Assert.True(UpdateRedirectPolicy.TryResolve(CurrentUri, new Uri(location, UriKind.RelativeOrAbsolute), 0, out var target, out var error));
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
    public void RejectsInsecureOrUntrustedRedirects(string location)
    {
        Assert.False(UpdateRedirectPolicy.TryResolve(CurrentUri, new Uri(location), 0, out var target, out var error));
        Assert.Null(target);
        Assert.NotNull(error);
    }

    [Fact]
    public void RejectsRedirectWithoutLocation()
    {
        Assert.False(UpdateRedirectPolicy.TryResolve(CurrentUri, null, 0, out var target, out var error));
        Assert.Null(target);
        Assert.NotNull(error);
    }

    [Fact]
    public void AllowsFiveRedirectsAndRejectsTheSixth()
    {
        Assert.True(UpdateRedirectPolicy.TryResolve(CurrentUri, new Uri("/one", UriKind.Relative), 4, out _, out _));
        Assert.False(UpdateRedirectPolicy.TryResolve(CurrentUri, new Uri("/six", UriKind.Relative), 5, out var target, out var error));
        Assert.Null(target);
        Assert.Contains("上限", error);
    }
}
