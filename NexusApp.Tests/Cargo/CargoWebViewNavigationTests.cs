using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests.Cargo;

// Pins the pure navigation-allow predicate that CargoWebView uses to cancel any WebView2 navigation
// off the app's own local virtual hosts (defense-in-depth for the offline cargo scene). The predicate
// is static and touches no WPF/WebView2 state, so it is unit-tested directly.
public class CargoWebViewNavigationTests
{
    [Theory]
    [InlineData("https://nexus.cargo/index.html")]
    [InlineData("https://nexus.cargo/three.module.js")]
    [InlineData("https://nexus.hulls/drak-ironclad.bin")]
    public void Allows_OwnVirtualHosts(string uri) =>
        Assert.True(CargoWebView.IsAllowedNavigation(uri));

    [Fact]
    public void Allows_InitialAboutBlank() =>
        Assert.True(CargoWebView.IsAllowedNavigation("about:blank"));

    [Theory]
    [InlineData("http://nexus.cargo/index.html")]      // http downgrade
    [InlineData("https://evil.example/index.html")]    // external https
    [InlineData("https://nexus.cargo.evil.com/x")]     // look-alike host
    [InlineData("javascript:alert(1)")]                // script scheme
    [InlineData("data:text/html,<h1>x</h1>")]          // data scheme
    [InlineData("file:///C:/Windows/system32/")]       // local file scheme
    [InlineData("about:config")]                       // non-blank about page
    [InlineData("")]
    [InlineData(null)]
    public void Denies_EverythingElse(string? uri) =>
        Assert.False(CargoWebView.IsAllowedNavigation(uri));

    [Theory]
    [InlineData("HTTPS://NEXUS.CARGO/index.html")]
    [InlineData("Https://Nexus.Hulls/a.bin")]
    public void Allows_HostAndSchemeCaseInsensitively(string uri) =>
        Assert.True(CargoWebView.IsAllowedNavigation(uri));
}
