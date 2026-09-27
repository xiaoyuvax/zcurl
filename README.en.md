# zcurl

**A UTF-8-safe HTTP client for Windows shells** — a single-file NativeAOT `zcurl.exe` meant to replace `curl` in agent scripts.

In one line: **ASCII-only argv; Chinese payloads go through stdin or an environment variable; bytes preserved end to end.**

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
3. **stdout = raw response bytes, stderr = status line.** Never mixed, never re-encoded.

## Build

```powershell
dotnet build -c Release                 # compile
dotnet publish -c Release -r win-x64    # single-file AOT exe → src\zcurl\bin\Release\net10.0\win-x64\publish\zcurl.exe (~4 MB)
dotnet test                             # unit tests (pure logic, no network)
```

Requires .NET 10 SDK plus the C++ AOT workload.

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
| File upload / multipart | supported | **not supported** (out of scope for v0.1) |

## Puppet subcommands

Hosts expose `/agent/*` (channel A) and `/appagent/*` (channel B) via `Puppet.Core`; see the Puppet section of the Chinese README for examples. Low-frequency endpoints such as `control` / `state` / `logs` are available through raw mode.

## Self-test

```bash
zcurl selftest    # spins up a loopback server, sends once via stdin and once via --body-env, compares bytes
```

## Known limits

- No file upload / multipart / cookie jar / proxy (proxies are explicitly disabled — this tool targets loopback and LAN endpoints).
- `--body-env` is bounded by the Windows environment block (~32K chars); use stdin for larger payloads.
- A PowerShell string pipe appends `\r\n`; do not use it when you need exact bytes.

## License

MIT
