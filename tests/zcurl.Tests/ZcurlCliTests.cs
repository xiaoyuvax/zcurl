using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Zcurl.Tests;

public class ZcurlCliTests
{
    // ---------- 铁律 1：argv 只收 ASCII ----------

    [Theory]
    [InlineData("plain-ascii", true)]
    [InlineData("App#1", true)]
    [InlineData("", true)]
    [InlineData("\r\n\t ", true)]                // 控制字符仍是 ASCII
    [InlineData("中文", false)]
    [InlineData("café", false)]
    [InlineData("emoji😀", false)]
    [InlineData("mixed中abc", false)]
    public void IsAscii_FlagsNonAscii(string s, bool expected)
        => Assert.Equal(expected, ZcurlCli.IsAscii(s));

    [Fact]
    public void FirstNonAscii_ReturnsFirstBadElement_OrAllNull()
    {
        Assert.Null(ZcurlCli.FirstNonAscii(["GET", "http://x", "-H", "X:1"]));
        Assert.Equal("参数中文", ZcurlCli.FirstNonAscii(["get", "参数中文", "ok"]));
        Assert.Equal("café", ZcurlCli.FirstNonAscii(["--base", "http://x", "café"]));
    }

    // ---------- 解析 ----------

    [Fact]
    public void Parse_RawMode_CollectsPositionalAndOptions()
    {
        var o = ZcurlCli.Parse(["POST", "http://127.0.0.1:9090/x", "s3cr3t",
            "-H", "Content-Type: application/json", "-H", "X-A:1", "-v", "--body-env", "B", "--out", "a.txt"]);
        Assert.Null(o.Sub);
        Assert.Equal(["POST", "http://127.0.0.1:9090/x", "s3cr3t"], o.Pos);
        Assert.Equal("B", o.BodyEnv);
        Assert.Equal("a.txt", o.OutFile);
        Assert.True(o.Verbose);
        Assert.Equal(2, o.Headers.Count);
        Assert.Equal(("Content-Type", "application/json"), o.Headers[0]);
        Assert.Equal(("X-A", "1"), o.Headers[1]);
        Assert.Null(o.Fatal);
    }

    [Fact]
    public void Parse_SubCommand_NotTreatedAsPositional()
    {
        var o = ZcurlCli.Parse(["get", "App#1", "Text.Name"]);
        Assert.Equal("get", o.Sub);
        Assert.Equal(["App#1", "Text.Name"], o.Pos);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void Parse_HelpWinsOverEverything(string flag)
        => Assert.Equal("help", ZcurlCli.Parse([flag, "get"]).Sub);

    [Fact]
    public void Parse_MissingOptionValue_IsFatal()
    {
        Assert.NotNull(ZcurlCli.Parse(["get", "--base"]).Fatal);
        Assert.NotNull(ZcurlCli.Parse(["--out"]).Fatal);
    }

    [Fact]
    public void Parse_MalformedHeader_IsFatal()
    {
        Assert.NotNull(ZcurlCli.Parse(["-H", "no-colon"]).Fatal);
        Assert.NotNull(ZcurlCli.Parse(["-H", ":novalue"]).Fatal);   // colon <= 0
    }

    [Fact]
    public void Parse_UnknownLongOption_IsFatal()
        => Assert.NotNull(ZcurlCli.Parse(["--frobnicate"]).Fatal);

    [Fact]
    public void UnshiftIfUrl_FallsBackToRawMode_WhenLowercaseMethodGotUrl()
    {
        var o = ZcurlCli.Parse(["get", "http://127.0.0.1:9090/agent/registry"]);
        ZcurlCli.UnshiftIfUrl(o);
        Assert.Null(o.Sub);
        Assert.Equal(["get", "http://127.0.0.1:9090/agent/registry"], o.Pos);
    }

    [Fact]
    public void UnshiftIfUrl_KeepsRealSubCommand()
    {
        var o = ZcurlCli.Parse(["get", "App#1", "Text.Name"]);
        ZcurlCli.UnshiftIfUrl(o);
        Assert.Equal("get", o.Sub);
        Assert.Equal(["App#1", "Text.Name"], o.Pos);
    }

    [Fact]
    public void UnshiftIfUrl_NeverTouchesEnc_Selftest_Help()
    {
        foreach (var sub in new[] { "enc", "selftest", "help" })
        {
            var o = new CliOptions { Sub = sub };
            ZcurlCli.UnshiftIfUrl(o);
            Assert.Equal(sub, o.Sub);
        }
    }

    [Theory]
    [InlineData("http://x", true)]
    [InlineData("https://x", true)]
    [InlineData("HTTPS://x", true)]
    [InlineData("ftp://x", false)]
    [InlineData("App#1", false)]
    public void IsHttpUrl_ChecksScheme(string s, bool expected)
        => Assert.Equal(expected, ZcurlCli.IsHttpUrl(s));

    // ---------- body 来源 ----------

    [Fact]
    public void DecideBody_ExplicitEnvWins_StdinIgnored()
    {
        Assert.Equal(BodyKind.Env, ZcurlCli.DecideBody("B", stdinRedirected: true));
        Assert.Equal(BodyKind.Env, ZcurlCli.DecideBody("B", stdinRedirected: false));
        Assert.Equal(BodyKind.Stdin, ZcurlCli.DecideBody(null, stdinRedirected: true));
        Assert.Equal(BodyKind.None, ZcurlCli.DecideBody(null, stdinRedirected: false));
    }

    // ---------- 百分号编码 ----------

    [Fact]
    public void MaybeEncode_DoesNotDoubleEncode()
    {
        Assert.Equal("%E4%B8%80", ZcurlCli.MaybeEncode("%E4%B8%80"));   // 已编码 → 原样
        Assert.Equal("App%231", ZcurlCli.MaybeEncode("App%231"));
        Assert.Equal("App%231", ZcurlCli.MaybeEncode("App#1")); // ASCII 特殊字符 → 编码
        Assert.Equal("plain", ZcurlCli.MaybeEncode("plain"));
        Assert.Equal("%20x", ZcurlCli.MaybeEncode("%20x"));
    }

    [Fact]
    public void MaybeEncode_EncodesChinese_ToPercentForm()
        => Assert.Equal("%E4%B8%AD%E6%96%87", ZcurlCli.MaybeEncode("中文"));

    [Theory]
    [InlineData("out.txt", "out.txt")]
    [InlineData("out%20dir%2Ff.txt", "out dir/f.txt")]
    [InlineData("100%done", "100%done")]   // 非 %XX 形态 → 原样
    public void MaybeDecode_OnlyWhenLooksEncoded(string input, string expected)
        => Assert.Equal(expected, ZcurlCli.MaybeDecode(input));

    [Fact]
    public void PercentEncode_RoundTripsThroughUnescape()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("一个女孩，赛博朋克\n");
        string enc = ZcurlCli.PercentEncode(bytes);
        Assert.True(enc.All(c => c < 0x80), "编码结果必须纯 ASCII");
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(Uri.UnescapeDataString(enc)));
    }

    [Fact]
    public void PercentEncode_LeavesUnreservedAscii_EscapesTheRest()
    {
        Assert.Equal("abc-._~", ZcurlCli.PercentEncode(Encoding.ASCII.GetBytes("abc-._~")));
        Assert.Equal("%20", ZcurlCli.PercentEncode([(byte)' ']));
        Assert.Equal("%0A", ZcurlCli.PercentEncode([0x0A]));
        Assert.Equal("%FF", ZcurlCli.PercentEncode([0xFF]));
        Assert.Equal("", ZcurlCli.PercentEncode([]));
    }

    // ---------- URL 组装 ----------

    [Fact]
    public void DescribeUrl_EscapesHashAndDefaultsFormatToMd()
        => Assert.Equal("http://h/agent/describe?name=App%231&format=md",
            ZcurlCli.DescribeUrl("http://h", "App#1"));

    [Fact]
    public void GetUrl_EscapesPath_AndKeepsPreEncodedValues()
    {
        Assert.Equal("http://h/agent/get?name=App%231&path=Text.Name",
            ZcurlCli.GetUrl("http://h", "App#1", "Text.Name"));
        Assert.Equal("http://h/agent/get?name=App%231&path=%E6%96%87%E6%9C%AC",
            ZcurlCli.GetUrl("http://h", "App#1", "%E6%96%87%E6%9C%AC"));   // 已编码不二次编码
    }

    [Fact]
    public void ActionAndManifestUrls_UseAppAgentEndpoints()
    {
        Assert.Equal("http://h/appagent/actions/OpenWorkspace?name=App%231",
            ZcurlCli.ActionUrl("http://h", "App#1", "OpenWorkspace"));
        Assert.Equal("http://h/appagent/manifest?name=App%231",
            ZcurlCli.ManifestUrl("http://h", "App#1"));
        Assert.Equal("http://h/agent/invoke?name=App%231&method=MoveWindowAction",
            ZcurlCli.InvokeUrl("http://h", "App#1", "MoveWindowAction"));
        Assert.Equal("http://h/agent/set?name=App%231",
            ZcurlCli.SetUrl("http://h", "App#1"));
    }

    // ---------- 载荷组装 ----------

    [Fact]
    public void ComposeSetBody_WrapsJsonValue_WithoutDoubleQuoting()
    {
        var body = JsonNode.Parse(ZcurlCli.ComposeSetBody("Text.Name", Encoding.UTF8.GetBytes("\"hi\"")))!;
        Assert.Equal("Text.Name", (string?)body["path"]);
        Assert.Equal("hi", (string?)body["value"]);
    }

    [Fact]
    public void ComposeSetBody_EmptyBody_YieldsNullValue_AndKeepsChinese()
    {
        var body = JsonNode.Parse(ZcurlCli.ComposeSetBody("路径", []))!;
        Assert.Equal("路径", (string?)body["path"]);
        Assert.True(body["value"] is null);
    }

    [Fact]
    public void ComposeSetBody_ObjectValue_PreservedAsObject()
    {
        var body = JsonNode.Parse(ZcurlCli.ComposeSetBody("Text", Encoding.UTF8.GetBytes("{\"a\":1}")))!;
        var value = Assert.IsType<JsonObject>(body["value"]);
        Assert.Equal(1, (int?)value["a"]);
    }

    [Fact]
    public void ComposeInvokeBody_EmptyIsArray_InvalidShapeThrows()
    {
        Assert.Equal("[]", ZcurlCli.ComposeInvokeBody([]));
        Assert.Equal("[\"x\",1]", ZcurlCli.ComposeInvokeBody(Encoding.UTF8.GetBytes("[\"x\",1]")));
        Assert.Throws<FormatException>(() => ZcurlCli.ComposeInvokeBody(Encoding.UTF8.GetBytes("{\"a\":1}")));
        Assert.Throws<FormatException>(() => ZcurlCli.ComposeInvokeBody(Encoding.UTF8.GetBytes("not json")));
    }

    [Fact]
    public void ComposeActionBody_AddsCallId_AndRequiresObject()
    {
        var body = JsonNode.Parse(ZcurlCli.ComposeActionBody(Encoding.UTF8.GetBytes("{\"folderPath\":\"C:/work\"}")))!;
        Assert.Equal("C:/work", (string?)body["args"]!["folderPath"]);
        string callId = (string?)body["callId"] ?? string.Empty;
        Assert.Equal(32, callId.Length);
        Assert.True(callId.All(Uri.IsHexDigit));

        var empty = JsonNode.Parse(ZcurlCli.ComposeActionBody([]))!;
        Assert.True(empty["args"] is null);
        Assert.NotNull(empty["callId"]);

        Assert.Throws<FormatException>(() => ZcurlCli.ComposeActionBody(Encoding.UTF8.GetBytes("[1,2]")));
    }

    // ---------- 基址与 key（临时改环境变量，测试内恢复） ----------

    [Fact]
    public void BaseOf_PriorityIs_Explicit_thenEnv_thenDefault()
    {
        string? saved = Environment.GetEnvironmentVariable("ZCURL_BASE");
        try
        {
            Environment.SetEnvironmentVariable("ZCURL_BASE", null);
            Assert.Equal(ZcurlCli.DefaultBase, ZcurlCli.BaseOf(new CliOptions()));

            Environment.SetEnvironmentVariable("ZCURL_BASE", "http://env:1/");
            Assert.Equal("http://env:1", ZcurlCli.BaseOf(new CliOptions()));   // 尾斜杠裁掉

            Assert.Equal("http://cli:2", ZcurlCli.BaseOf(new CliOptions { BaseUrl = "http://cli:2" }));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZCURL_BASE", saved);
        }
    }

    [Fact]
    public void KeyFor_ChannelB_FallsBackThroughKeyB_thenKey()
    {
        string? savedA = Environment.GetEnvironmentVariable("ZCURL_KEY");
        string? savedB = Environment.GetEnvironmentVariable("ZCURL_KEY_B");
        try
        {
            Environment.SetEnvironmentVariable("ZCURL_KEY", null);
            Environment.SetEnvironmentVariable("ZCURL_KEY_B", null);

            Assert.Null(ZcurlCli.KeyFor(new CliOptions(), channelB: false));
            Assert.Null(ZcurlCli.KeyFor(new CliOptions(), channelB: true));

            var withKey = new CliOptions { Key = "k" };
            Assert.Equal("k", ZcurlCli.KeyFor(withKey, channelB: false));
            Assert.Equal("k", ZcurlCli.KeyFor(withKey, channelB: true));        // B 面回落到 A key

            Environment.SetEnvironmentVariable("ZCURL_KEY", "envA");
            Assert.Equal("envA", ZcurlCli.KeyFor(new CliOptions(), channelB: false));
            Assert.Equal("envA", ZcurlCli.KeyFor(new CliOptions(), channelB: true));

            Environment.SetEnvironmentVariable("ZCURL_KEY_B", "envB");
            Assert.Equal("envB", ZcurlCli.KeyFor(new CliOptions(), channelB: true));
            Assert.Equal("envA", ZcurlCli.KeyFor(new CliOptions(), channelB: false));

            var both = new CliOptions { Key = "cliA", KeyB = "cliB" };
            Assert.Equal("cliB", ZcurlCli.KeyFor(both, channelB: true));
            Assert.Equal("cliA", ZcurlCli.KeyFor(both, channelB: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZCURL_KEY", savedA);
            Environment.SetEnvironmentVariable("ZCURL_KEY_B", savedB);
        }
    }
}
