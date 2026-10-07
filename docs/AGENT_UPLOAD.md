# AnyDrop 接口说明（给 AI 与脚本用）

所有路径都带部署前缀 `/drop`，下面用 `$ANYDROP_BASE`（例如 `https://fshake.com/drop`）代替。
错误响应统一是 `{"error":{"code":"...","message":"..."}}`，`message` 是中文，可以直接展示给人。

只有**上传**需要密钥；**下载不需要任何凭证**，文件 id 本身就是凭证。

## 上传（上传密钥）

```
POST $ANYDROP_BASE/v1/blobs
Authorization: Bearer <上传密钥>
X-Filename: report.md          # 可选；建议只用 ASCII
Content-Type: application/octet-stream
Idempotency-Key: <uuid>        # 可选；重试时带上同一个值
body: 文件原始字节（不要用 multipart/form-data）
```

成功返回 `201`：

```json
{ "id": "K7F3…", "url": "https://…/drop/v1/blobs/K7F3…", "sha256": "<明文哈希>", "size": 12345, "expiresAt": "2026-11-05T07:00:00Z" }
```

`url` **就是文件直链**：浏览器点开即下载，`curl` 它即拿到字节，不需要任何头部、也不要把 `/v1/blobs/` 改写。
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
- 密钥从环境变量读，**不要**写进代码、提交信息、日志或发给人的消息里。它是上传密钥：泄漏的后果只是
  别人能往服务里传东西（受配额与单文件上限约束），读不到也删不掉任何文件。

## 上传（管理面板，人工上传）

管理员在 `$ANYDROP_BASE/admin` 登录后可以直接拖拽/选择文件上传，返回的 `url` 与接口上传**完全相同格式**，
所以「人上传 → 把 `url` 交给 AI 下载」这条路的用法就是：让管理员把面板里显示（或点「复制直链」得到）的链接发出来，
AI 端按下面的下载方式取即可。

- 面板上传不消耗任何上传密钥的配额，也不属于任何密钥（审计里 `tokenId` 为空）。
- 面板上传**不参与幂等**：即使带了 `Idempotency-Key` 也不会复用，传两次就是两个文件。
- 面板里能做的其它事：改期、保留（不过期）、删除。删除只有这一条入口——密钥没有任何删除权。

## 下载

```
GET $ANYDROP_BASE/v1/blobs/{id}
Range: bytes=0-1023            # 可选，支持 a-b / a- / -n（后缀长度）
```

**不要带 Authorization**（带了也会被忽略）。只要知道 id（或直接拿到上传返回的 `url`）就能下载：

```bash
curl -fsS -o report.md "$ANYDROP_BASE/v1/blobs/<id>"
```

- 成功 `200`（带 Range 则 `206`）；响应头 `X-File-Sha256` 是**明文**的 SHA-256，下载后请核对。
- 响应 `Content-Type` **固定为 `application/octet-stream`**、`Content-Disposition: attachment`：
  上传者声明的类型不会被用来决定下载响应类型（避免把 blob 当 HTML 在同源下渲染）。
- `HEAD` 同样可用，只返回元数据，不会增加下载计数。
- 想看元数据的人类可以打开 `$ANYDROP_BASE/f/{id}`：服务端直出的信息页，展示文件名/大小/sha256/过期时间，
  同样不需要密钥。它只是可选的视图，机器不需要用它。
  - **时区**：这一页的过期时间固定按 **UTC** 显示并标注，因为收件人时区未知；
    而管理面板里的时间按**浏览器本地时区**显示。两处格式相同（`yyyy-MM-dd HH:mm:ss`）、时区刻意不同。
- id 是 128 bit 随机值，不可猜测也无法枚举；**拿到 id 就等于拿到文件**，所以链接要按密码对待，
  不要贴到公开渠道。链接泄漏后唯一的止血手段是让管理员在管理面板里删掉文件（或等它过期）。

## 删除

没有面向密钥的删除接口。删除只能由管理员在管理界面（`/drop/admin`）里操作；
管理面板里上传的文件也走同一条路径。

## 错误码

| HTTP | code | 说明 |
| --- | --- | --- |
| 401 | `invalid_token` / `token_expired` | 上传密钥无效、已撤销或已过期 |
| 404 | `not_found` | 文件不存在或已过期（含 id 格式非法） |
| 409 | `idempotency_conflict` | 同一幂等键被用于不同内容 |
| 411 | `length_required` | 上传没带 Content-Length |
| 413 | `file_too_large` | 超过该密钥或服务端的单文件上限 |
| 416 | `range_not_satisfiable` | Range 头不合法或越界 |
| 429 | `rate_limited` | 请求过于频繁 |
| 500 | `integrity_error` / `internal_error` | 密文校验失败 / 服务内部错误 |
| 503 | `ui_not_built` | 管理界面未构建（不影响接口） |
| 507 | `quota_exceeded` / `disk_low` | 该密钥配额用尽 / 服务器空间不足 |
