using System.Text;

namespace Zcurl;

/// <summary>解析后的命令行选项。所有值都已通过 ASCII 校验。</summary>
internal sealed class CliOptions
{
    public string? Sub;                    // registry/describe/get/set/invoke/action/manifest/enc/selftest/help；null=原始模式
    public readonly List<string> Pos = new();
    public string? BodyEnv;
    public string? BaseUrl;
    public string? Key;
    public string? KeyB;
    public string? OutFile;
    public readonly List<(string Name, string Value)> Headers = new();
    public bool Verbose;
    public string? Fatal;
}

/// <summary>body 的来源：无 / stdin 字节流 / 环境变量（宽字符）。</summary>
internal enum BodyKind { None, Stdin, Env }

/// <summary>纯逻辑（无网络、无输出副作用），供 Program 与单测共用。</summary>
internal static class ZcurlCli
{
    public const string DefaultBase = "http://127.0.0.1:9090";

    static readonly string[] SubNames =
        ["registry", "describe", "get", "set", "invoke", "action", "manifest", "enc", "selftest"];

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
                case "--base":
                case "--key":
                case "--key-b":
                case "--out":
                case "-H":
                    if (i + 1 >= args.Length) { o.Fatal = $"选项 {a} 缺少取值"; return o; }
                    string v = args[++i];
                    switch (a)
                    {
                        case "--body-env": o.BodyEnv = v; break;
                        case "--base": o.BaseUrl = v; break;
                        case "--key": o.Key = v; break;
                        case "--key-b": o.KeyB = v; break;
                        case "--out": o.OutFile = v; break;
                        default:
                            int colon = v.IndexOf(':');
                            if (colon <= 0) { o.Fatal = $"-H 需要 \"Name: Value\" 形式，收到 {v}"; return o; }
                            o.Headers.Add((v[..colon].Trim(), v[(colon + 1)..].Trim()));
                            break;
                    }
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) { o.Fatal = $"未知选项 {a}"; return o; }
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

    // ---------- 铁律 2：body 来源（显式 --body-env 优先，stdin 忽略） ----------

    public static BodyKind DecideBody(string? bodyEnv, bool stdinRedirected)
    {
        if (bodyEnv != null) return BodyKind.Env;
        return stdinRedirected ? BodyKind.Stdin : BodyKind.None;
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

    // ---------- 基址与 key ----------

    public static string BaseOf(CliOptions o)
        => (o.BaseUrl ?? Env("ZCURL_BASE") ?? DefaultBase).TrimEnd('/');

    public static string? KeyFor(CliOptions o, bool channelB)
        => channelB
            ? o.KeyB ?? Env("ZCURL_KEY_B") ?? o.Key ?? Env("ZCURL_KEY")
            : o.Key ?? Env("ZCURL_KEY");

    static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    // ---------- 子命令 URL 与载荷 ----------

    /// <summary>所有子命令的 query 一律经 MaybeEncode：ASCII 实例名（App#1）也会被正确转义成 %231。</summary>
    public static string DescribeUrl(string b, string name, string? format = null)
        => $"{b}/agent/describe?name={MaybeEncode(name)}&format={MaybeEncode(format ?? "md")}";

    public static string GetUrl(string b, string name, string path)
        => $"{b}/agent/get?name={MaybeEncode(name)}&path={MaybeEncode(path)}";

    public static string SetUrl(string b, string name)
        => $"{b}/agent/set?name={MaybeEncode(name)}";

    public static string InvokeUrl(string b, string name, string method)
        => $"{b}/agent/invoke?name={MaybeEncode(name)}&method={MaybeEncode(method)}";

    public static string ActionUrl(string b, string name, string action)
        => $"{b}/appagent/actions/{MaybeEncode(action)}?name={MaybeEncode(name)}";

    public static string ManifestUrl(string b, string name)
        => $"{b}/appagent/manifest?name={MaybeEncode(name)}";

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
