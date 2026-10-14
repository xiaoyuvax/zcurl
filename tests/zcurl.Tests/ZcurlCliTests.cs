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

    [Theory]
    [InlineData("-X")]
    [InlineData("-d")]
    [InlineData("--noproxy")]
    public void Parse_CurlStyleOption_IsFatalWithHint(string flag)
    {
        var fatal = ZcurlCli.Parse([flag]).Fatal;
        Assert.NotNull(fatal);
        Assert.Contains("curl", fatal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_FieldOption_IsCaptured()
    {
        var o = ZcurlCli.Parse(["get", "App#1", "Text", "--field", "value"]);
        Assert.Equal("value", o.Field);
        Assert.Null(o.Fatal);
        Assert.NotNull(ZcurlCli.Parse(["--field"]).Fatal);   // 缺取值
    }

    [Fact]
    public void Parse_QueryOption_CollectsRepeatedPairs()
    {
        var o = ZcurlCli.Parse(["registry", "-q", "type=WinForm", "-q", "limit=10", "-q", "keyword=%E4%B8%AD"]);
        Assert.Equal("registry", o.Sub);
        Assert.Equal([("type", "WinForm"), ("limit", "10"), ("keyword", "%E4%B8%AD")], o.Query);
        Assert.Null(o.Fatal);
    }

    [Fact]
    public void Parse_QueryOption_MissingOrMalformed_IsFatal()
    {
        Assert.NotNull(ZcurlCli.Parse(["-q"]).Fatal);          // 缺取值
        Assert.NotNull(ZcurlCli.Parse(["-q", "noequals"]).Fatal);
        Assert.NotNull(ZcurlCli.Parse(["-q", "=v"]).Fatal);    // 空 key
    }

    [Fact]
    public void Parse_NewOptions_AreCaptured()
    {
        var o = ZcurlCli.Parse(["--body-file", "p.json", "--profile", "App#1", "--timeout", "5000",
            "--expect", "400", "get", "X", "Y"]);
        Assert.Equal("p.json", o.BodyFile);
        Assert.Equal("App#1", o.Profile);
        Assert.Equal(5000, o.TimeoutMs);
        Assert.Equal(400, o.ExpectStatus);
        Assert.Null(o.Fatal);
        Assert.NotNull(ZcurlCli.Parse(["--body-file"]).Fatal);   // 缺取值
        Assert.NotNull(ZcurlCli.Parse(["--profile"]).Fatal);
    }

    [Theory]
    [InlineData("--timeout", "0")]
    [InlineData("--timeout", "-5")]
    [InlineData("--timeout", "abc")]
    [InlineData("--timeout", "1.5")]
    [InlineData("--expect", "99")]
    [InlineData("--expect", "600")]
    [InlineData("--expect", "x")]
    public void Parse_TimeoutExpect_BadValues_IsFatal(string flag, string value)
        => Assert.NotNull(ZcurlCli.Parse([flag, value]).Fatal);

    [Fact]
    public void StateUrl_EscapesInstanceName()
        => Assert.Equal("http://h/agent/state?name=App%231", ZcurlCli.StateUrl("http://h", "App#1"));

    [Fact]
    public void ApplyQuery_NoQuery_LeavesUrlUntouched()
    {
        var url = "http://h/agent/get?name=App%231&path=Text";
        Assert.Equal(url, ZcurlCli.ApplyQuery(url, []));
    }

    [Fact]
    public void ApplyQuery_AppendsWithAmp_WhenUrlAlreadyHasQuery()
        => Assert.Equal("http://h/agent/registry?type=WinForm&limit=10",
            ZcurlCli.ApplyQuery("http://h/agent/registry", [("type", "WinForm"), ("limit", "10")]));

    [Fact]
    public void ApplyQuery_EncodesValues_ButNotPreEncoded()
    {
        Assert.Equal("http://h/x?name=App%231", ZcurlCli.ApplyQuery("http://h/x", [("name", "App#1")]));
        Assert.Equal("http://h/x?path=%E6%96%87%E6%9C%AC", ZcurlCli.ApplyQuery("http://h/x", [("path", "%E6%96%87%E6%9C%AC")]));
        Assert.Equal("http://h/x?raw=a%3Db", ZcurlCli.ApplyQuery("http://h/x", [("raw", "a=b")]));
    }

    [Fact]
    public void ApplyQuery_RawModeUrl_WithoutQuery_GetsQuestionMark()
        => Assert.Equal("http://h/agent/control?name=MainForm",
            ZcurlCli.ApplyQuery("http://h/agent/control", [("name", "MainForm")]));

    // ---------- 响应字段提取（--field） ----------
    [Fact]
    public void ExtractField_StringUnquoted_ObjectCompact()
    {
        Assert.Equal("已打开图像库", ZcurlCli.ExtractField("""{"ok":true,"message":"已打开图像库"}""", "message"));
        Assert.Equal("""{"a":1}""", ZcurlCli.ExtractField("""{"result":{"a":1}}""", "result"));
        Assert.Equal("42", ZcurlCli.ExtractField("""{"n":42}""", "n"));
        Assert.Equal("true", ZcurlCli.ExtractField("""{"ok":true}""", "ok"));
    }

    [Theory]
    [InlineData("""{"ok":true}""", "missing")]
    [InlineData("not json", "value")]
    [InlineData("""["a"]""", "value")]
    [InlineData("""{"value":null}""", "value")]
    [InlineData("""{"value":"x"}""", "")]
    public void ExtractField_MissingOrNull_ReturnsNull(string json, string field)
        => Assert.Null(ZcurlCli.ExtractField(json, field));

    [Fact]
    public void ExtractField_TopLevelArray_TakesFirstMatch()
        => Assert.Equal("v1", ZcurlCli.ExtractField("""[{"name":"v1"},{"name":"v2"}]""", "name"));

    [Fact]
    public void ExtractField_DotPath_NavigatesObjectsAndArrays()
    {
        Assert.Equal("0.75", ZcurlCli.ExtractField("""{"data":{"camera":{"alpha":0.75}}}""", "data.camera.alpha"));
        Assert.Equal("y", ZcurlCli.ExtractField("""{"items":[{"n":"x"},{"n":"y"}]}""", "items.1.n"));
        Assert.Equal("10", ZcurlCli.ExtractField("""{"a":[10]}""", "a.0"));
        Assert.Equal("1", ZcurlCli.ExtractField("""[{"cfg":{"v":1}}]""", "cfg.v"));     // 根数组回退 + 点路径
        Assert.Null(ZcurlCli.ExtractField("""{"a":{"b":1}}""", "a.c"));                // 中段缺失
        Assert.Null(ZcurlCli.ExtractField("""{"a":[10]}""", "a.x"));                   // 数组段非数字
        Assert.Null(ZcurlCli.ExtractField("""{"a":{"b":1}}""", "a.b.c"));              // 标量下再取段
    }

    [Fact]
    public void ExtractField_LiteralDottedKey_StillWins_LegacySemanticsPreserved()
        => Assert.Equal("1", ZcurlCli.ExtractField("""{"a.b":1,"a":{"b":2}}""", "a.b"));

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
    public void DecideBody_ExplicitSourcesWin_StdinFallback()
    {
        Assert.Equal((BodyKind.Env, (string?)null), ZcurlCli.DecideBody("B", null, stdinRedirected: true));
        Assert.Equal((BodyKind.Env, (string?)null), ZcurlCli.DecideBody("B", null, stdinRedirected: false));
        Assert.Equal((BodyKind.File, (string?)null), ZcurlCli.DecideBody(null, "f.json", stdinRedirected: true));   // file 压过 stdin
        Assert.Equal((BodyKind.File, (string?)null), ZcurlCli.DecideBody(null, "f.json", stdinRedirected: false));
        Assert.Equal((BodyKind.Stdin, (string?)null), ZcurlCli.DecideBody(null, null, stdinRedirected: true));
        Assert.Equal((BodyKind.None, (string?)null), ZcurlCli.DecideBody(null, null, stdinRedirected: false));
    }

    [Fact]
    public void DecideBody_EnvAndFileTogether_IsFatal()
    {
        var (kind, fatal) = ZcurlCli.DecideBody("B", "f.json", stdinRedirected: false);
        Assert.Equal(BodyKind.None, kind);
        Assert.NotNull(fatal);
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

    // ---------- 发现档案（--profile）：解析与选择是纯函数 ----------

    [Fact]
    public void ParseProfileJson_RealShape_ParsesAll()
    {
        var (ep, key, pid, err) = ZcurlCli.ParseProfileJson(
            """{"app":"X","endpoint":"http://127.0.0.1:8000","key":"abc","protocol":"appagent/1.0","pid":123,"registeredAt":"2026-10-13T04:31:38Z"}""");
        Assert.Null(err);
        Assert.Equal("http://127.0.0.1:8000", ep);
        Assert.Equal("abc", key);
        Assert.Equal(123, pid);
    }

    [Theory]
    [InlineData("""{"endpoint":"http://h","key":"k"}""", "pid")]
    [InlineData("""{"endpoint":"http://h","pid":1}""", "key")]
    [InlineData("""{"key":"k","pid":1}""", "endpoint")]
    [InlineData("""{"endpoint":"ftp://h","key":"k","pid":1}""", "http")]
    [InlineData("not json", "JSON")]
    [InlineData("[1,2]", "对象")]
    public void ParseProfileJson_BadShape_ReturnsError(string json, string expect)
    {
        var err = ZcurlCli.ParseProfileJson(json).Error;
        Assert.NotNull(err);
        Assert.Contains(expect, err, StringComparison.OrdinalIgnoreCase);
    }

    static ZcurlCli.ProfileCandidate Cand(string path, int? pid, bool alive, string? err = null,
        DateTime? mtime = null, string? ep = "http://h", string? key = "k")
        => new(path, ep, key, pid, alive, mtime ?? DateTime.UnixEpoch, err);

    [Fact]
    public void SelectProfile_NewestUsableWins_DeadPidAndParseErrorRejected()
    {
        var t0 = DateTime.UnixEpoch;
        var cands = new[]
        {
            Cand("a.json", 1, true, mtime: t0),
            Cand("b.json", 2, true, mtime: t0.AddMinutes(5)),
            Cand("c.json", 3, false, mtime: t0.AddMinutes(9)),                         // 死 pid
            Cand("d.json", null, false, err: "JSON 解析失败", mtime: t0.AddMinutes(10)), // 解析失败（mtime 最新也不选）
        };
        Assert.Equal("b.json", ZcurlCli.SelectProfile(cands)!.Value.Path);
    }

    [Fact]
    public void SelectProfile_NoneUsable_ReturnsNull()
    {
        Assert.Null(ZcurlCli.SelectProfile([]));
        Assert.Null(ZcurlCli.SelectProfile([Cand("a.json", 1, false)]));               // 全死
        Assert.Null(ZcurlCli.SelectProfile([Cand("a.json", null, false, err: "坏")])); // 全解析失败
    }

    // ---------- --expect 退出码语义 ----------

    [Theory]
    [InlineData(204, null, 0)]
    [InlineData(404, null, 1)]
    [InlineData(400, null, 1)]
    [InlineData(404, 404, 0)]     // 语义翻转：匹配即成功（404 匹配也 exit 0）
    [InlineData(400, 404, 1)]
    [InlineData(200, 400, 1)]
    public void ExpectExit_MatchWins_OtherwiseClassic2xx(int status, int? expect, int expected)
        => Assert.Equal(expected, ZcurlCli.ExpectExit(status, expect));

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

    [Fact]
    public void BaseOf_ProfileSitsBetweenCliFlagAndEnv()
    {
        string? saved = Environment.GetEnvironmentVariable("ZCURL_BASE");
        try
        {
            Environment.SetEnvironmentVariable("ZCURL_BASE", "http://env:1/");
            Assert.Equal("http://prof:9", ZcurlCli.BaseOf(new CliOptions { ProfileBase = "http://prof:9" }));
            Assert.Equal("http://cli:2", ZcurlCli.BaseOf(new CliOptions { ProfileBase = "http://prof:9", BaseUrl = "http://cli:2" }));

            Environment.SetEnvironmentVariable("ZCURL_BASE", null);
            Assert.Equal("http://prof:9", ZcurlCli.BaseOf(new CliOptions { ProfileBase = "http://prof:9" }));
        }
        finally { Environment.SetEnvironmentVariable("ZCURL_BASE", saved); }
    }

    [Fact]
    public void KeyFor_ProfileSitsBetweenFlagsAndEnv()
    {
        string? savedA = Environment.GetEnvironmentVariable("ZCURL_KEY");
        string? savedB = Environment.GetEnvironmentVariable("ZCURL_KEY_B");
        try
        {
            Environment.SetEnvironmentVariable("ZCURL_KEY", null);
            Environment.SetEnvironmentVariable("ZCURL_KEY_B", null);
            var prof = new CliOptions { ProfileKey = "prof" };
            Assert.Equal("prof", ZcurlCli.KeyFor(prof, channelB: false));
            Assert.Equal("prof", ZcurlCli.KeyFor(prof, channelB: true));

            Environment.SetEnvironmentVariable("ZCURL_KEY", "envA");
            Environment.SetEnvironmentVariable("ZCURL_KEY_B", "envB");
            Assert.Equal("prof", ZcurlCli.KeyFor(prof, channelB: false));   // profile 压过环境变量
            Assert.Equal("prof", ZcurlCli.KeyFor(prof, channelB: true));

            Assert.Equal("cliA", ZcurlCli.KeyFor(new CliOptions { Key = "cliA", ProfileKey = "prof" }, channelB: false));  // flag 压过 profile
            Assert.Equal("cliA", ZcurlCli.KeyFor(new CliOptions { Key = "cliA", ProfileKey = "prof" }, channelB: true));
            Assert.Equal("cliB", ZcurlCli.KeyFor(new CliOptions { Key = "cliA", KeyB = "cliB", ProfileKey = "prof" }, channelB: true));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZCURL_KEY", savedA);
            Environment.SetEnvironmentVariable("ZCURL_KEY_B", savedB);
        }
    }
}
