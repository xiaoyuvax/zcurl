using System.Text;
using System.Text.Json.Nodes;

namespace Zcurl;

/// <summary>解析后的命令行选项。所有值都已通过 ASCII 校验。</summary>
internal sealed class CliOptions
{
    public string? Sub;                    // registry/describe/state/get/set/invoke/action/manifest/enc/selftest/help；null=原始模式
    public readonly List<string> Pos = new();
    public string? BodyEnv;
    public string? BodyFile;                // --body-file <path>，与 BodyEnv 互斥
    public string? Profile;                 // --profile <app>：发现档案自动填 base/key
    public string? ProfileBase;             //   装配结果（Program 读档案后回填）
    public string? ProfileKey;
    public int? TimeoutMs;                  // --timeout <ms>
    public int? ExpectStatus;               // --expect <code>
    public string? BaseUrl;
    public string? Key;
    public string? KeyB;
    public string? OutFile;
    public readonly List<string> Fields = new();   // 只打印响应 JSON 的这些字段（可重复；支持点路径 a.b.0.c）
    public readonly List<(string Name, string Value)> Headers = new();
    public readonly List<(string Name, string Value)> Query = new();   // -q k=v，追加到任何子命令 URL
    public bool Verbose;
    public string? Fatal;
}

/// <summary>body 的来源：无 / stdin 字节流 / 环境变量（宽字符）/ 文件字节。</summary>
internal enum BodyKind { None, Stdin, Env, File }

/// <summary>纯逻辑（无网络、无输出副作用），供 Program 与单测共用。</summary>
internal static class ZcurlCli
{
    public const string DefaultBase = "http://127.0.0.1:9090";

    static readonly string[] SubNames =
        ["registry", "describe", "state", "get", "set", "invoke", "action", "manifest", "enc", "selftest"];

    // ---------- 铁律 1：argv 只收 ASCII ----------

    public static bool IsAscii(string s)
    {
        foreach (var c in s)
            if (c > 0x7F) return false;
        return true;
    }

    /// <summary>返回第一个含非 ASCII 字符的 argv 元素，全 ASCII 返回 null。</summary>
    public static string? FirstNonAscii(string[] args)
    {
        foreach (var a in args)
            if (!IsAscii(a)) return a;
        return null;
    }

    // ---------- 解析 ----------

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-h":
                case "--help":
                    o.Sub = "help";
                    return o;
                case "-v":
                    o.Verbose = true;
                    break;
                case "--body-env":
                case "--body-file":
                case "--profile":
                case "--timeout":
                case "--expect":
                case "--base":
                case "--key":
                case "--key-b":
                case "--out":
                case "--field":
                case "-H":
                case "-q":
                    if (i + 1 >= args.Length) { o.Fatal = $"选项 {a} 缺少取值"; return o; }
                    string v = args[++i];
                    switch (a)
                    {
                        case "--body-env": o.BodyEnv = v; break;
                        case "--body-file": o.BodyFile = v; break;
                        case "--profile": o.Profile = v; break;
                        case "--timeout":
                            if (!int.TryParse(v, out int ms) || ms <= 0)
                            { o.Fatal = $"--timeout 需要正整数毫秒，收到 {v}"; return o; }
                            o.TimeoutMs = ms;
                            break;
                        case "--expect":
                            if (!int.TryParse(v, out int code) || code is < 100 or > 599)
                            { o.Fatal = $"--expect 需要 100-599 的 HTTP 状态码，收到 {v}"; return o; }
                            o.ExpectStatus = code;
                            break;
                        case "--base": o.BaseUrl = v; break;
                        case "--key": o.Key = v; break;
                        case "--key-b": o.KeyB = v; break;
                        case "--out": o.OutFile = v; break;
                        case "--field": o.Fields.Add(v); break;
                        case "-q":
                            int eq = v.IndexOf('=');
                            if (eq <= 0) { o.Fatal = $"-q 需要 \"k=v\" 形式，收到 {v}"; return o; }
                            o.Query.Add((v[..eq], v[(eq + 1)..]));
                            break;
                        default:
                            int colon = v.IndexOf(':');
                            if (colon <= 0) { o.Fatal = $"-H 需要 \"Name: Value\" 形式，收到 {v}"; return o; }
                            o.Headers.Add((v[..colon].Trim(), v[(colon + 1)..].Trim()));
                            break;
                    }
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                    { o.Fatal = $"未知选项 {a}（zcurl 不是 curl，没有 {a}；用法见 zcurl -h）"; return o; }
                    if (a.Length > 1 && a[0] == '-')
                    { o.Fatal = $"未知选项 {a}（不支持 curl 的参数如 -X/-d/-o/--noproxy；用法见 zcurl -h）"; return o; }
                    if (o.Sub is null && Array.IndexOf(SubNames, a) >= 0) o.Sub = a;
                    else o.Pos.Add(a);
                    break;
            }
        }
        return o;
    }

    /// <summary>误把原始模式写成小写方法（zcurl get http://...）时退回原始模式。</summary>
    public static void UnshiftIfUrl(CliOptions o)
    {
        if (o.Sub is null || o.Sub is "enc" or "selftest" or "help") return;
        if (o.Pos.Count > 0 && IsHttpUrl(o.Pos[0])) { o.Pos.Insert(0, o.Sub); o.Sub = null; }
    }

    public static bool IsHttpUrl(string s)
        => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // ---------- 铁律 2：body 来源（显式 --body-env/--body-file 互斥且优先于 stdin） ----------

    /// <summary>决定 body 来源。env 与 file 同时给出 → Fatal（显式来源不并存）；file 压过 stdin。</summary>
    public static (BodyKind Kind, string? Fatal) DecideBody(string? bodyEnv, string? bodyFile, bool stdinRedirected)
    {
        if (bodyEnv != null && bodyFile != null)
            return (BodyKind.None, "--body-env 与 --body-file 互斥（body 只能有一个显式来源）");
        if (bodyFile != null) return (BodyKind.File, null);
        if (bodyEnv != null) return (BodyKind.Env, null);
        return (stdinRedirected ? BodyKind.Stdin : BodyKind.None, null);
    }

    // ---------- 百分号编码（中文只能这样进 argv） ----------

    public static string Encode(string s) => Uri.EscapeDataString(s ?? "");

    /// <summary>值里已有 %XX 转义则视为已编码，避免二次编码。</summary>
    public static bool LooksEncoded(string s)
    {
        for (int i = 0; i + 2 < s.Length; i++)
            if (s[i] == '%' && IsHex(s[i + 1]) && IsHex(s[i + 2])) return true;
        return false;
    }

    static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    public static string MaybeEncode(string s) => LooksEncoded(s) ? s : Encode(s);

    /// <summary>已是 %XX 转义形态才解码，普通路径原样返回。</summary>
    public static string MaybeDecode(string s) => LooksEncoded(s) ? Uri.UnescapeDataString(s) : s;

    /// <summary>原始字节 → 百分号编码（zcurl enc）。仅转义非 unreserved 字符。</summary>
    public static string PercentEncode(ReadOnlySpan<byte> bytes)
    {
        const string Hex = "0123456789ABCDEF";
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (byte b in bytes)
        {
            bool unreserved = b >= 'A' && b <= 'Z' || b >= 'a' && b <= 'z'
                || b >= '0' && b <= '9' || b == '-' || b == '.' || b == '_' || b == '~';
            if (unreserved) sb.Append((char)b);
            else sb.Append('%').Append(Hex[b >> 4]).Append(Hex[b & 15]);
        }
        return sb.ToString();
    }

    // ---------- 基址与 key（优先级：显式 flag > --profile 档案 > 环境变量 > 默认） ----------

    public static string BaseOf(CliOptions o)
        => (o.BaseUrl ?? o.ProfileBase ?? Env("ZCURL_BASE") ?? DefaultBase).TrimEnd('/');

    public static string? KeyFor(CliOptions o, bool channelB)
        => channelB
            ? o.KeyB ?? o.Key ?? o.ProfileKey ?? Env("ZCURL_KEY_B") ?? Env("ZCURL_KEY")
            : o.Key ?? o.ProfileKey ?? Env("ZCURL_KEY");

    static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    // ---------- 发现档案（--profile）：解析与选择是纯函数，文件/进程 IO 在 Program ----------

    /// <summary>档案候选。Error=解析/读取失败文案；PidAlive 由 Program 查进程。</summary>
    public readonly record struct ProfileCandidate(
        string Path, string? Endpoint, string? Key, int? Pid, bool PidAlive,
        DateTime LastWriteUtc, string? Error)
    {
        public bool Usable => Error is null && Endpoint is not null && Key is not null && PidAlive;
        public string Why => Error ?? (PidAlive ? "可用" : $"pid {Pid} 已死");
    }

    /// <summary>解析档案 JSON → (endpoint, key, pid) 或错误文案。</summary>
    public static (string? Endpoint, string? Key, int? Pid, string? Error) ParseProfileJson(string json)
    {
        JsonNode? n;
        try { n = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException ex) { return (null, null, null, $"JSON 解析失败: {ex.Message}"); }
        if (n is not JsonObject o) return (null, null, null, "不是 JSON 对象");
        var ep = (string?)o["endpoint"];
        var key = (string?)o["key"];
        int? pid = o["pid"] is JsonValue pv && pv.TryGetValue<int>(out var p) && p > 0 ? p : null;
        if (string.IsNullOrEmpty(ep)) return (null, null, pid, "缺 endpoint 字段");
        if (!IsHttpUrl(ep)) return (null, null, pid, $"endpoint 不是 http(s): {ep}");
        if (string.IsNullOrEmpty(key)) return (null, null, pid, "缺 key 字段");
        if (pid is null) return (null, null, null, "缺 pid 字段（无法确认存活）");
        return (ep, key, pid, null);
    }

    /// <summary>可用（解析成功且 pid 存活）候选里 mtime 最新者；无 → null。</summary>
    public static ProfileCandidate? SelectProfile(IReadOnlyList<ProfileCandidate> candidates)
    {
        ProfileCandidate? best = null;
        foreach (var c in candidates)
            if (c.Usable && (best is null || c.LastWriteUtc > best.Value.LastWriteUtc))
                best = c;
        return best;
    }

    // ---------- --expect 退出码语义 ----------

    /// <summary>未指定 expect：2xx → 0。指定：匹配 → 0（非 2xx 也算成功），不匹配 → 1。</summary>
    public static int ExpectExit(int status, int? expect)
        => expect is { } e
            ? (status == e ? 0 : 1)
            : status is >= 200 and <= 299 ? 0 : 1;

    // ---------- 子命令 URL 与载荷 ----------

    /// <summary>所有子命令的 query 一律经 MaybeEncode：ASCII 实例名（App#1）也会被正确转义成 %231。</summary>
    public static string DescribeUrl(string b, string name, string? format = null)
        => $"{b}/agent/describe?name={MaybeEncode(name)}&format={MaybeEncode(format ?? "md")}";

    public static string GetUrl(string b, string name, string path)
        => $"{b}/agent/get?name={MaybeEncode(name)}&path={MaybeEncode(path)}";

    public static string StateUrl(string b, string name)
        => $"{b}/agent/state?name={MaybeEncode(name)}";

    public static string SetUrl(string b, string name)
        => $"{b}/agent/set?name={MaybeEncode(name)}";

    public static string InvokeUrl(string b, string name, string method)
        => $"{b}/agent/invoke?name={MaybeEncode(name)}&method={MaybeEncode(method)}";

    public static string ActionUrl(string b, string name, string action)
        => $"{b}/appagent/actions/{MaybeEncode(action)}?name={MaybeEncode(name)}";

    public static string ManifestUrl(string b, string name)
        => $"{b}/appagent/manifest?name={MaybeEncode(name)}";

    /// <summary>把 -q k=v 追加到 URL：已有 query 用 &amp; 接续，k/v 各自 MaybeEncode（已编码不二次编码）。</summary>
    public static string ApplyQuery(string url, IReadOnlyList<(string Name, string Value)> query)
    {
        if (query.Count == 0) return url;
        var sb = new StringBuilder(url);
        sb.Append(url.Contains('?') ? '&' : '?');
        for (int i = 0; i < query.Count; i++)
        {
            if (i > 0) sb.Append('&');
            sb.Append(MaybeEncode(query[i].Name)).Append('=').Append(MaybeEncode(query[i].Value));
        }
        return sb.ToString();
    }

    /// <summary>set：body 是一个 JSON 值，包成 {path,value}。</summary>
    public static string ComposeSetBody(string path, ReadOnlySpan<byte> body)
    {
        var value = body.IsEmpty ? null : Json.ParseUtf8(body);
        return Json.Obj(("path", path), ("value", value));
    }

    /// <summary>invoke：body 必须是 JSON 数组（空 → []）。</summary>
    public static string ComposeInvokeBody(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) return "[]";
        var node = Json.ParseUtf8(body);
        if (!Json.IsArray(node)) throw new FormatException("invoke 的 body 必须是 JSON 数组，如 [\"MoveWindowAction\",10,20]");
        return Json.Raw(node);
    }

    /// <summary>action：body 必须是 JSON 对象（空 → {}），自动补 callId。</summary>
    public static string ComposeActionBody(ReadOnlySpan<byte> body)
    {
        var args = body.IsEmpty ? null : Json.ParseUtf8(body);
        if (args is not null && !Json.IsObject(args))
            throw new FormatException("action 的 body 必须是 JSON 对象，如 {\"folderPath\":\"C:/work\"}");
        return Json.Obj(("args", args), ("callId", Guid.NewGuid().ToString("N")));
    }

    // ---------- 响应字段提取（--field） ----------

    /// <summary>取响应 JSON 的单字段：字符串去引号原样输出，其余输出紧凑 JSON。
    /// 支持点路径 a.b.0.c（数段索引数组）；根对象/根数组的字面全名优先（旧语义不变，兼容带点的字面 key）。
    /// 非 JSON / 路径不存在 / 值为 null → null（调用方给一行提示）。</summary>
    public static string? ExtractField(string json, string field)
    {
        if (string.IsNullOrEmpty(field)) return null;
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }
        if (node is JsonObject o)
        {
            if (o.ContainsKey(field)) return Take(o[field]);       // 字面全名（旧语义）
            return Navigate(o, field.Split('.'));
        }
        if (node is JsonArray a)
        {
            foreach (var e in a)                                    // 旧语义：首个含字面全名的元素
                if (e is JsonObject eo && eo.ContainsKey(field)) return Take(eo[field]);
            var segs = field.Split('.');                            // 新语义：点路径
            foreach (var e in a)
                if (e is JsonObject eo && eo.ContainsKey(segs[0]))
                    return Navigate(eo, segs);
        }
        return null;

        static string? Navigate(JsonNode cur, string[] segs)
        {
            foreach (var seg in segs)
            {
                switch (cur)
                {
                    case JsonObject o:
                        if (!o.TryGetPropertyValue(seg, out var child) || child is null) return null;
                        cur = child;
                        break;
                    case JsonArray a:
                        if (!int.TryParse(seg, out int idx) || idx < 0 || idx >= a.Count) return null;
                        var e = a[idx];
                        if (e is null) return null;
                        cur = e;
                        break;
                    default:
                        return null;
                }
            }
            return Take(cur);
        }

        static string? Take(JsonNode? v) => v switch
        {
            null => null,
            JsonValue jv when jv.TryGetValue<string>(out var s) => s,
            _ => v.ToJsonString(),
        };
    }
}

/// <summary>System.Text.Json DOM 封装（NativeAOT 安全，不用反射序列化）。</summary>
internal static class Json
{
    public static object? ParseUtf8(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(utf8)
                ?? throw new FormatException("body 不是合法 JSON（内容是 null）");
        }
        catch (System.Text.Json.JsonException ex)
        {
            // 统一成 FormatException：Program 只需一种 catch，用户看到的是干净的一行提示
            throw new FormatException($"body 不是合法 JSON: {ex.Message}");
        }
    }

    public static bool IsArray(object? node) => node is System.Text.Json.Nodes.JsonArray;
    public static bool IsObject(object? node) => node is System.Text.Json.Nodes.JsonObject;

    public static string Raw(object? node)
        => (node as System.Text.Json.Nodes.JsonNode)?.ToJsonString() ?? "null";

    public static string Obj(params (string Key, object? Value)[] props)
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var (k, v) in props)
        {
            o[k] = v switch
            {
                null => null,
                System.Text.Json.Nodes.JsonNode n => n,
                string s => System.Text.Json.Nodes.JsonValue.Create(s),
                _ => System.Text.Json.Nodes.JsonValue.Create(v?.ToString()),
            };
        }
        return o.ToJsonString();
    }
}
