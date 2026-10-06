# AnyDrop 开发计划 v1.2（G1 已确认）

公网文件交换服务。AI 用环境变量里的写 key 上传、拿回 id/URL；人输读 key 下载；磁盘上加密；`/drop/admin` 管理页。

部署形态：公网 VPS 上手动启动单个 AOT exe；公网入口 `https://fshake.com/drop`，由服务器 nginx 反代到 `127.0.0.1:8790`。

---

## 1. 功能需求

| # | 需求 |
|---|---|
| F1 | `POST /drop/v1/blobs` + Bearer 写 token + `X-Filename` + 原始字节体 → 201 `{id,url,sha256,size,expiresAt}` |
| F2 | id = 16 字节 CSPRNG → 26 位 Crockford Base32；不含文件名/哈希/时间 |
| F3 | `GET /drop/v1/blobs/{id}` 需读 token 或有效会话；否则 401 |
| F4 | `GET /drop/f/{id}` = 服务端直出的极简下载页（无框架）→ 输 key → `POST /drop/api/session` → 跳转下载 |
| F5 | 上传返回与 `X-File-Sha256` 均为明文的 SHA-256；上传后下载逐字节一致 |
| F6 | `data/` 内不含明文（明文特征串检索零命中） |
| F7 | `GET /drop/admin` = Vue 3 + Element Plus + TS 单页：登录、文件表、下载/删除/改期/pin、token 管理、创建后一次性显示 key |
| F8 | TTL 到期由 GC 删除，之后 404 |
| F9 | 超单文件 256 MiB → 413；超 token 配额 507；磁盘水位越线 507 |
| F10 | `Range` → 206，块对齐解密 |
| F11 | 同 `Idempotency-Key` 重放返回同一 id |
| F12 | 上传/下载/删除写 `audit_log`（含真实 IP，不记明文 key） |
| F13 | `GET /drop/healthz` → 状态 + 剩余磁盘 + 文件数 |
| F14 | SPA history 路由白名单回退（`/drop/`、`/drop/admin/**` → index.html；`/v1/**`、`/api/**`、`/healthz` 永不吞）+ 严格 CSP + UI 未构建兜底页 |
| F15 | 能力位 `can_upload / can_read / can_delete`，预设 `ai-write` / `me-read` / `nas-pull` |
| F16 | 反向取件按 id：`nas-pull` token 访问 `to-nas` 命名空间，可勾选取完自删 |

## 2. 技术方案

- **后端**：.NET 10 minimal API + Kestrel（仅 `127.0.0.1:8790`），`PublishAot=true` 单文件；`Microsoft.Data.Sqlite` 存元数据；`System.Text.Json` 源生成；AES-256-GCM 分块（1 MiB）+ 每文件随机 DEK（KEK 包裹）；PBKDF2-SHA256 210k；`UsePathBase("/drop")`；静态资源内嵌。
- **前端**：Vue 3 + Vite + Element Plus（按需引入）+ TypeScript + Vue Router（history，`base=/drop/`）+ Vitest；`vue-tsc --noEmit`；产物内嵌进 exe。下载页不进 SPA。
- **构建链**：`npm ci → vue-tsc → vitest run → vite build → 复制 dist + 生成资源清单 → dotnet publish (AOT)`。`win-x64` 本机出，`linux-x64` 在 Linux 上出（AOT 不能跨 OS 交叉编译）。
- **跨平台约束**：不出现平台专有 API；路径一律 `Path.Combine`；`master.key` 权限按 OS 分支；文件名不依赖 ANSI 代码页。
- **已否决**：手写单页 + Bootstrap/Pico、hash 路由、前端产物放 `wwwroot/`、下载页进 SPA、EF Core、MVC 控制器、MinIO/S3、filebrowser+frp、复用 Anywhere 通道、分片续传、端到端加密（预留 `key_version`）、TOTP、CLI、一次性短链、列表接口、ESLint/Pinia/i18n/SSR。

## 3. 系统结构

```
[构建] web/ → vite build → dist/ ──┐
                                   ├→ Generated/WebAssets.g.cs → AOT publish → AnyDrop.Server.exe
[运行] nginx(443,/drop) → Kestrel(127.0.0.1:8790)
                           ├→ Api/{Blob,Admin,Page,Health}Endpoints
                           │     └→ Services(Token·Blob·AdminAuth·RateLimiter)
                           │           ├→ Crypto(ChunkedAead·KeyRing·SecretHasher)
                           │           ├→ Storage(BlobStore·SqliteIndex)
                           │           └→ Maintenance(GcService)
                           ├→ WebAssets(内嵌 SPA + 兜底页) · IpAccessor(XFF) · Errors
                           └→ Pages/download.html（服务端直出，不依赖前端构建）
```

## 4. 数据结构

```sql
tokens(id TEXT PK, name TEXT, key_hash TEXT, key_prefix TEXT, scope_namespace TEXT,
       can_upload INT, can_read INT, can_delete INT, max_file_bytes INT,
       quota_bytes INT, used_bytes INT DEFAULT 0, created_at TEXT, expires_at TEXT NULL,
       revoked_at TEXT NULL, last_used_at TEXT NULL)
blobs(id TEXT PK, name TEXT NULL, size INT, sha256 TEXT, content_type TEXT NULL,
      cipher_path TEXT, key_version INT, file_nonce BLOB, chunk_size INT,
      wrapped_dek BLOB, dek_nonce BLOB, wrap_tag BLOB, token_id TEXT, namespace TEXT,
      created_at TEXT, expires_at TEXT, download_count INT DEFAULT 0, pinned INT DEFAULT 0)
idempotency(token_id TEXT, key TEXT, blob_id TEXT, created_at TEXT, PRIMARY KEY(token_id,key))
audit_log(id INTEGER PK AUTOINCREMENT, ts TEXT, action TEXT, token_id TEXT NULL, blob_id TEXT NULL,
          namespace TEXT NULL, ip TEXT NULL, bytes INT NULL, detail TEXT NULL)
admin(id INT PK CHECK(id=1), password_hash TEXT, salt BLOB, iterations INT, created_at TEXT, updated_at TEXT)
```

磁盘格式：`data/blobs/{id 前 2 位}/{id}` = `header(magic|ver|chunkSize|fileNonce|keyVersion) + N×(密文||16B tag)`，nonce = `fileNonce(4B) || 块序号(8B BE)`。

会话在内存（重启失效）。配置 `anydrop.json`（`ANYDROP__*` 环境变量可覆盖）：`pathBase` / `publicBaseUrl` / `maxUploadBytes` / `dataDir` / `minFreeBytes` / `maxUsedPercent` / `defaultTtlDays` / `gcIntervalMinutes` / `sessionDays` / `trustProxy`。

## 5. 模块划分

| 模块 | 负责 | 不负责 |
|---|---|---|
| Crypto | 字节↔密文、DEK 包裹、哈希 | 路径、鉴权、HTTP |
| BlobStore | 布局、`.part` 原子写、删除、孤儿扫描 | 加密、元数据 |
| SqliteIndex | 元数据 CRUD、建表 | 文件内容 |
| Services | 鉴权→配额→TTL→调下层 | HTTP 状态码 |
| Api | HTTP 契约、认证头、错误码 | 业务规则 |
| WebAssets | 内嵌资源清单、SPA 白名单回退、未构建兜底 | 构建过程本身（归脚本） |
| GcService | 过期/孤儿回收 | 业务写入 |

整体不做：注册/多用户、预览缩略图、分享短链、推送、对象存储、分片、E2E、TOTP、WebDAV、打包下载、ESLint/Prettier 工程化。

## 6. 交互逻辑

- **AI 上传**：`ANYDROP_KEY` → `curl -X POST https://fshake.com/drop/v1/blobs -H "Authorization: Bearer $ANYDROP_KEY" -H "X-Filename: r.md" --data-binary @r.md`。
- **人取件**：点 url → 输读 key → 会话 cookie（`Path=/drop`，默认 30 天）→ 浏览器原生下载。
- **发给 NAS**：用 `to-nas` 写 token 上传得 id → 把 id 给 NAS 上的 AI → 它用 `nas-pull` token 按 id 取（有 delete 权则自删）。
- **超管**：`/drop/admin` 登录后管理；新 token 的 key 只在创建时显示一次。
- **状态流转**：写 `.part` → `rename` + 写库（active）→ `expired` → GC 删除。先落文件后写库，失败只留孤儿；DB 绝不指向不存在的密文。

## 7. 异常情况

`{"error":{"code","message"}}`；前端用 `ElMessage` 显示 message。

| HTTP | code | message |
|---|---|---|
| 401 | `invalid_token` / `token_expired` | 密钥无效或已撤销 / 密钥已过期 |
| 403 | `scope_denied` | 该密钥无权执行此操作 |
| 404 | `not_found` | 文件不存在或已过期 |
| 409 | `idempotency_conflict` | 该幂等键已用于不同内容 |
| 413 | `file_too_large` | 文件超过上限（最大 256 MiB） |
| 416 | `range_not_satisfiable` | 请求的字节范围无效 |
| 429 | `rate_limited` / `login_locked` | 请求过于频繁 / 登录失败过多，请 N 分钟后重试 |
| 503 | `ui_not_built` | 管理界面未构建，请先执行前端构建 |
| 507 | `quota_exceeded` / `disk_low` | 该密钥配额已用尽 / 服务器空间不足，暂时拒绝写入 |
| 500 | `integrity_error` / `internal_error` | 文件数据校验失败 / 服务内部错误（细节只进日志） |

## 8. 边界条件

0 字节文件合法（SHA-256 为空串哈希）；`chunkSize±1`；恰好等于上限（允许）；文件名 >200 截断、剥路径与控制字符、空则回落 id、非 UTF-8 用 `filename*=UTF-8''`；同内容重复上传各自独立 id；Range 越界 416；同 id 并发下载允许；同 token 并发上传上限 4；首启自动建目录、生成 600 权限 `master.key`、无 admin 时提示 `--set-password`；id 冲突重试 3 次；ENOSPC → 507 + 删 `.part`；`web/dist` 缺失 → 兜底页而非崩溃。

## 9. 修改范围

全部新建于 `C:\Running\AnyDrop`，不触碰任何现有目录。文件清单：

```
.gitignore · README.md · AnyDrop.sln · anydrop.json.sample
docs/{PLAN.md, AGENT_UPLOAD.md, DEPLOY.md}
src/AnyDrop.Server/{AnyDrop.Server.csproj, Program.cs, AppConfig.cs, JsonContext.cs,
                    Errors.cs, IpAccessor.cs, WebAssets.cs}
src/AnyDrop.Server/Crypto/{KeyRing,ChunkedAead,SecretHasher}.cs
src/AnyDrop.Server/Storage/{Records,SqliteIndex,BlobStore}.cs
src/AnyDrop.Server/Services/{TokenService,BlobService,AdminAuth,RateLimiter}.cs
src/AnyDrop.Server/Api/{Blob,Admin,Page,Health}Endpoints.cs
src/AnyDrop.Server/Pages/{download.html, ui-not-built.html}
src/AnyDrop.Server/Maintenance/GcService.cs
src/AnyDrop.Server/Generated/WebAssets.g.cs（构建生成，gitignore）
web/{package.json, package-lock.json, tsconfig.json, tsconfig.node.json, vite.config.ts, index.html}
web/src/{main.ts, App.vue, router.ts, session.ts, styles/app.css}
web/src/api/{client.ts, types.ts}
web/src/views/{AdminView.vue, LoginView.vue}
web/src/components/{FileTable,TokenPanel,TokenCreatedDialog}.vue
web/tests/{api.spec.ts, router.spec.ts, session.spec.ts}
tests/AnyDrop.Tests/{AnyDrop.Tests.csproj, TestApp.cs, CryptoTests, BlobStoreTests, SqliteIndexTests,
                     TokenAuthTests, UploadEndpointTests, DownloadRangeTests, AdminTests, GcTests}.cs
scripts/{build-web.ps1, build-all.ps1, publish-win.ps1, publish-linux.sh,
         fetch-by-id.ps1, run.cmd, run.sh}
deploy/{nginx-anydrop.conf.sample, NOTICE-element-plus.txt}
```

运行时依赖仅 `Microsoft.Data.Sqlite`。

## 10. 实现步骤

① 计划文档 → ② .NET 脚手架 → ③ 配置/数据目录/`master.key` → ④ Crypto(+测) → ⑤ SqliteIndex(+测) → ⑥ BlobStore(+测) → ⑦ TokenService → ⑧ BlobEndpoints(+集成测) → ⑨ 下载页 + 会话 → ⑩ AdminAuth + Admin API(+测) → ⑪ GC/healthz/审计 → ⑫ 限速/锁定/水位 → ⑬ 前端工程 + Vitest + `vue-tsc` → ⑭ 内嵌资源 + 白名单回退 + 兜底页 → ⑮ `build-all.ps1` + 本机 AOT 验证 → ⑯ 文档与 nginx 样板 → ⑰ 阶段四跑全量测试

## 11. 测试方案

**后端 xUnit + `WebApplicationFactory`**，7 维：正常（上传→下载哈希一致/空文件/多块）、边界（chunk±1、上限值、超长名、Range 端点）、异常（无/过期/撤销 token、越权、超配额、超上限、水位、篡改、幂等冲突、UI 未构建）、并发（同 id 并发下载、并发上传到上限）、加密（密文无明文、每块 nonce 唯一、翻转一字节必失败、KEK 错必失败）、认证（PBKDF2、锁定、cookie `Path=/drop`+HttpOnly、CSRF/Origin）、GC（过期删、孤儿清、pin 不删）。

**前端 Vitest**（3 个文件）：API 客户端（错误映射、Bearer 头）、路由解析（`/drop/f/{id}` 与 `/drop/admin` 判定）、会话状态。**类型检查** `vue-tsc --noEmit`。

**不做**：前端 E2E（Playwright）—— 列为未覆盖项，靠阶段四端到端人工验证。

## 已知风险与兜底

| 风险 | 兜底 |
|---|---|
| AOT 下 `ManifestEmbeddedFileProvider` 不可用 | 改用构建生成的显式资源名→流映射（`WebAssets.g.cs`，无反射） |
| npm/esbuild 撞沙箱命名管道限制 | 申请一次 `danger-full-access` 提权，先问用户 |
| Element Plus 需要 `style-src 'unsafe-inline'` | 已知放宽，`script-src 'self'` 仍收紧 |
| 前端构建产物缺失 | 兜底页 + 启动日志警告，不崩溃 |

---

## 附：实施修正记录（阶段三复核后追加，不改动上面的已批准内容）

### 文件清单修正（§9）

实际实现与上面的清单有以下差异，以这一段为准：

- 不存在：`web/tsconfig.node.json`（合并进单个 `tsconfig.json`）、`web/src/views/LoginView.vue`（登录表单内联在 `AdminView.vue`）、`src/AnyDrop.Server/Generated/WebAssets.g.cs`。
- 前端产物改用 `src/AnyDrop.Server/wwwroot/` + 构建生成的 `wwwroot.g.props`（`EmbeddedResource` + 确定性 `LogicalName`），`WebAssets.cs` 在启动时读进内存字典。
- 额外存在：`src/AnyDrop.Server/Middleware.cs`（安全头与统一错误中间件）、`src/AnyDrop.Server/Cli.cs`（`--set-password` / `--set-password-stdin`）、`src/AnyDrop.Server/Crypto/Primitives.cs`（Id 与时间）、`scripts/build-web.sh`（Linux 等价构建脚本）、`web/src/auto-imports.d.ts` 与 `web/src/components.d.ts`（构建生成但入库，否则新克隆无法 `typecheck`）。
- 解决方案文件实际是 `AnyDrop.slnx`（.NET 10 默认格式）。

### 构建链修正（§2 / §10）

实际顺序与上面写的相反，以实际为准：`npm ci → vue-tsc → vitest run → vite build → 复制 dist 并生成 wwwroot.g.props → dotnet publish`。
（先检查后构建：类型检查或单测失败时不更新内嵌资源，`build-web.ps1` 会明确提示"当前 wwwroot 仍是上一次成功构建的内容，不要发布"。）

### 测试方案修正（§11）

- 测试框架是 **xunit v3 + Microsoft.Testing.Platform 进程内运行器**（`dotnet test` 在本机受限环境下无法打开父进程句柄），用 `dotnet run --project tests/AnyDrop.Tests` 运行。
- "API 客户端（错误映射、Bearer 头）"应读作"错误信封映射、CSRF 头、`/drop` 前缀拼接"；SPA 只用 cookie 会话，没有 Bearer 用法。
- 测试临时目录放仓库内 `.local-dev/`（系统临时目录不可写）。

### 阶段三复核后的行为变更

- 客户端 IP：优先 `X-Real-IP`，否则取 `X-Forwarded-For` 最右项（原实现取最左，可被伪造）。
- 登录新增与 IP/用户名无关的全局限流 `security:loginGlobalPerMinute`。
- 配额改为原子预占（`TryReserveQuotaAsync`）；blob 行与幂等键同一事务写入。
- 幂等键指向的 blob 已删除/过期时按新上传处理（不再永久 409）。
- GC 过期删除加 `pinned = 0` 条件；孤儿回收改两阶段（删前向数据库复核引用）；清理分片跳过进行中的上传。
- 密文与元数据不一致时在写任何字节前返回 500 `integrity_error`；正文已开始后的失败改为中断连接。
- 下载响应类型固定 `application/octet-stream`。
- `--set-password` 增加 `-stdin` 变体并告警；`retention:defaultTtlDays` 等配置项补校验。
