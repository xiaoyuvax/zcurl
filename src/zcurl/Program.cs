// zcurl — UTF-8 安全的 HTTP 客户端（Windows shell 专用）
//
// 铁律：
//   1. argv 只收 ASCII。中文/任意非 ASCII 一律走 stdin 字节流或 --body-env（宽字符环境变量）。
//   2. URL 与子命令参数里的非 ASCII 必须先百分号编码（printf '%s' 中文 | zcurl enc）。
//   3. stdout = 原始响应字节；stderr = 状态行；exit 0=2xx，1=HTTP/网络失败，2=用法或编码错误。
using System.Net.Http;
using System.Text;
using Zcurl;

// ---------- 铁律 1：argv 全 ASCII ----------
var badArg = ZcurlCli.FirstNonAscii(args);
if (badArg is not null)
{
    Console.Error.WriteLine($"zcurl: argv 只收 ASCII，参数含非 ASCII 字符 → 拒绝执行。");
    Console.Error.WriteLine($"  问题参数: {badArg}");
    Console.Error.WriteLine("  中文载荷 → stdin（bash: printf '%s' \"$J\" | zcurl ...）或 --body-env（PS: $env:X='...'; zcurl ... --body-env X）");
    Console.Error.WriteLine("  URL/参数里的中文 → 先 printf '%s' '中文' | zcurl enc 百分号编码");
    return 2;
}

if (args.Length == 0)
{
    Console.Error.Write(Usage.Text);
    return 2;
}

var opt = ZcurlCli.Parse(args);
if (opt.Fatal is not null) return Fail(opt.Fatal);
if (opt.Sub == "help")
{
    Console.Out.Write(Usage.Text);
    return 0;
}
ZcurlCli.UnshiftIfUrl(opt);

// ---------- 载荷读取 ----------
async Task<(ReadOnlyMemory<byte> Body, BodyKind Kind, string? Error)> ReadBodyAsync(CliOptions o)
{
    var kind = ZcurlCli.DecideBody(o.BodyEnv, Console.IsInputRedirected);

    if (kind == BodyKind.Env)
    {
        var v = Environment.GetEnvironmentVariable(o.BodyEnv!);
        if (v is null)
            return (default, BodyKind.None,
                $"环境变量 {o.BodyEnv} 未设置（--body-env 必须显式赋值，避免误发上一次的载荷）");
        return (Encoding.UTF8.GetBytes(v), BodyKind.Env, null);   // UTF-16 → UTF-8，进程内完成
    }

    if (kind == BodyKind.Stdin)
    {
        using var ms = new MemoryStream();
        var src = Console.OpenStandardInput();
        var copy = src.CopyToAsync(ms);
        try { await copy.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException) { return (default, BodyKind.None, "stdin 20 秒未关闭（可能继承了未关闭的管道）；请确认管道已结束再执行"); }
        return (ms.GetBuffer().AsMemory(0, (int)ms.Length), BodyKind.Stdin, null);  // 零拷贝：直接借用缓冲区
    }

    return (default, BodyKind.None, null);
}

// ---------- HTTP ----------
async Task<int> SendAsync(string method, string url, string? key, ReadOnlyMemory<byte> body, CliOptions o)
{
    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, Proxy = null });
    using var req = new HttpRequestMessage(new HttpMethod(method), url);

    if (!string.IsNullOrEmpty(key))
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);

    bool userCt = false, userAccept = false;
    foreach (var (n, v) in o.Headers)
    {
        if (n.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) userCt = true;
        if (n.Equals("Accept", StringComparison.OrdinalIgnoreCase)) userAccept = true;
    }

    if (body.Length > 0 || userCt)
    {
        var content = new ReadOnlyMemoryContent(body);
        if (!userCt) content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        req.Content = content;
        foreach (var (n, v) in o.Headers)
            if (n.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                content.Headers.TryAddWithoutValidation(n, v);
    }
    foreach (var (n, v) in o.Headers)
        if (!n.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            req.Headers.TryAddWithoutValidation(n, v);
    if (!userAccept) req.Headers.TryAddWithoutValidation("Accept", "application/json");

    if (o.Verbose)
    {
        Console.Error.WriteLine($"> {method} {url}");
        foreach (var (n, v) in o.Headers)
            Console.Error.WriteLine($"> {n}: {v}");
        Console.Error.WriteLine($"> Authorization: {(string.IsNullOrEmpty(key) ? "(none)" : "Bearer ***")}");
        Console.Error.WriteLine($"> body: {body.Length}B");
    }

    try
    {
        // ResponseHeadersRead：不等整个 body 进内存，边收边写（省一次全量拷贝，--out 大文件峰值内存 1x）
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        var size = resp.Content.Headers.ContentLength is { } n ? $"{n}B" : "?B";
        if (o.OutFile is not null)
        {
            var path = ZcurlCli.MaybeDecode(o.OutFile);
            using (var f = File.Create(path))
                await resp.Content.CopyToAsync(f);
            Console.Error.WriteLine($"[{(int)resp.StatusCode}] {new FileInfo(path).Length}B -> {path}");
        }
        else
        {
            using var stdout = Console.OpenStandardOutput();
            await resp.Content.CopyToAsync(stdout);
            Console.Error.WriteLine($"[{(int)resp.StatusCode}] {size}");
        }
        return resp.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"zcurl: {ex.Message}");
        return 1;
    }
}

static int Fail(string msg)
{
    Console.Error.WriteLine("zcurl: " + msg);
    return 2;
}

// ---------- 分发 ----------
switch (opt.Sub)
{
    case null: // 原始模式
    {
        if (opt.Pos.Count is < 2 or > 3)
            return Fail("原始模式用法: zcurl <METHOD> <URL> [key]");
        string method = opt.Pos[0].ToUpperInvariant();
        if (!method.All(char.IsLetter))
            return Fail($"METHOD 不是纯字母: {opt.Pos[0]}（子命令是小写单词，如 get/set/invoke）");
        string url = opt.Pos[1];
        if (!ZcurlCli.IsHttpUrl(url))
            return Fail($"URL 必须以 http:// 或 https:// 开头: {url}");
        string? key = opt.Key ?? (opt.Pos.Count > 2 ? opt.Pos[2] : ZcurlCli.KeyFor(opt, false));

        var (body, kind, err) = await ReadBodyAsync(opt);
        if (err is not null) return Fail(err);
        return await SendAsync(method, url, key, body, opt);
    }

    case "registry":
        if (opt.Pos.Count != 0) return Fail("用法: zcurl registry");
        return await SendAsync("GET", $"{ZcurlCli.BaseOf(opt)}/agent/registry",
            ZcurlCli.KeyFor(opt, false), default, opt);

    case "describe":
        if (opt.Pos.Count is < 1 or > 2) return Fail("用法: zcurl describe <instance> [format]");
        return await SendAsync("GET", ZcurlCli.DescribeUrl(ZcurlCli.BaseOf(opt), opt.Pos[0],
                opt.Pos.Count > 1 ? opt.Pos[1] : null),
            ZcurlCli.KeyFor(opt, false), default, opt);

    case "get":
        if (opt.Pos.Count != 2) return Fail("用法: zcurl get <instance> <path>");
        return await SendAsync("GET", ZcurlCli.GetUrl(ZcurlCli.BaseOf(opt), opt.Pos[0], opt.Pos[1]),
            ZcurlCli.KeyFor(opt, false), default, opt);

    case "set":
    {
        if (opt.Pos.Count != 2) return Fail("用法: zcurl set <instance> <path>   (body = JSON 值)");
        var (body, _, err) = await ReadBodyAsync(opt);
        if (err is not null) return Fail(err);
        try
        {
            var payload = Encoding.UTF8.GetBytes(ZcurlCli.ComposeSetBody(opt.Pos[1], body.Span));
            return await SendAsync("POST", ZcurlCli.SetUrl(ZcurlCli.BaseOf(opt), opt.Pos[0]),
                ZcurlCli.KeyFor(opt, false), payload, opt);
        }
        catch (FormatException ex) { return Fail(ex.Message); }
    }

    case "invoke":
    {
        if (opt.Pos.Count != 2) return Fail("用法: zcurl invoke <instance> <method>   (body = JSON 数组)");
        var (body, _, err) = await ReadBodyAsync(opt);
        if (err is not null) return Fail(err);
        try
        {
            var payload = Encoding.UTF8.GetBytes(ZcurlCli.ComposeInvokeBody(body.Span));
            return await SendAsync("POST", ZcurlCli.InvokeUrl(ZcurlCli.BaseOf(opt), opt.Pos[0], opt.Pos[1]),
                ZcurlCli.KeyFor(opt, false), payload, opt);
        }
        catch (FormatException ex) { return Fail(ex.Message); }
    }

    case "action": // B 面
    {
        if (opt.Pos.Count != 2) return Fail("用法: zcurl action <instance> <name>   (body = JSON 对象 args)");
        var (body, _, err) = await ReadBodyAsync(opt);
        if (err is not null) return Fail(err);
        try
        {
            var payload = Encoding.UTF8.GetBytes(ZcurlCli.ComposeActionBody(body.Span));
            return await SendAsync("POST", ZcurlCli.ActionUrl(ZcurlCli.BaseOf(opt), opt.Pos[0], opt.Pos[1]),
                ZcurlCli.KeyFor(opt, true), payload, opt);
        }
        catch (FormatException ex) { return Fail(ex.Message); }
    }

    case "manifest": // B 面
        if (opt.Pos.Count != 1) return Fail("用法: zcurl manifest <instance>");
        return await SendAsync("GET", ZcurlCli.ManifestUrl(ZcurlCli.BaseOf(opt), opt.Pos[0]),
            ZcurlCli.KeyFor(opt, true), default, opt);

    case "enc":
    {
        if (opt.Pos.Count != 0) return Fail("用法: zcurl enc   (stdin 或 --body-env 输入，stdout 输出百分号编码)");
        var (body, kind, err) = await ReadBodyAsync(opt);
        if (err is not null) return Fail(err);
        if (kind == BodyKind.None)
            return Fail("enc 没有输入。用法: printf '%s' '中文' | zcurl enc");
        var ascii = Encoding.ASCII.GetBytes(ZcurlCli.PercentEncode(body.Span));
        using var stdout = Console.OpenStandardOutput();
        await stdout.WriteAsync(ascii);
        return 0;
    }

    case "selftest":
        if (opt.Pos.Count != 0) return Fail("用法: zcurl selftest");
        return await SelfTest.RunAsync();

    default:
        return Fail($"未知子命令 {opt.Sub}");
}

internal static class Usage
{
    public const string Text = """
        zcurl — UTF-8-safe HTTP client for Windows shells（argv 只收 ASCII）

        用法
          zcurl <METHOD> <URL> [key] [选项]      原始请求（body 走 stdin）
          zcurl registry                          GET  /agent/registry
          zcurl describe <实例> [format]           GET  /agent/describe
          zcurl get <实例> <path>                  GET  /agent/get
          zcurl set <实例> <path>                  POST /agent/set      body=JSON 值
          zcurl invoke <实例> <method>             POST /agent/invoke   body=JSON 数组
          zcurl action <实例> <action>             POST /appagent/actions/<action>  body=JSON 对象
          zcurl manifest <实例>                    GET  /appagent/manifest
          zcurl enc                                stdin → 百分号编码（ASCII）
          zcurl selftest                           本地回环字节保真自检（无外网）

        选项
          --body-env NAME   body 取自环境变量（宽字符；PowerShell 零配置首选；未设置即报错）
          --base URL        子命令基址，默认 $ZCURL_BASE 或 http://127.0.0.1:9090
          --key KEY         Bearer（A 面），默认 $ZCURL_KEY
          --key-b KEY       Bearer（B 面），默认 $ZCURL_KEY_B 或 --key
          -H "Name: Value"  追加请求头（可重复）
          --out FILE        响应写入文件（字节原样）；路径可百分号编码绕开 argv
          -v                请求行/请求头打到 stderr（key 脱敏）
          -h, --help        本帮助

        约定
          中文载荷：bash  printf '%s' "$J" | zcurl POST "$URL"
                    PS    $env:B='...'; zcurl POST $URL --body-env B
          中文 URL/参数：printf '%s' '中文' | zcurl enc
          已含 %XX 的值视为已编码，不会二次编码。
          退出码 0=2xx  1=HTTP/网络失败  2=用法或编码错误
        """;
}
