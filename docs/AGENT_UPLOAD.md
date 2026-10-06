# AnyDrop 接口说明（给 AI 与脚本用）

所有路径都带部署前缀 `/drop`，下面用 `$ANYDROP_BASE`（例如 `https://fshake.com/drop`）代替。
错误响应统一是 `{"error":{"code":"...","message":"..."}}`，`message` 是中文，可以直接展示给人。

## 上传（写密钥）

```
POST $ANYDROP_BASE/v1/blobs
Authorization: Bearer <写密钥>
X-Filename: report.md          # 可选；建议只用 ASCII
Content-Type: application/octet-stream
Idempotency-Key: <uuid>        # 可选；重试时带上同一个值
body: 文件原始字节（不要用 multipart/form-data）
```

成功返回 `201`：

```json
{ "id": "K7F3…", "url": "https://…/drop/f/K7F3…", "sha256": "<明文哈希>", "size": 12345, "expiresAt": "2026-11-05T07:00:00Z" }
```

把 `url`（或 `id`）原样发给使用者即可，不要自己拼接链接。

给 Agent 的一行版：

```bash
curl -fsS -X POST "$ANYDROP_BASE/v1/blobs" \
  -H "Authorization: Bearer $ANYDROP_KEY" \
  -H "X-Filename: $(basename "$FILE")" \
  --data-binary @"$FILE"
```

要点与坑：

- **必须带 Content-Length**（`--data-binary` 会自动带）。分块传输会被拒绝：`411 length_required`。
- 单文件上限默认 256 MiB，且**不支持断点续传**；超过就先拆分或压缩。
- 中文文件名请用查询参数：`POST $ANYDROP_BASE/v1/blobs?name=%E6%8A%A5%E5%91%8A.md`。
- 网络中断后用**同一个 `Idempotency-Key`** 重试是安全的：内容相同会返回同一个 id，不重复占盘；内容不同返回 `409`。
- 密钥从环境变量读，**不要**写进代码、提交信息、日志或发给人的消息里。

## 下载

```
GET $ANYDROP_BASE/v1/blobs/{id}
Authorization: Bearer <读密钥>
Range: bytes=0-1023            # 可选，支持 a-b / a- / -n（后缀长度）
```

- 成功 `200`（带 Range 则 `206`）；响应头 `X-File-Sha256` 是**明文**的 SHA-256，下载后请核对。
- 响应 `Content-Type` **固定为 `application/octet-stream`**、`Content-Disposition: attachment`：
  上传者声明的类型不会被用来决定下载响应类型（避免把 blob 当 HTML 在同源下渲染）。
- `HEAD` 同样可用，只返回元数据，不会增加下载计数。
- 浏览器流程：打开 `$ANYDROP_BASE/f/{id}` → 输入读密钥 → 会话 cookie 记住 30 天 → 点下载。
- 想用脚本建立会话（拿到 cookie）：

```bash
curl -c jar.txt -X POST "$ANYDROP_BASE/api/session" \
  -H 'Content-Type: application/json' -d '{"key":"<读密钥>"}'
curl -b jar.txt -o out.bin "$ANYDROP_BASE/v1/blobs/<id>"
```

## 删除

```
DELETE $ANYDROP_BASE/v1/blobs/{id}
Authorization: Bearer <有删除权的密钥>
```

`nas-pull` 预设自带删除权，适合"取完即删"。也可用 `scripts/fetch-by-id.ps1 -Delete`。

## 错误码

| HTTP | code | 说明 |
| --- | --- | --- |
| 401 | `invalid_token` / `token_expired` | 密钥无效、已撤销或已过期 |
| 403 | `scope_denied` | 该密钥没有这项能力，或命名空间不匹配 |
| 404 | `not_found` | 文件不存在或已过期（含 id 格式非法） |
| 409 | `idempotency_conflict` | 同一幂等键被用于不同内容 |
| 411 | `length_required` | 上传没带 Content-Length |
| 413 | `file_too_large` | 超过该密钥或服务端的单文件上限 |
| 416 | `range_not_satisfiable` | Range 头不合法或越界 |
| 429 | `rate_limited` | 请求过于频繁 |
| 500 | `integrity_error` / `internal_error` | 密文校验失败 / 服务内部错误 |
| 503 | `ui_not_built` | 管理界面未构建（不影响接口） |
| 507 | `quota_exceeded` / `disk_low` | 该密钥配额用尽 / 服务器空间不足 |

## 命名空间

每个密钥绑定一个命名空间（默认 `default`），它只能看到自己命名空间里的文件。
典型用法：AI 用 `ai-write` + 命名空间 `inbox` 上传；你用 `me-read` + 同一个命名空间取件；
要给 NAS 送东西时用另一把写密钥绑 `to-nas`，NAS 侧用 `nas-pull` 绑 `to-nas` 按 id 取。
