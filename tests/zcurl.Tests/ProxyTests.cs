using Xunit;

namespace Zcurl.Tests;

public class ProxyTests
{
    [Fact]
    public void Parse_Proxy_IsOffByDefault()
    {
        var o = ZcurlCli.Parse(["GET", "https://example.com"]);
        Assert.False(o.UseProxy);
        Assert.Null(o.ProxyUrl);
        Assert.Null(o.Fatal);
    }

    [Fact]
    public void Parse_Proxy_UsesEnvironmentOrSystem()
    {
        var o = ZcurlCli.Parse(["GET", "https://example.com", "--proxy"]);
        Assert.True(o.UseProxy);
        Assert.Null(o.ProxyUrl);
        Assert.Equal(["GET", "https://example.com"], o.Pos);
        Assert.Null(o.Fatal);
    }

    [Fact]
    public void Parse_Proxy_ExplicitUrl()
    {
        var o = ZcurlCli.Parse(["GET", "https://example.com", "--proxy=http://127.0.0.1:7897"]);
        Assert.True(o.UseProxy);
        Assert.Equal("http://127.0.0.1:7897", o.ProxyUrl);
        Assert.Null(o.Fatal);
    }

    [Theory]
    [InlineData("--proxy=127.0.0.1:7897")]   // 没有 scheme
    [InlineData("--proxy=")]
    [InlineData("--proxy=socks5://127.0.0.1:7897")]
    public void Parse_Proxy_InvalidUrl_IsFatal(string arg)
    {
        Assert.NotNull(ZcurlCli.Parse(["GET", "https://example.com", arg]).Fatal);
    }

    [Fact]
    public void Parse_NoProxy_OverridesEarlierProxy()
    {
        var o = ZcurlCli.Parse(["GET", "https://example.com", "--proxy=http://127.0.0.1:7897", "--no-proxy"]);
        Assert.False(o.UseProxy);
        Assert.Null(o.ProxyUrl);
        Assert.Null(o.Fatal);
    }

    [Fact]
    public void ResolveProxyUrl_PrefersExplicit_ThenHttps_ThenHttp()
    {
        Assert.Equal("http://explicit:1", ZcurlCli.ResolveProxyUrl("http://explicit:1", "http://https:2", "http://http:3"));
        Assert.Equal("http://https:2", ZcurlCli.ResolveProxyUrl(null, "http://https:2", "http://http:3"));
        Assert.Equal("http://http:3", ZcurlCli.ResolveProxyUrl(null, null, "http://http:3"));
        Assert.Null(ZcurlCli.ResolveProxyUrl(null, null, null));
    }

    [Theory]
    [InlineData("127.0.0.1:7897", "http://127.0.0.1:7897")]
    [InlineData(" http://127.0.0.1:7897 ", "http://127.0.0.1:7897")]
    [InlineData("https://proxy.local:8443", "https://proxy.local:8443")]
    public void ResolveProxyUrl_NormalizesScheme(string input, string expected)
        => Assert.Equal(expected, ZcurlCli.ResolveProxyUrl(input, null, null));

    [Theory]
    [InlineData("socks5://127.0.0.1:7897")]
    [InlineData("not a url")]
    public void ResolveProxyUrl_RejectsUnsupported(string input)
        => Assert.Null(ZcurlCli.ResolveProxyUrl(input, null, null));
}
