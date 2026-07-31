using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// Pins the pure navigation-allow predicate that MapWebView uses to cancel any WebView2 navigation
// off the app's own local virtual host (defense-in-depth for the offline starmap scene), plus the
// pure JS-message parse-and-route decision (ParseMessage). Both are static and touch no
// WPF/WebView2 state, so they are unit-tested directly. Shape mirrors Cargo\CargoWebViewNavigationTests.cs.
public class MapWebViewNavigationTests
{
    [Theory]
    [InlineData("https://nexus.map/index.html")]
    public void Allows_OwnVirtualHost(string uri) =>
        Assert.True(MapWebView.IsAllowedNavigation(uri));

    [Fact]
    public void Allows_InitialAboutBlank() =>
        Assert.True(MapWebView.IsAllowedNavigation("about:blank"));

    [Theory]
    [InlineData("https://nexus.cargo/index.html")]     // another view's own host
    [InlineData("http://nexus.map/")]                  // http downgrade
    [InlineData("https://example.com/")]                // external https
    [InlineData("https://nexus.map.evil.com/x")]        // look-alike host
    [InlineData("javascript:alert(1)")]                 // script scheme
    [InlineData("data:text/html,<h1>x</h1>")]           // data scheme
    [InlineData("file:///C:/Windows/system32/")]        // local file scheme
    [InlineData("about:config")]                        // non-blank about page
    [InlineData("")]
    [InlineData(null)]
    public void Denies_EverythingElse(string? uri) =>
        Assert.False(MapWebView.IsAllowedNavigation(uri));

    [Theory]
    [InlineData("HTTPS://NEXUS.MAP/index.html")]
    [InlineData("Https://Nexus.Map/index.html")]
    public void Allows_HostAndSchemeCaseInsensitively(string uri) =>
        Assert.True(MapWebView.IsAllowedNavigation(uri));

    [Fact]
    public void Denies_Garbage() =>
        Assert.False(MapWebView.IsAllowedNavigation("not a uri at all ::::"));

    // --- ParseMessage: pure parse-and-route decision for inbound JS messages ---

    [Fact]
    public void ParseMessage_Ready()
    {
        var msg = MapWebView.ParseMessage("{\"type\":\"ready\"}");
        Assert.NotNull(msg);
        Assert.Equal("ready", msg!.Type);
    }

    [Fact]
    public void ParseMessage_PinClick()
    {
        var msg = MapWebView.ParseMessage("{\"type\":\"pinClick\",\"id\":42}");
        Assert.NotNull(msg);
        Assert.Equal("pinClick", msg!.Type);
        Assert.Equal(42, msg.Id);
    }

    [Fact]
    public void ParseMessage_PinDoubleClick()
    {
        var msg = MapWebView.ParseMessage("{\"type\":\"pinDoubleClick\",\"id\":7}");
        Assert.NotNull(msg);
        Assert.Equal("pinDoubleClick", msg!.Type);
        Assert.Equal(7, msg.Id);
    }

    [Fact]
    public void ParseMessage_MeasureResult()
    {
        var msg = MapWebView.ParseMessage("{\"type\":\"measureResult\",\"a\":3,\"b\":9}");
        Assert.NotNull(msg);
        Assert.Equal("measureResult", msg!.Type);
        Assert.Equal(3, msg.A);
        Assert.Equal(9, msg.B);
    }

    [Fact]
    public void ParseMessage_Log()
    {
        var msg = MapWebView.ParseMessage("{\"type\":\"log\",\"msg\":\"scene ready\"}");
        Assert.NotNull(msg);
        Assert.Equal("log", msg!.Type);
        Assert.Equal("scene ready", msg.Msg);
    }

    [Fact]
    public void ParseMessage_MalformedJson_ReturnsNull() =>
        Assert.Null(MapWebView.ParseMessage("not json at all {{{"));

    [Fact]
    public void ParseMessage_EmptyString_ReturnsNull() =>
        Assert.Null(MapWebView.ParseMessage(""));

    [Fact]
    public void ParseMessage_UnknownType_ReturnsNull() =>
        Assert.Null(MapWebView.ParseMessage("{\"type\":\"somethingElse\"}"));

    [Fact]
    public void ParseMessage_MissingType_ReturnsNull() =>
        Assert.Null(MapWebView.ParseMessage("{\"id\":1}"));

    [Fact]
    public void ParseMessage_NonObjectJson_ReturnsNull() =>
        Assert.Null(MapWebView.ParseMessage("[1,2,3]"));

    [Fact]
    public void ParseMessage_PinClick_MissingId_ReturnsNull() =>
        Assert.Null(MapWebView.ParseMessage("{\"type\":\"pinClick\"}"));

    [Fact]
    public void ParseMessage_PinDoubleClick_MissingId_ReturnsNull() =>
        Assert.Null(MapWebView.ParseMessage("{\"type\":\"pinDoubleClick\"}"));

    [Fact]
    public void ParseMessage_MeasureResult_MissingB_ReturnsNull() =>
        Assert.Null(MapWebView.ParseMessage("{\"type\":\"measureResult\",\"a\":3}"));

    [Fact]
    public void ParseMessage_MeasureResult_MissingA_ReturnsNull() =>
        Assert.Null(MapWebView.ParseMessage("{\"type\":\"measureResult\",\"b\":3}"));

    [Fact]
    public void ParseMessage_Log_MissingMsg_ReturnsNull() =>
        Assert.Null(MapWebView.ParseMessage("{\"type\":\"log\"}"));

    [Fact]
    public void ParseMessage_PinClick_WrongIdType_ReturnsNull() =>
        Assert.Null(MapWebView.ParseMessage("{\"type\":\"pinClick\",\"id\":\"forty-two\"}"));

    [Fact]
    public void ParseMessage_Log_WrongMsgType_ReturnsNull() =>
        Assert.Null(MapWebView.ParseMessage("{\"type\":\"log\",\"msg\":123}"));
}
