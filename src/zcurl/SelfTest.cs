using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Zcurl;

/// <summary>
/// 本地回环字节保真自检：起一个最小 HTTP 回显服务，用 stdin 与 --body-env 各起一个自身子进程各发一次，
/// 比对服务端收到的字节与 Content-Type。不访问外网。
/// </summary>
internal static class SelfTest
{
    private const string Payload =
        "{\"prompt\":\"一个女孩，赛博朋克\"}\n{\"第二行\":true}";

    public static async Task<int> RunAsync()
    {
        byte[] expected = Encoding.UTF8.GetBytes(Payload);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string url = $"http://127.0.0.1:{port}/echo";
        Console.Error.WriteLine($"selftest: loopback 127.0.0.1:{port}");

        var received = new List<(string Ct, byte[] Body)>();
        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
                received.Add(await HandleAsync(listener));
        });

        var (file, prefix) = SelfPath();
        var log = new StringBuilder();

        int stdinExit = await RunChildAsync(file, prefix, ["POST", url],
            stdin: expected, env: null, log);
        int envExit = await RunChildAsync(file, prefix, ["POST", url, "--body-env", "ZCURL_SELFTEST_BODY"],
            stdin: null, env: ("ZCURL_SELFTEST_BODY", Payload), log);

        try
        {
            await server.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"selftest: 回环服务失败 {ex.Message}");
            Console.Error.WriteLine(log.ToString());
            try { listener.Stop(); } catch { /* ignore */ }
            return 1;
        }
        listener.Stop();

        bool ok = true;
        ok &= Check("stdin 路径", stdinExit, received, 0, expected, log);
        ok &= Check("--body-env 路径", envExit, received, 1, expected, log);
        Console.Error.WriteLine(ok ? "selftest: ALL PASS (2/2)" : "selftest: FAIL");
        return ok ? 0 : 1;
    }

    private static bool Check(string label, int exit, List<(string Ct, byte[] Body)> received,
        int index, byte[] expected, StringBuilder log)
    {
        if (index >= received.Count)
        {
            Console.Error.WriteLine($"selftest: {label} FAIL（服务端只收到 {received.Count} 个请求，期望 {index + 1}）");
            Console.Error.WriteLine(log.ToString());
            return false;
        }

        var (ct, body) = received[index];
        bool okExit = exit == 0;
        bool okBytes = body.SequenceEqual(expected);
        bool okCt = ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
        bool ok = okExit && okBytes && okCt;
        Console.Error.WriteLine(
            $"selftest: {label,-16} {(ok ? "PASS" : "FAIL")}  exit={exit} ct={ct} {body.Length}B"
            + (okBytes ? "" : $"（期望 {expected.Length}B，字节不一致）"));
        if (!ok) Console.Error.WriteLine(log.ToString());
        return ok;
    }

    /// <summary>拿到可再启动自身的命令行（AOT/单文件→exe；dotnet zcurl.dll→dotnet）。</summary>
    private static (string File, List<string> Prefix) SelfPath()
    {
        string? p = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(p) && !Path.GetFileName(p).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
            return (p, []);
        // 以 `dotnet zcurl.dll` 启动时，dll 就在 BaseDirectory 下（AOT 单文件走不到这里）
        return ("dotnet", [Path.Combine(AppContext.BaseDirectory, "zcurl.dll")]);
    }

    private static async Task<int> RunChildAsync(string file, List<string> prefix, string[] argv,
        byte[]? stdin, (string Name, string Value)? env, StringBuilder log)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = stdin != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in prefix.Concat(argv)) psi.ArgumentList.Add(a);
        psi.Environment.Remove("ZCURL_SELFTEST_BODY");   // 清残留，避免误判
        if (env is { } e) psi.Environment[e.Name] = e.Value;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动子进程");
        if (stdin is not null)
        {
            await p.StandardInput.BaseStream.WriteAsync(stdin);
            await p.StandardInput.BaseStream.FlushAsync();
            p.StandardInput.Close();
        }

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        try
        {
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            log.AppendLine("子进程超时被杀");
            return -1;
        }

        log.AppendLine($"exit={p.ExitCode} stderr=[{(await errTask).Trim()}] stdout=[{await outTask}]");
        return p.ExitCode;
    }

    /// <summary>最小 HTTP/1.1 服务端：读头 → 读 Content-Length 字节体 → 回 200。</summary>
    private static async Task<(string Ct, byte[] Body)> HandleAsync(TcpListener listener)
    {
        using var conn = await listener.AcceptTcpClientAsync();
        using var stream = conn.GetStream();
        var buf = new byte[16 * 1024];
        int n = 0, headEnd = -1;
        while (headEnd < 0)
        {
            int r = await stream.ReadAsync(buf.AsMemory(n, buf.Length - n));
            if (r == 0) break;
            n += r;
            for (int i = 0; i + 3 < n; i++)
                if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                { headEnd = i; break; }
        }
        if (headEnd < 0) throw new InvalidOperationException("请求头不完整");

        string headText = Encoding.ASCII.GetString(buf, 0, headEnd);
        var lines = headText.Split("\r\n");

        if (headText.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));

        int contentLength = 0;
        string ct = "";
        foreach (var line in lines)
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
            else if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
                ct = line["Content-Type:".Length..].Trim();
        }

        var body = new byte[contentLength];
        int copied = Math.Min(contentLength, Math.Max(0, n - (headEnd + 4)));
        Buffer.BlockCopy(buf, headEnd + 4, body, 0, copied);
        while (copied < contentLength)
        {
            int r = await stream.ReadAsync(body.AsMemory(copied));
            if (r == 0) break;
            copied += r;
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        return (ct, body);
    }
}
