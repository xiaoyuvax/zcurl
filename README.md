# zcurl

**Windows shell 专用的 UTF-8 安全 HTTP 客户端**，单文件 NativeAOT exe，给 agent / 脚本替代 `curl`。

一句话：**argv 只收 ASCII；中文载荷走 stdin 或环境变量；字节逐字节保真。**

## 下载

单文件 `zcurl.exe`（**4.40 MB**，NativeAOT，无运行时依赖），放进 PATH 即用：

```powershell
 Invoke-WebRequest 'https://github.com/xiaoyuvax/zcurl/releases/download/v0.6.0/zcurl.exe' -OutFile .\zcurl.exe
.\zcurl.exe selftest                                     # 本地回环自检，不访问外网
Get-FileHash .\zcurl.exe -Algorithm SHA256               # 与 Release 页 SHA256 比对
```

- 下载页：<https://github.com/xiaoyuvax/zcurl/releases/latest>
- 源码：<https://github.com/xiaoyuvax/zcurl> ｜ 镜像 <https://gitee.com/xiaoyuvax/zcurl>

## 为什么需要它

Windows 上把中文 JSON 送出去，每条路都坑：

| 途径 | 实测结果 |
|---|---|
| Git Bash 的 curl + argv | 中文变 GBK：`{"prompt":"\xd2\xbb\xb8\xf6..."}` |
| PowerShell 5.1 argv | **引号被吞**：`{"a":"b"}` → `{a:b}` |
| PowerShell 管道（默认 `$OutputEncoding=ASCII`） | 中文变 `????` |
| `curl -d`（PS 管道） | 换行被吞：`a\nb\nc` → `abc` |

zcurl 把这些坑全部排除在**工具边界之外**：命令行里出现任何非 ASCII 字符立即拒绝（exit 2），中文只允许从字节通道进来，全链路原样透传。

## 铁律

1. **argv 只收 ASCII**。非 ASCII 立即 `exit 2` 并给出替代写法（stdin / `--body-env` / `zcurl enc`）。
2. **body 走字节通道**：stdin（默认）或 `--body-env NAME`（宽字符环境变量，PowerShell 零配置）或 `--body-file FILE`（本地文件字节，与 `--body-env` 互斥，大载荷首选）。
3. **stdout = 原始响应字节**，stderr = 状态行，绝不混流、绝不改编码。收发都是流式直传（`ResponseHeadersRead` + `CopyToAsync`），内存峰值 = 1× 响应大小。

## 构建

```powershell
dotnet build -c Release                 # 普通编译
dotnet publish -c Release -r win-x64    # 单文件 AOT exe → src\zcurl\bin\Release\net10.0\win-x64\publish\zcurl.exe（4.40 MB）
dotnet test                             # 单元测试（纯逻辑，无网络）
```

要求 .NET 10 SDK + C++ AOT 工作负载。

发布开关（`zcurl.csproj`）：`OptimizationPreference=Size` + 关诊断活动/EventSource/元数据更新器/调试器/异常堆栈。v0.5 基线实测比 `Speed` 配置**更小也更快**：3.70 MB vs 4.87 MB，启动 32ms vs 34ms，回环请求 39 vs 44 ms（`bench.ps1`）。代价是崩溃时只报异常消息、不带堆栈——所有异常都在程序内 catch 成一行提示，影响可忽略。

剩下的体积是 HTTPS 客户端本体，砍不动：实测 `System.Net.Http`（含 Sockets + TLS）占 1.80 MB（46%），`selftest` 占 0.30 MB，`System.Text.Json` DOM 占 0.15 MB，AOT 运行时地板 0.87 MB。换 `HttpClientHandler` 反而大 0.26 MB（Windows 原生 WinHTTP 开关实测无效）。

## 用法

```text
zcurl <METHOD> <URL> [key] [选项]      原始请求（body 走 stdin）
zcurl registry                          GET  /agent/registry
zcurl describe <实例> [format]           GET  /agent/describe
zcurl state <实例>                       GET  /agent/state
zcurl get <实例> <path>                  GET  /agent/get
zcurl set <实例> <path>                  POST /agent/set      body = JSON 值
zcurl invoke <实例> <method>             POST /agent/invoke   body = JSON 数组
zcurl action <实例> <action>             POST /appagent/actions/<action>  body = JSON 对象
zcurl manifest <实例>                    GET  /appagent/manifest
zcurl enc                                stdin → 百分号编码（ASCII 输出）
zcurl selftest                           本地回环字节保真自检（不访问外网）
```

选项：`--body-file FILE`（body 取自文件，与 `--body-env` 互斥）、`--body-env NAME`、`--profile APP`（自动发现 base/key）、`--timeout MS`、`--expect CODE`（期望状态码，匹配 exit 0）、`--base URL`、`--key KEY`、`--key-b KEY`、`--field NAME`（只打印响应 JSON 的该字段，支持点路径 `a.b.0.c`）、`-H "Name: Value"`（可重复）、`-q k=v`（可重复，追加 URL query）、`--out FILE`、`-v`、`-h`。
取值优先级：显式 flag > `--profile` 档案 > `$ZCURL_*` 环境变量 > 默认。子命令基址默认 `http://127.0.0.1:9090`（`--base` 或 `ZCURL_BASE` 覆盖），key 默认 `ZCURL_KEY` / `ZCURL_KEY_B`。
`--profile APP` 读 `%LOCALAPPDATA%\Puppet.AppAgents\APP.*.json`（解析成功且 pid 存活取最新；全部失效 exit 2 并列出原因），大载荷用 `--body-file`（`--body-env` 受环境块约 32K 字符上限约束）。

body 形状三套，写错 exit 2：`set` = JSON 值、`invoke` = JSON 数组、`action` = JSON 对象。
zcurl **不是 curl**：`-X` / `-d` / `-o` / `--noproxy` 等一律 exit 2 并提示；代理默认直连，用 `--proxy` 走 `HTTPS_PROXY`/`HTTP_PROXY`（无则系统代理），`--proxy=URL` 指定代理。
Puppet 返回 **404 常是 Bearer 密钥不对**（密钥错误伪装成 404 而非 401）→ 先查 `--key` / `$ZCURL_KEY`，stderr 会带这行提示。
PS 控制台读中文响应先设 `[Console]::OutputEncoding = [Text.Encoding]::UTF8`，否则显示为 GBK 乱码（zcurl 本身不改编码）。

### 中文载荷：两条正道

**Git Bash**（stdin 字节流）：

```bash
J='{"prompt":"一个女孩，赛博朋克"}'
printf '%s' "$J" | zcurl POST "http://127.0.0.1:9090/x" --out resp.bin
```

**PowerShell**（环境变量，宽字符，无需管道）：

```powershell
$env:BODY = '{"prompt":"一个女孩，赛博朋克"}'
zcurl POST 'http://127.0.0.1:9090/x' --body-env BODY --out resp.bin
```

> `--body-env` 指定时 stdin 被忽略；变量未设置直接报错（exit 2），避免误发上一次的载荷。

### 中文 URL / 参数：先 `enc`

```bash
printf '%s' '中文' | zcurl enc        # → %E4%B8%AD%E6%96%87（纯 ASCII）
zcurl get 'App#1' '%E4%B8%AD%E6%96%87'
```

已含 `%XX` 的值视为**已编码**，不会二次编码；`--out` 的路径同样按此规则解码。

### 退出码

| code | 含义 |
|---|---|
| 0 | 2xx，或 `--expect` 匹配（非 2xx 也算成功） |
| 1 | HTTP 非 2xx 或网络失败 / `--field` 字段不存在 / `--expect` 不匹配 / `--timeout` 超时 |
| 2 | 用法错误 / argv 含非 ASCII / body JSON 不合法 / 环境变量缺失 / `--body-env` 与 `--body-file` 冲突 / `--profile` 无可用档案 |

## 与 curl 的对照

| 场景 | curl | zcurl |
|---|---|---|
| PS 发中文 JSON | 引号被吞 / 乱码 | `--body-env` 一次到位 |
| bash 发中文 JSON | argv 变 GBK | `printf '%s' "$J" \| zcurl ...` |
| 请求体带换行 | `-d` 会吞 | stdin 字节原样（等价 `--data-binary`） |
| 响应中文 | 需要改 `[Console]::OutputEncoding` | `--out file` 或直接重定向字节，不改控制台 |
| 传文件 / multipart | 支持 | **不支持**（v0.3 范围外，用 curl） |
| 传二进制 body | `--data-binary @-` | stdin 或 `--body-file` 字节原样 |
| `-X` / `-d` / `--noproxy` 等参数 | 支持 | **不支持**（未知选项 exit 2 + 提示，防止带着 curl 习惯写出静默错的命令） |

## Puppet.Core 支持

[Puppet.Core](https://github.com/xiaoyuvax/PuppetCore) 是宿主应用用来把自身暴露成 agent 可操作对象（实例 / 属性 / 方法 / 动作）的 .NET 库。zcurl 对它的 `/agent/*`（A 面）与 `/appagent/*`（B 面）端点提供一等子命令，免去手拼 URL 与 JSON：

```bash
zcurl registry
zcurl registry -q type=WinForm -q limit=10        # 端点自带的过滤参数用 -q 透传
zcurl describe 'App#1'
zcurl state 'App#1'
zcurl get 'App#1' 'Display.Text'
zcurl get 'App#1' 'PicResult.Image' --field type     # 只取一个字段（复杂对象判据：value=null 而 type 非空 = 有值）
printf '%s' '"新值"' | zcurl set 'App#1' 'Display.Text'
printf '%s' '["MoveAction",10,20]' | zcurl invoke 'App#1' 'MoveAction'
printf '%s' '{"folderPath":"C:/work"}' | zcurl action 'App#1' 'OpenAction'   # B 面，用 ZCURL_KEY_B
zcurl manifest 'App#1'
```

`-q k=v`（可重复）追加到任何 URL——子命令与原始模式通用，端点的查询参数都从这里进（`registry` 的 `type/tags/keyword/offset/limit`、`logs` 的 `maxLen`、`book`/`lock`/`control` 的参数）。值按「已含 `%XX` 视为已编码」规则处理，不会二次编码。

自动化常用组合（发现档案 + 断言 + 点路径）：

```bash
zcurl registry --profile MyApp -v                      # 自动填 base/key：档案取解析成功且 pid 存活的最新者，-v 列出落选原因
zcurl get 'App#1' 'Display.Text' --timeout 5000 --expect 200   # 超时 exit 1；期望码匹配 exit 0（非 2xx 也算成功）
zcurl get 'App#1' 'Display.Text' --field value         # 点路径同理：--field data.camera.alpha（数段索引数组）
```

`control` / `logs` 这类更低频的端点用原始模式即可：

```bash
printf '{}' | zcurl POST 'http://127.0.0.1:9090/agent/control?name=MainForm&ctrl=Button1&action=click'
```

## 自检

```bash
zcurl selftest     # 起本地回环服务，分别用 stdin、--body-env 与 --body-file 各发一次，比对字节与 Content-Type
```

## 字节保真矩阵（实测）

对本地回显服务的实测（载荷 = `{"prompt":"一个女孩，赛博朋克"}\n{"第二行":true}`，59 B；另加 1 MB 随机二进制 `--body-file` sha256 对拍），10 项全过（PowerShell 5.1 + Git Bash）：

| 场景 | 结果 |
|---|---|
| PowerShell → stdin（字节直写） | ✅ 59B 精确 |
| PowerShell → `--body-env`（中文环境变量） | ✅ 59B 精确 |
| PowerShell 管道 + `$OutputEncoding=UTF8` | ⚠️ 64B = PS 5.1 自加 BOM(3) + 59B + `\r\n`(2)，原样透传 |
| PowerShell 管道默认编码 | ⚠️ 37B，中文 → `?`（反面教材，README 已注明） |
| PowerShell argv 中文 | ✅ 拒绝 exit 2，不发请求 |
| 空 body POST | ✅ `Content-Length: 0`，不发 `Content-Type` |
| Git Bash → stdin | ✅ 59B 精确 |
| Git Bash → `--body-env` | ✅ 59B 精确 |
| Git Bash argv 中文 | ✅ 拒绝 exit 2，不发请求 |
| PowerShell → `--body-file`（1 MB 随机二进制，sha256 对拍） | ✅ 逐字节一致 |

> 要精确字节就别用 PowerShell 的字符串管道（改 BOM、改编码、追加 `\r\n`），用 `--body-env` 或 `cmd /c "type file | zcurl ..."`。

## 已知边界

- 不支持文件上传 / multipart / cookie jar；代理可选（`--proxy` / `--proxy=URL`，默认直连）。
- `--body-env` 值受 Windows 环境块限制（约 32K 字符）；更大的载荷用 stdin 或 `--body-file`。
- PS 管道写入会追加 `\r\n` 并可能加 BOM，要精确控制字节就别走 PS 字符串管道。
- 响应无 `Content-Length`（chunked）时，stderr 状态行的字节数显示 `?B`（`--out` 仍报落盘文件的精确字节数）。

## 作为 Agent Skill 安装

根目录 [`SKILL.md`](SKILL.md) 是给编码 agent 的技能文档：何时用、三条铁律、载荷配方、Puppet 子命令、退出码与踩坑。复制到 agent 的 skills 目录即装好（agent 可自主调用 zcurl）：

```powershell
# opencode
$d = "$env:USERPROFILE\.config\opencode\skills\zcurl"; New-Item -ItemType Directory -Force $d | Out-Null
Copy-Item SKILL.md "$d\SKILL.md"

# Claude Code
$d = "$env:USERPROFILE\.claude\skills\zcurl"; New-Item -ItemType Directory -Force $d | Out-Null
Copy-Item SKILL.md "$d\SKILL.md"
```

skill 列表在 agent 会话启动时加载，装完需开新会话；用 `zcurl selftest`（期望 `ALL PASS (3/3)`）确认可用。

## License

MIT
