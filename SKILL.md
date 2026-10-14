---
name: zcurl
description: "UTF-8-safe HTTP client for Windows shells (zcurl.exe). Use whenever you must send an HTTP request from PowerShell or Git Bash on Windows — especially with non-ASCII / Chinese payloads (argv is ASCII-only by design), and for Puppet.Core host endpoints (/agent/*, /appagent/*) on 127.0.0.1:9090. Covers install, payload rules, subcommands, exit codes and pitfalls."
license: MIT
---

# zcurl — Windows shell 的 UTF-8 安全 HTTP 客户端

## 何时用

- 从 **PowerShell / Git Bash** 发 HTTP 请求，尤其 body 含中文/非 ASCII。
- 操作 **Puppet.Core 宿主应用**的 `/agent/*`、`/appagent/*` 端点。
- 任何「用 curl 发中文 JSON 会乱码 / 吞引号」的场景。

不要用它：上传文件、multipart、cookie、走代理（这些 curl 更合适；zcurl 显式 `UseProxy=false`）。

## 安装与自检

```powershell
# 1) 取得 exe：Release 页下载 zcurl.exe（https://github.com/xiaoyuvax/zcurl/releases/latest），或自行构建（见 README「构建」）
# 2) 把 zcurl.exe 放进 PATH 中任一目录（或直接用绝对路径调用）
# 3) 校验 + 自检（不访问外网）
Get-FileHash .\zcurl.exe -Algorithm SHA256   # 与 Release 页 sha256 比对
zcurl selftest                               # 期望 ALL PASS (3/3)
zcurl -h                                     # 用法
```

## 三条铁律（违反必出错）

1. **argv 只收 ASCII**。中文/任何非 ASCII 参数 → 立即 `exit 2`。
   中文载荷走字节通道，中文 URL 参数先 `enc`。
2. **body 只走 stdin / `--body-env` / `--body-file`**（显式来源不并存：`--body-env` 与 `--body-file` 同给 exit 2；env 指定时忽略 stdin，变量未设置即 exit 2，防误发旧载荷；大载荷用 `--body-file` 绕开环境变量 ~32K 上限）。
3. **stdout = 原始响应字节，stderr = 状态行**。要落盘用 `--out FILE`，别把 stdout 当日志读。

## 载荷示例

PowerShell（推荐，宽字符环境变量）：

```powershell
$env:BODY = '{"prompt":"一个女孩，赛博朋克"}'
zcurl POST 'http://127.0.0.1:9090/x' --body-env BODY --out resp.bin
```

Git Bash（stdin 字节流）：

```bash
J='{"prompt":"一个女孩，赛博朋克"}'
printf '%s' "$J" | zcurl POST 'http://127.0.0.1:9090/x' --out resp.bin
```

大载荷 / 已落盘的 body（与 `--body-env` 互斥；路径可百分号编码）：

```powershell
zcurl POST 'http://127.0.0.1:9090/x' --body-file .\payload.json
```

中文 URL / 参数：

```bash
printf '%s' '中文' | zcurl enc      # → %E4%B8%AD%E6%96%87
zcurl get 'App#1' '%E4%B8%AD%E6%96%87'
```

已含 `%XX` 的值视为已编码，不会二次编码。

## Puppet 子命令（默认 `--base http://127.0.0.1:9090`）

| 命令 | 端点 | body |
|---|---|---|
| `zcurl registry` | GET `/agent/registry` | — |
| `zcurl describe <实例> [md]` | GET `/agent/describe` | — |
| `zcurl state <实例>` | GET `/agent/state` | — |
| `zcurl get <实例> <path>` | GET `/agent/get` | — |
| `printf '%s' '"值"' \| zcurl set <实例> <path>` | POST `/agent/set` | JSON 值（自动包 `{path,value}`） |
| `printf '%s' '["方法",1,2]' \| zcurl invoke <实例> <方法>` | POST `/agent/invoke` | JSON 数组 |
| `printf '%s' '{"key":"v"}' \| zcurl action <实例> <action>` | POST `/appagent/actions/<action>` | JSON 对象（自动补 `callId`） |
| `zcurl manifest <实例>` | GET `/appagent/manifest` | — |

body 形状三套，写错 exit 2：`set` = JSON 值、`invoke` = JSON 数组、`action` = JSON 对象。
`--field NAME` 只打印响应 JSON 的该字段（字符串去引号），支持点路径 `a.b.0.c`（数段索引数组）：`zcurl get 'App#1' 'Pic.Image' --field type`、`--field data.camera.alpha`。
自动化三件套：`--profile <app>`（读 `%LOCALAPPDATA%\Puppet.AppAgents\app.*.json`，取解析成功且 pid 存活的最新档案自动填 base+key，全部失效 exit 2 并列原因；优先级 显式 flag > profile > `$ZCURL_*` 环境变量）、`--timeout MS`（超时 exit 1）、`--expect CODE`（匹配 exit 0，**非 2xx 也算成功**；不匹配 exit 1 并打 `expect/got`——想断言 400/404/409 就用它）。
key：A 面 `ZCURL_KEY`，B 面 `ZCURL_KEY_B`（`action`/`manifest` 自动用 B，缺失回落 A）。
**404 = 先查 key**：Puppet 密钥错误返回 404 而非 401，stderr 会带这行提示；别急着改 URL。
实例名里的 `#` 会自动转义（`App#1` → `App%231`），无需手动 encode。
端点自带的查询参数用 `-q k=v`（可重复，子命令与原始模式通用）：`zcurl registry -q type=WinForm -q limit=10`、`-q maxLen=4096`；值已含 `%XX` 视为已编码，不二次编码。
`/agent/control`、`/agent/logs` 用原始模式即可。

原始模式：

```bash
zcurl GET  'http://127.0.0.1:9090/agent/registry' "$ZCURL_KEY"
printf '{}' | zcurl POST 'http://127.0.0.1:9090/agent/control?name=MainForm&ctrl=btnX&action=click'
```

## 退出码

| code | 含义 | agent 该怎么处理 |
|---|---|---|
| 0 | 2xx 或 `--expect` 匹配 | 成功 |
| 1 | HTTP 非 2xx / 网络失败 / 字段缺失 / `--expect` 不匹配 / 超时 | 读 stderr 状态行；检查服务是否启动 |
| 2 | 用法/argv 非 ASCII/body JSON 不合法/env 缺失/body 来源冲突/`--profile` 无可用档案 | 读 stderr 提示，按提示改写命令（**不要重试同一条**） |

## 踩坑

- **别把中文写进 argv**（PS/bash 都会坏），永远走 stdin / `--body-env` / `--body-file`。
- **这不是 curl**：`-X` / `-d` / `-o` / `--noproxy` 等未知选项直接 exit 2 并提示；zcurl 固定直连（`UseProxy=false`），别带 curl 的代理参数。
- **404 先查 key**：Puppet 密钥错误返回 404 而非 401 → 检查 `--key` / `$ZCURL_KEY`（B 面 `--key-b` / `$ZCURL_KEY_B`），别改 URL。
- **PS 字符串管道**默认 `$OutputEncoding=ASCII` → 变 `????`，且会追加 `\r\n`（设成 `[Text.Encoding]::UTF8` 也一样会加 BOM）；要精确字节别用 PS 管道，用 `--body-env`。
- `--body-env` 的变量必须当次赋值，别复用旧变量。
- PS 里读中文响应先设 `[Console]::OutputEncoding = [Text.Encoding]::UTF8`，否则控制台显示为 GBK 乱码（响应字节本身是 UTF-8，zcurl 不改编码）。
- 响应中文乱码？那是你读 stdout 的方式不对：用 `--out file` 或按字节重定向，zcurl 不改编码。
- 详细设计与构建见仓库 `README.md` / `README.en.md`。
