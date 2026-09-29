# zcurl

**A UTF-8-safe HTTP client for Windows shells** — a single-file NativeAOT `zcurl.exe` meant to replace `curl` in agent scripts.

In one line: **ASCII-only argv; Chinese payloads go through stdin or an environment variable; bytes preserved end to end.**

## Download

Single-file `zcurl.exe` (**3.70 MB**, NativeAOT, no runtime dependencies) — drop it on your `PATH`:

```powershell
Invoke-WebRequest 'https://github.com/xiaoyuvax/zcurl/releases/download/v0.2.0/zcurl.exe' -OutFile .\zcurl.exe
.\zcurl.exe selftest                        # loopback self-test, never touches the network
Get-FileHash .\zcurl.exe -Algorithm SHA256  # compare against zcurl.exe.sha256
```

- Releases: <https://github.com/xiaoyuvax/zcurl/releases/latest>
- Source: <https://github.com/xiaoyuvax/zcurl> ｜ mirror <https://gitee.com/xiaoyuvax/zcurl>

## Why

Every route for sending Chinese JSON on Windows is broken:

| Route | Measured result |
|---|---|
| Git Bash `curl` + argv | Chinese turns into GBK: `{"prompt":"\xd2\xbb\xb8\xf6..."}` |
| PowerShell 5.1 argv | **Quotes swallowed**: `{"a":"b"}` → `{a:b}` |
| PowerShell pipe (default `$OutputEncoding=ASCII`) | Chinese becomes `????` |
| `curl -d` (from a PS pipe) | Newlines eaten: `a\nb\nc` → `abc` |

zcurl moves those traps **behind a tool boundary**: any non-ASCII argument is rejected immediately (`exit 2`), and Chinese is only allowed in through a byte channel, passed through untouched.

## Rules

1. **argv accepts ASCII only.** Anything else → immediate `exit 2` with the recommended alternative (stdin / `--body-env` / `zcurl enc`).
2. **Body travels as bytes:** stdin (default) or `--body-env NAME` (wide-char environment variable, zero-config on PowerShell).
3. **stdout = raw response bytes, stderr = status line.** Never mixed, never re-encoded. Request and response bodies are streamed straight through (`ResponseHeadersRead` + `CopyToAsync`), so peak memory is 1× the response size.

## Build

```powershell
dotnet build -c Release                 # compile
dotnet publish -c Release -r win-x64    # single-file AOT exe → src\zcurl\bin\Release\net10.0\win-x64\publish\zcurl.exe (3.70 MB)
dotnet test                             # unit tests (pure logic, no network)
```

Requires .NET 10 SDK plus the C++ AOT workload.

Publish switches (`zcurl.csproj`): `OptimizationPreference=Size` plus disabled diagnostics activity / EventSource / metadata updater / debugger / exception stack traces. Measured **smaller and faster** than the `Speed` configuration: 3.70 MB vs 4.87 MB, startup 32 ms vs 34 ms, loopback request 39 vs 44 ms (`bench.ps1`). Trade-off: crashes report the exception message without a stack — every exception is caught in-process and rendered as a single line anyway.

What is left is the HTTPS client itself and cannot be cut: measured, `System.Net.Http` (sockets + TLS) is 1.80 MB (46%) of the exe, `selftest` 0.30 MB, the `System.Text.Json` DOM 0.15 MB, and the AOT runtime floor 0.87 MB. Swapping in `HttpClientHandler` makes it 0.26 MB *bigger*, and the native Windows WinHTTP switch measured as a no-op.

## Usage

```text
zcurl <METHOD> <URL> [key] [options]    raw request (body via stdin)
zcurl registry                          GET  /agent/registry
zcurl describe <instance> [format]      GET  /agent/describe
zcurl get <instance> <path>             GET  /agent/get
zcurl set <instance> <path>             POST /agent/set      body = a JSON value
zcurl invoke <instance> <method>        POST /agent/invoke   body = a JSON array
zcurl action <instance> <action>        POST /appagent/actions/<action>  body = a JSON object
zcurl manifest <instance>               GET  /appagent/manifest
zcurl enc                               stdin → percent-encoded ASCII
zcurl selftest                          loopback byte-fidelity self-test (no network)
```

Options: `--body-env NAME`, `--base URL`, `--key KEY`, `--key-b KEY`, `-H "Name: Value"` (repeatable), `--out FILE`, `-v`, `-h`.
Base URL defaults to `http://127.0.0.1:9090` (`--base` / `ZCURL_BASE`), keys to `ZCURL_KEY` / `ZCURL_KEY_B`.

### Two supported ways to send Chinese payloads

**Git Bash** (stdin byte stream):

```bash
J='{"prompt":"a girl, cyberpunk"}'
printf '%s' "$J" | zcurl POST "http://127.0.0.1:9090/x" --out resp.bin
```

**PowerShell** (wide-char environment variable, no pipe needed):

```powershell
$env:BODY = '{"prompt":"一个女孩，赛博朋克"}'
zcurl POST 'http://127.0.0.1:9090/x' --body-env BODY --out resp.bin
```

When `--body-env` is given, stdin is ignored; an unset variable is a hard error (`exit 2`) so a stale payload can never be sent by accident.

### Non-ASCII URLs / parameters: encode first

```bash
printf '%s' '中文' | zcurl enc        # → %E4%B8%AD%E6%96%87 (pure ASCII)
zcurl get 'App#1' '%E4%B8%AD%E6%96%87'
```

Values that already contain `%XX` are treated as encoded and are never double-encoded; `--out` paths are decoded by the same rule.

### Exit codes

| code | meaning |
|---|---|
| 0 | 2xx |
| 1 | non-2xx HTTP status or network failure |
| 2 | usage error / non-ASCII argv / invalid JSON body / missing env var |

## Versus curl

| Case | curl | zcurl |
|---|---|---|
| Chinese JSON from PowerShell | quotes swallowed / mojibake | `--body-env` just works |
| Chinese JSON from bash | argv becomes GBK | `printf '%s' "$J" \| zcurl ...` |
| Body containing newlines | `-d` eats them | stdin bytes untouched |
| Chinese response | must tweak `[Console]::OutputEncoding` | `--out file`, console untouched |
| File upload / multipart | supported | **not supported** (out of scope for v0.2) |

## Puppet.Core support

[Puppet.Core](https://github.com/xiaoyuvax/PuppetCore) is the .NET library host apps use to expose themselves as agent-operable objects (instances / properties / methods / actions). zcurl ships first-class subcommands for its `/agent/*` (channel A) and `/appagent/*` (channel B) endpoints, so you never hand-build the URLs or JSON:

```bash
zcurl registry
zcurl describe 'App#1'
zcurl get 'App#1' 'Display.Text'
printf '%s' '"new value"' | zcurl set 'App#1' 'Display.Text'
printf '%s' '["MoveAction",10,20]' | zcurl invoke 'App#1' 'MoveAction'
printf '%s' '{"folderPath":"C:/work"}' | zcurl action 'App#1' 'OpenAction'   # channel B, uses ZCURL_KEY_B
zcurl manifest 'App#1'
```

Low-frequency endpoints such as `control` / `state` / `logs` are available through raw mode.

## Self-test

```bash
zcurl selftest    # spins up a loopback server, sends once via stdin and once via --body-env, compares bytes
```

## Known limits

- No file upload / multipart / cookie jar / proxy (proxies are explicitly disabled — this tool targets loopback and LAN endpoints).
- `--body-env` is bounded by the Windows environment block (~32K chars); use stdin for larger payloads.
- A PowerShell string pipe appends `\r\n` and may prepend a BOM; do not use it when you need exact bytes.
- When a response has no `Content-Length` (chunked), the stderr status line shows `?B` for the byte count (`--out` still reports the exact file size).

## Byte-fidelity matrix (measured)

Measured against a local echo server, all 9 cases pass (PowerShell 5.1 + Git Bash); payload = `{"prompt":"一个女孩，赛博朋克"}\n{"第二行":true}` (59 B):

| Case | Result |
|---|---|
| PowerShell → stdin (exact bytes) | ✅ 59B exact |
| PowerShell → `--body-env` (Chinese env var) | ✅ 59B exact |
| PowerShell pipe + `$OutputEncoding=UTF8` | ⚠️ 64B = PS 5.1's own BOM (3) + 59B + `\r\n` (2), passed through untouched |
| PowerShell pipe, default encoding | ⚠️ 37B, Chinese → `?` (negative control, by design) |
| PowerShell argv with Chinese | ✅ rejected, exit 2, no request sent |
| Empty-body POST | ✅ `Content-Length: 0`, no `Content-Type` |
| Git Bash → stdin | ✅ 59B exact |
| Git Bash → `--body-env` | ✅ 59B exact |
| Git Bash argv with Chinese | ✅ rejected, exit 2, no request sent |

> Need exact bytes? Avoid PowerShell string pipes (they change the encoding, may add a BOM and append `\r\n`) — use `--body-env`, or `cmd /c "type file | zcurl ..."`.

## Install as an agent skill

[`SKILL.md`](SKILL.md) at the repo root is the agent-facing skill doc: when to use it, the three rules, payload recipes, Puppet subcommands, exit codes and pitfalls. Copy it into your agent's skills directory:

```powershell
# opencode
$d = "$env:USERPROFILE\.config\opencode\skills\zcurl"; New-Item -ItemType Directory -Force $d | Out-Null
Copy-Item SKILL.md "$d\SKILL.md"

# Claude Code
$d = "$env:USERPROFILE\.claude\skills\zcurl"; New-Item -ItemType Directory -Force $d | Out-Null
Copy-Item SKILL.md "$d\SKILL.md"
```

Skill lists load at session start — open a new session afterwards. Verify with `zcurl selftest` (`ALL PASS (2/2)` expected).

## License

MIT
