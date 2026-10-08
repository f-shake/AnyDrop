---
name: anydrop
description: 把本地文件（报告、导出数据、截图、日志、安装包等）变成一条免登录即可下载的直链，用来交付给用户。当用户说「发我」「把这个文件给我」「给我个下载链接」「分享这个文件」「怎么传给你」「文件太大发不过去」时使用；判断依据是**交付对象是一个本地文件、且当前聊天通道不适合直接传（大文件、二进制）**——仅把一段文本或一段代码贴给用户时不要用本技能。底层是自建投递服务（内部名 AnyDrop，用户通常不知道这个名字）。
---

# 文件投递（把本地文件变成直链）

一句话契约：**上传要凭据，下载不要任何凭据** —— 文件 id 本身就是凭证。

> 📦 **这是一份仓库资产。** 它随 AnyDrop 仓库分发，放在 `capabilities/anydrop/`，
> **不会被任何 agent 自动加载**。要启用它，把它所在的父目录加进 agent 的技能根
> （DSH 见下），或直接从仓库读本文件。

服务地址由部署决定：生产 `https://fshake.com/drop`，本机开发实例 `http://127.0.0.1:8790/drop`。

## 首选做法：用仓库里的 CLI

```bash
python capabilities/anydrop/scripts/anydrop.py upload <文件路径>
```

成功时 **stdout 恰好一行 JSON**：

```json
{"id":"K7F3…","url":"https://…/drop/v1/blobs/K7F3…","sha256":"<明文哈希>","size":12345,"expiresAt":"2026-11-05T07:00:00Z"}
```

- **`url` 就是直链，原样转述给用户**。浏览器点开即下载，任何 HTTP 客户端 GET 即得字节。
  **不要自己拼链接、不要把 `/v1/blobs/` 改写。**
- 失败时 stdout 为空、原因写在 stderr，退出码区分类别：
  `1` 未预期的内部错误（属 bug，会附 traceback）· `2` 密钥或配置缺失 · `3` 本地文件问题 ·
  `4` 服务端拒绝 · `5` 网络失败。
  成功与否**以退出码与 stdout 为准**，不要只看有没有输出。
- 常用参数：`--name` 覆盖服务端保存的文件名 · `--idempotency-key` 重试复用 ·
  `--base` 指定端点（或环境变量 `ANYDROP_BASE`）· `--timeout` 单次请求超时秒数。

### 密钥：它不会从环境变量里来

脚本自己按这个顺序找密钥：

1. 环境变量 `ANYDROP_KEY` 或 `AnyDrop_Key`（Windows 上这两个名字是同一个变量）；
2. 没有就回退读 **Windows 注册表** `HKCU\Environment`。

第 2 条不是多余的：**agent 会话里的 shell 子进程拿不到任何匹配
`/KEY|PASSWORD|SECRET|TOKEN/i` 的环境变量**（harness 会统一清洗掉，避免把自己的
API key 泄漏给子进程），所以 `$env:ANYDROP_KEY` 在会话里通常是空的。

**绝不打印密钥、绝不写进文件/提交信息/给用户的消息。** 它是上传密钥：泄漏的后果只是
别人能往服务里传东西（受配额与单文件上限约束），读不到、删不掉任何文件。

## 回退做法：直接用 HTTP

CLI 不可用时（比如没有 Python），按下面的最小契约手写请求：

```
POST {base}/v1/blobs
Authorization: Bearer <上传密钥>     X-Filename: report.md     Idempotency-Key: <随机值>
body: 文件原始字节
```

四个必须知道的坑：

- **body 就是文件原始字节**，不要用 `multipart/form-data`（任何客户端的表单/文件上传封装都会踩这个坑）。
- **必须带 Content-Length**。管道、流式 body、或默认分块的客户端会被拒：`411 length_required`。
- 文件名优先用 ASCII 放 `X-Filename`；**非 ASCII 一律走查询参数** `?name=<urlencode 后的文件名>`。
- 重试**必须复用同一个 `Idempotency-Key`**：同内容返回同一个 id，不重复占盘；同键不同内容 → `409`。

> 工具相关的坑：PowerShell 里 `curl` 是 `Invoke-WebRequest` 的别名（要写 `curl.exe`）。

## 下载与校验

- `GET {base}/v1/blobs/{id}`，**不要带 Authorization**（带了也会被忽略）。
- 支持 `Range`（`a-b` / `a-` / `-n`），命中时返回 `206`；`HEAD` 只取元数据、不增加下载计数。
- 响应头 `X-File-Sha256` 是**明文**哈希（服务端存的是密文），把它和本地文件哈希比对，
  就能确认端到端无损。重要文件建议做这一步。
- 想只看元数据的人可以打开 `{base}/f/{id}`（文件名/大小/sha256/过期时间，同样免凭据）。
  该页过期时间固定按 **UTC** 标注，而管理面板按**浏览器本地时区**显示 —— 格式相同、时区刻意不同。

## 删除与改期：没有接口

**没有任何凭据能删除文件。** 删除、改期、设为不过期都只能由管理员在 `{base}/admin` 面板操作。
所以「删掉某某文件」这类请求，正确做法是转告用户去面板点，**不要去翻接口**（不存在，翻了也是白花一轮）。

## 硬边界（照抄，别自行放宽）

- 上传密钥泄漏的后果：别人能往服务里**传**东西（受配额与单文件上限约束）—— **读不到、删不掉、列不出**任何文件。
- **直链按密码对待**：拿到 id 就等于拿到文件，不要贴到公开渠道。泄漏后唯一止血手段是管理员删除或等它过期。
- 单文件上限默认 **256 MiB，且不支持断点续传**；更大就先压缩或拆分。
- 转述过期时间时说明是哪一种口径（UTC 还是本地）。

## 错误码速查

| HTTP | code | 处置 |
| --- | --- | --- |
| 401 | `invalid_token` / `token_expired` | 上传密钥无效/撤销/过期 → 让用户确认，不要自己猜、不要换别的凭据 |
| 404 | `not_found` | 文件不存在或已过期（含 id 格式非法） |
| 409 | `idempotency_conflict` | 同幂等键用于不同内容 → 换一个新键重传 |
| 411 | `length_required` | 没带 Content-Length → 改为一次性发送已知长度的 body |
| 413 | `file_too_large` | 超单文件上限 → 压缩或拆分 |
| 416 | `range_not_satisfiable` | Range 头不合法或越界 |
| 429 | `rate_limited` | 请求过频 → 稍后重试 |
| 500 | `integrity_error` / `internal_error` | 密文校验失败 / 服务内部错误 → 报给用户，别静默重试 |
| 507 | `quota_exceeded` / `disk_low` | 配额用尽 / 空间不足 → 报给用户，别重试 |

错误体统一是 `{"error":{"code":"...","message":"..."}}`，`message` 是中文，可直接展示给人。

## 完整契约

逐字段的完整说明在 [`docs/AGENT_UPLOAD.md`](../../docs/AGENT_UPLOAD.md)（管理面板上传、`/f/{id}`、
Range 语法、配额与水位细节）。CLI 的实现与设计说明在 [`README.md`](README.md)。
