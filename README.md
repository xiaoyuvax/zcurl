# zcurl

**Windows shell 专用的 UTF-8 安全 HTTP 客户端**，单文件 NativeAOT exe，给 agent / 脚本替代 `curl`。

一句话：**argv 只收 ASCII；中文载荷走 stdin 或环境变量；字节逐字节保真。**

## 下载

单文件 `zcurl.exe`（**3.7 MB**，NativeAOT，无运行时依赖），放进 PATH 即用：

```powershell
Invoke-WebRequest 'https://github.com/xiaoyuvax/zcurl/releases/download/v0.1.0/zcurl.exe' -OutFile .\zcurl.exe
.\zcurl.exe selftest                                     # 本地回环自检，不访问外网
Get-FileHash .\zcurl.exe -Algorithm SHA256               # 与 zcurl.exe.sha256 比对
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
2. **body 走字节通道**：stdin（默认）或 `--body-env NAME`（宽字符环境变量，PowerShell 零配置）。
3. **stdout = 原始响应字节**，stderr = 状态行，绝不混流、绝不改编码。

## 构建

```powershell
dotnet build -c Release                 # 普通编译
dotnet publish -c Release -r win-x64    # 单文件 AOT exe → src\zcurl\bin\Release\net10.0\win-x64\publish\zcurl.exe（3.69 MB）
dotnet test                             # 单元测试（纯逻辑，无网络）
```

要求 .NET 10 SDK + C++ AOT 工作负载。

发布开关（`zcurl.csproj`）：`OptimizationPreference=Size` + 关诊断活动/EventSource/元数据更新器/调试器/异常堆栈。实测比 `Speed` 配置**更小也更快**：3.69 MB vs 4.87 MB，启动 32ms vs 34ms，回环请求 39 vs 44 ms（`bench.ps1`）。代价是崩溃时只报异常消息、不带堆栈——所有异常都在程序内 catch 成一行提示，影响可忽略。

## 用法

```text
zcurl <METHOD> <URL> [key] [选项]      原始请求（body 走 stdin）
zcurl registry                          GET  /agent/registry
zcurl describe <实例> [format]           GET  /agent/describe
zcurl get <实例> <path>                  GET  /agent/get
zcurl set <实例> <path>                  POST /agent/set      body = JSON 值
zcurl invoke <实例> <method>             POST /agent/invoke   body = JSON 数组
zcurl action <实例> <action>             POST /appagent/actions/<action>  body = JSON 对象
zcurl manifest <实例>                    GET  /appagent/manifest
zcurl enc                                stdin → 百分号编码（ASCII 输出）
zcurl selftest                           本地回环字节保真自检（不访问外网）
```

选项：`--body-env NAME`、`--base URL`、`--key KEY`、`--key-b KEY`、`-H "Name: Value"`（可重复）、`--out FILE`、`-v`、`-h`。
子命令基址默认 `http://127.0.0.1:9090`（`--base` 或 `ZCURL_BASE` 覆盖），key 默认 `ZCURL_KEY` / `ZCURL_KEY_B`。

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
| 0 | 2xx |
| 1 | HTTP 非 2xx 或网络失败 |
| 2 | 用法错误 / argv 含非 ASCII / body JSON 不合法 / 环境变量缺失 |

## 与 curl 的对照

| 场景 | curl | zcurl |
|---|---|---|
| PS 发中文 JSON | 引号被吞 / 乱码 | `--body-env` 一次到位 |
| bash 发中文 JSON | argv 变 GBK | `printf '%s' "$J" \| zcurl ...` |
| 请求体带换行 | `-d` 会吞 | stdin 字节原样（等价 `--data-binary`） |
| 响应中文 | 需要改 `[Console]::OutputEncoding` | `--out file` 或直接重定向字节，不改控制台 |
| 传文件 / multipart | 支持 | **不支持**（v0.1 范围外，用 curl） |
| 传二进制 body | `--data-binary @-` | stdin 字节原样 |

## Puppet.Core 支持

[Puppet.Core](https://github.com/xiaoyuvax/PuppetCore) 是宿主应用用来把自身暴露成 agent 可操作对象（实例 / 属性 / 方法 / 动作）的 .NET 库。zcurl 对它的 `/agent/*`（A 面）与 `/appagent/*`（B 面）端点提供一等子命令，免去手拼 URL 与 JSON：

```bash
zcurl registry
zcurl describe 'App#1'
zcurl get 'App#1' 'Display.Text'
printf '%s' '"新值"' | zcurl set 'App#1' 'Display.Text'
printf '%s' '["MoveAction",10,20]' | zcurl invoke 'App#1' 'MoveAction'
printf '%s' '{"folderPath":"C:/work"}' | zcurl action 'App#1' 'OpenAction'   # B 面，用 ZCURL_KEY_B
zcurl manifest 'App#1'
```

`control` / `state` / `logs` 这类低频端点用原始模式即可：

```bash
printf '{}' | zcurl POST 'http://127.0.0.1:9090/agent/control?name=MainForm&ctrl=Button1&action=click'
```

## 自检

```bash
zcurl selftest     # 起本地回环服务，分别用 stdin 与 --body-env 各发一次，比对字节与 Content-Type
```

## 字节保真矩阵（实测）

对本地回显服务 `127.0.0.1:8731` 的实测，9 项全过：

| 场景 | 结果 |
|---|---|
| PowerShell → stdin（字节直写） | ✅ 40B 精确 |
| PowerShell → `--body-env`（中文环境变量） | ✅ 40B 精确 |
| PowerShell 管道 + `$OutputEncoding=UTF8` | ✅ 精确（PS 追加 `\r\n` 属 PS 行为） |
| PowerShell 管道默认编码 | ⚠️ 按设计变成 `????`（反面教材，README 已注明） |
| PowerShell argv 中文 | ✅ 拒绝 exit 2，不发请求 |
| Git Bash → stdin | ✅ 40B 精确 |
| Git Bash → `--body-env` | ✅ 40B 精确 |
| Git Bash argv 中文 | ✅ 拒绝 exit 2，不发请求 |
| 空 body POST | ✅ `Content-Length: 0`，不发 `Content-Type` |

## 已知边界

- 不支持文件上传 / multipart / cookie jar / 代理（`UseProxy=false`，专攻本机与内网回环）。
- `--body-env` 值受 Windows 环境块限制（约 32K 字符）；更大的载荷用 stdin。
- PS 管道写入会追加 `\r\n`，要精确控制字节就别走 PS 字符串管道。

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

skill 列表在 agent 会话启动时加载，装完需开新会话；用 `zcurl selftest`（期望 `ALL PASS (2/2)`）确认可用。

## License

MIT
