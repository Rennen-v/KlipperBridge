using Xunit;

namespace KlipperBridge.Tests;

public sealed class ProxyRouteSelectorTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/assets/index.js")]
    [InlineData("/dashboard")]
    [InlineData("/favicon.ico")]
    public void SendsUiAndSpaPathsToFluidd(string path) =>
        Assert.False(ProxyRouteSelector.IsMoonrakerPath(path));

    [Theory]
    [InlineData("/server/info")]
    [InlineData("/server/files/upload")]
    [InlineData("/printer/objects/query")]
    [InlineData("/api/version")]
    [InlineData("/websocket")]
    [InlineData("/klippysocket")]
    public void SendsMoonrakerApiAndSocketsToMoonraker(string path) =>
        Assert.True(ProxyRouteSelector.IsMoonrakerPath(path));

    [Fact]
    public void DoesNotRouteSimilarButUnrelatedPrefixesToMoonraker() =>
        Assert.False(ProxyRouteSelector.IsMoonrakerPath("/server-assets/app.js"));
}
