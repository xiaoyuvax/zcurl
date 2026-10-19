// zcurl — UTF-8 安全的 HTTP 客户端（Windows shell 专用）
//
// 铁律：
//   1. argv 只收 ASCII。中文/任意非 ASCII 一律走 stdin 字节流或 --body-env（宽字符环境变量）。
//   2. URL 与子命令参数里的非 ASCII 必须先百分号编码（printf '%s' 中文 | zcurl enc）。
//   3. stdout = 原始响应字节；stderr = 状态行；exit 0=2xx，1=HTTP/网络失败，2=用法或编码错误。
using System.Diagnostics;
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

// ---------- --profile：发现档案填 base/key（flag > profile > 环境变量 > 默认） ----------
if (opt.Profile is not null)
{
    var perr = LoadProfile(opt);
    if (perr is not null) return Fail(perr);
}

// ---------- 发现档案：glob + 解析 + pid 活性（选择逻辑在 ZcurlCli.SelectProfile） ----------
string? LoadProfile(CliOptions o)
{
    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Puppet.AppAgents");
    string fullPattern = o.Profile + ".*.json";
    string[] files;
    try { files = Directory.Exists(dir) ? Directory.GetFiles(dir, fullPattern) : []; }
    catch (Exception ex) { return $"读取档案目录失败 {dir}: {ex.Message}"; }

    if (files.Length == 0)
        return $"--profile {o.Profile}: 未找到档案 {Path.Combine(dir, fullPattern)}（宿主未启动或未注册？先启动应用让 Agent 落档案）";

    var cands = new List<ZcurlCli.ProfileCandidate>();
    foreach (var f in files)
    {
        DateTime mtime;
        try { mtime = File.GetLastWriteTimeUtc(f); }
        catch { mtime = DateTime.MinValue; }

        string? json = null, readErr = null;
        try { json = File.ReadAllText(f); }
        catch (Exception ex) { readErr = $"读取失败: {ex.Message}"; }

        if (readErr is not null)
        {
            cands.Add(new(f, null, null, null, false, mtime, readErr));
            continue;
        }
        var (ep, key, pid, perr) = ZcurlCli.ParseProfileJson(json!);
        cands.Add(new(f, ep, key, pid, pid is { } p && IsPidAlive(p), mtime, perr));
    }

    var win = ZcurlCli.SelectProfile(cands);
    if (win is null)
    {
        var detail = string.Join("；", cands.Select(c => $"{Path.GetFileName(c.Path)} → {c.Why}"));
        return $"--profile {o.Profile}: {cands.Count} 个档案全部不可用（需解析成功且 pid 存活）: {detail}";
    }
    o.ProfileBase = win.Value.Endpoint;
    o.ProfileKey = win.Value.Key;
    if (o.Verbose)
    {
        Console.Error.WriteLine($"profile: {win.Value.Path} → {win.Value.Endpoint}");
        foreach (var c in cands)
            if (c.Path != win.Value.Path)
                Console.Error.WriteLine($"  落选 {Path.GetFileName(c.Path)} → {c.Why}");
    }
    return null;

    static bool IsPidAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}

// ---------- 载荷读取 ----------
async Task<(ReadOnlyMemory<byte> Body, BodyKind Kind, string? Error)> ReadBodyAsync(CliOptions o)
{
    var (kind, fatal) = ZcurlCli.DecideBody(o.BodyEnv, o.BodyFile, Console.IsInputRedirected);
    if (fatal is not null) return (default, BodyKind.None, fatal);

    if (kind == BodyKind.File)
    {
        var path = ZcurlCli.MaybeDecode(o.BodyFile!);
        try { return (File.ReadAllBytes(path), BodyKind.File, null); }
        catch (Exception ex) { return (default, BodyKind.None, $"读取 --body-file 失败 {path}: {ex.Message}"); }
    }

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
        var bytes = ms.GetBuffer().AsMemory(0, (int)ms.Length);
        // stdin 载荷必须是 UTF-8。shell 管道若按 GBK/ANSI 编码写入（cmd chcp 936、$OutputEncoding 非 UTF8），
        // 服务端会收到乱码——这里只告警不拦截（stdin 也允许原始字节），提示改用 --body-env/--body-file。
        if (bytes.Length > 0 && !System.Text.Unicode.Utf8.IsValid(bytes.Span))
            Console.Error.WriteLine("zcurl: 警告：stdin 载荷非合法 UTF-8（疑似管道按 GBK/ANSI 编码写入，服务端将收到乱码）。中文载荷请改用 --body-env 或 --body-file。");
        return (bytes, BodyKind.Stdin, null);  // 零拷贝：直接借用缓冲区
    }

    return (default, BodyKind.None, null);
}

// ---------- HTTP ----------
async Task<int> SendAsync(string method, string url, string? key, ReadOnlyMemory<byte> body, CliOptions o)
{
    url = ZcurlCli.ApplyQuery(url, o.Query);   // -q k=v：子命令与原始模式通用
    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, Proxy = null });
    if (o.TimeoutMs is { } tmo) client.Timeout = TimeSpan.FromMilliseconds(tmo);
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

        // Puppet 的密钥错误伪装成 404（不是 401），排障第一步就是查 key —— 免得被当成"路由不存在"
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            Console.Error.WriteLine("  提示: 404 常是 Bearer 密钥不对（Puppet 密钥错误返回 404 而非 401）→ 检查 --key / $ZCURL_KEY");
        else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            Console.Error.WriteLine("  提示: 401 是 B 面 /appagent/* 密钥不对（credential-invalid）→ 检查 --key-b / $ZCURL_KEY_B");

        if (o.OutFile is not null)
        {
            var path = ZcurlCli.MaybeDecode(o.OutFile);
            using (var f = File.Create(path))
                await resp.Content.CopyToAsync(f);
            if (o.Verbose) Console.Error.WriteLine($"[{(int)resp.StatusCode}] {new FileInfo(path).Length}B -> {path}");
        }
        else if (o.Fields.Count > 0)
        {
            using var ms = new MemoryStream();
            await resp.Content.CopyToAsync(ms);
            var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            var parts = new List<string>(o.Fields.Count);
            foreach (var f in o.Fields)
            {
                var field = ZcurlCli.ExtractField(text, f);
                if (field is null)
                {
                    Console.Error.WriteLine($"zcurl: 字段 {f} 不存在或响应不是 JSON");
                    Console.Error.WriteLine(text.Length > 2048 ? text[..2048] : text);
                    return 1;
                }
                parts.Add(field);
            }
            var outBytes = Encoding.UTF8.GetBytes(string.Join("\n", parts));
            using (var stdout = Console.OpenStandardOutput())
                await stdout.WriteAsync(outBytes);
            if (o.Verbose) Console.Error.WriteLine($"[{(int)resp.StatusCode}] fields:{string.Join(",", o.Fields)} {outBytes.Length}B");
        }
        else
        {
            using var stdout = Console.OpenStandardOutput();
            await resp.Content.CopyToAsync(stdout);
            if (o.Verbose) Console.Error.WriteLine($"[{(int)resp.StatusCode}] {size}");
        }
        int status = (int)resp.StatusCode;
        if (o.ExpectStatus is { } exp && status != exp)
            Console.Error.WriteLine($"expect {exp}, got {status}");
        return ZcurlCli.ExpectExit(status, o.ExpectStatus);
    }
    catch (TaskCanceledException) when (o.TimeoutMs is not null)
    {
        Console.Error.WriteLine($"zcurl: 请求超时（{o.TimeoutMs}ms）");
        return 1;
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

    case "state":
        if (opt.Pos.Count != 1) return Fail("用法: zcurl state <实例>");
        return await SendAsync("GET", ZcurlCli.StateUrl(ZcurlCli.BaseOf(opt), opt.Pos[0]),
            ZcurlCli.KeyFor(opt, false), default, opt);

    case "get":
        if (opt.Pos.Count != 2) return Fail("用法: zcurl get <实例> <path>");
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
          zcurl state <实例>                       GET  /agent/state
          zcurl get <实例> <path>                  GET  /agent/get
          zcurl set <实例> <path>                  POST /agent/set      body=JSON 值
          zcurl invoke <实例> <method>             POST /agent/invoke   body=JSON 数组
          zcurl action <实例> <action>             POST /appagent/actions/<action>  body=JSON 对象
          zcurl manifest <实例>                    GET  /appagent/manifest
          zcurl enc                                stdin → 百分号编码（ASCII）
          zcurl selftest                           本地回环字节保真自检（无外网）

        选项
          --body-env NAME   body 取自环境变量（宽字符；PowerShell 零配置首选；未设置即报错）
          --body-file FILE  body 取自文件字节（与 --body-env 互斥；路径可百分号编码；大载荷首选，绕开环境变量 32KB 上限）
          --profile APP     自动发现 base/key：%LOCALAPPDATA%\Puppet.AppAgents\APP.*.json（解析成功且 pid 存活取最新）
          --timeout MS      请求超时（正整数毫秒；超时 exit 1）
          --expect CODE     期望状态码（100-599）：匹配 exit 0（非 2xx 也算成功），不匹配 exit 1
          --base URL        子命令基址，默认 $ZCURL_BASE 或 http://127.0.0.1:9090
          --key KEY         Bearer（A 面），默认 $ZCURL_KEY
          --key-b KEY       Bearer（B 面），默认 $ZCURL_KEY_B 或 --key
          --field NAME      只打印响应 JSON 的该字段（可重复=多字段逐行；字符串去引号；支持点路径 a.b.0.c）
          -H "Name: Value"  追加请求头（可重复）
          -q k=v            追加 URL query（可重复；子命令与原始模式通用，registry 过滤 / logs maxLen 等）
          --out FILE        响应写入文件（字节原样）；路径可百分号编码绕开 argv
          -v                请求行/请求头/状态行打到 stderr（key 脱敏；--profile 落选档案也列出）；不加则 stderr 静默
          -h, --help        本帮助

        body 形状（三套，写错会 exit 2）
          set      = JSON 值      printf '%s' '"新值"'      | zcurl set <实例> <path>
          invoke   = JSON 数组    printf '%s' '["MoveAction",10,20]' | zcurl invoke <实例> <方法>
          action   = JSON 对象    printf '%s' '{"folderPath":"C:/work"}' | zcurl action <实例> <名>（B 面）
          大载荷用 --body-file f.json（PS 环境变量有 32,767 字符上限）；--body-env 与 --body-file 互斥。

        约定
          取值优先级：显式 flag > --profile 档案 > $ZCURL_* 环境变量 > 默认。
          中文载荷：bash  printf '%s' "$J" | zcurl POST "$URL"
                    PS    $env:B='...'; zcurl POST $URL --body-env B
          中文 URL/参数：printf '%s' '中文' | zcurl enc
          已含 %XX 的值视为已编码，不会二次编码。
          不是 curl：不支持 -X/-d/-o/--noproxy 等参数（未知选项直接 exit 2）；代理一律直连（UseProxy=false）。
          PS 控制台读中文响应先设 [Console]::OutputEncoding = [Text.Encoding]::UTF8，否则显示为 GBK 乱码。
          404 常是 Bearer 密钥不对（Puppet 密钥错误返回 404 而非 401），先查 --key / $ZCURL_KEY。
          B 面（/appagent/*）密钥错返回 401 credential-invalid，查 --key-b / $ZCURL_KEY_B。
          stderr 默认静默（仅 -v 或出错/提示时才有输出），避免 PS 5.1 把状态行当 NativeCommandError。
          退出码 0=2xx 或 --expect 匹配  1=HTTP/网络失败/字段缺失/expect 不匹配/超时  2=用法或编码错误
        """;
}
