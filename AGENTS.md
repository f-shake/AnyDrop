# AGENTS.md — AnyDrop 开发须知

本文件是**给在本仓库里干活的 AI** 的工作须知，不是用户文档。面向使用者的说明见 `README.md`。

下面写的是**现状**。`docs/PLAN.md` 是决策历史，正文 §1–§11 有相当一部分已被推翻——两者冲突时以本文件、`README.md` 和 `docs/PLAN.md` 的文末附录为准（见「文档优先级」）。

## 项目是什么

公网文件交换服务：**AI 用上传密钥上传，拿回一条免登录直链；谁拿到链接谁就能下载。**

- 下载**不需要任何凭证**：blob id 本身就是凭证（128 bit CSPRNG → 26 位 Crockford Base32，不可枚举）。
- 只有一种密钥：**上传密钥**。它只能上传——读不到、删不掉任何文件，连它自己刚传的也读不到。
- 磁盘上 AES-256-GCM 分块加密（1 MiB 块，每文件随机 DEK 由 KEK 包裹），元数据在 SQLite。
- 部署形态：Windows/Linux VPS 上跑单个 AOT exe，只监听 `127.0.0.1:8790`，公网入口 `https://<域名>/drop` 由 nginx 反代。不用 Docker、外部数据库、对象存储。
- 管理界面是 Vue 3 + Element Plus 单页，构建产物**内嵌进 exe**。

## 命令

```powershell
dotnet build AnyDrop.slnx                       # 编译；正常应 0 警告（两个工程都开了 TreatWarningsAsErrors）
dotnet run  --project tests/AnyDrop.Tests       # 跑全部后端测试
cd web && npm run typecheck && npm run test     # 前端类型检查 + Vitest
dotnet run --project src/AnyDrop.Server         # 本机起服务（需先有 anydrop.json，见 README 快速上手）
dotnet run --project src/AnyDrop.Server -- --set-password   # 交互式设管理员密码（≥16 位）

pwsh ./scripts/build-web.ps1                    # 前端构建 → 复制 dist + 生成 wwwroot.g.props
pwsh ./scripts/build-all.ps1                    # 前端 + 服务端一起发布（win-x64）
pwsh ./scripts/publish-win.ps1 -NoAot           # 没有原生链接器的退路（自包含单文件）
pwsh ./scripts/pack-win.ps1                     # 打可拷到 Windows VPS 的发布包（release/ + zip，默认非 AOT）
./scripts/build-web.sh && ./scripts/publish-linux.sh        # Linux 等价路径
```

### 命令层面的硬约束

- **`scripts/*.ps1` 必须在 PowerShell 7（`pwsh`）下跑。** 它们是无 BOM 的 UTF-8 且含中文，Windows PowerShell 5.1 按 ANSI(cp936) 解码后会在**解析阶段**抛出乱码语法错；`build-web.ps1` 还用了 .NET Core 2.0+ 才有的 `[System.IO.Path]::GetRelativePath`。`pack-win.ps1` 会在开头直接拦住。
- **后端测试用 `dotnet run`，不要用 `dotnet test`。** 框架是 xunit v3 + Microsoft.Testing.Platform **进程内**运行器；`dotnet test` 在本机受限环境下打不开父进程句柄。
- **服务正在运行时 `dotnet build` 会以 MSB3027 失败**（exe 被锁）。先停服务再编译。
- **AOT 不能跨操作系统交叉编译**：目标是什么系统就在那个系统上发布。Linux 用 `scripts/publish-linux.sh`。
- **跑 `pack-win.ps1` 前先停掉 `npm run dev`**：Vite 会占住 `node_modules` 里的原生模块，`npm ci` 会以 EPERM unlink 失败（脚本会提前检测 5173 端口并提示）。
- 测试临时目录写在仓库内 `.local-dev/test-runs/`（系统临时目录在本机沙箱下不可写）。`.local-dev/` 全是 gitignore 的草稿数据，**不要提交、也不要当成源码读**。

## 红线：安全与数据

改动这块之前先想清楚，这几条是产品的核心承诺。

1. **下载 id 就是凭证，绝不能进日志。** `logging.logLevel` 里 `Microsoft.AspNetCore`（逐请求日志会记完整 URL）与 `AnyDrop.Server.Storage.BlobStore`（GC 打印 `blobs/xx/<id>` 路径）**必须保持 `Warning`**。这是唯一挡住"日志文件变成可用链接清单"的护栏。
   - 注意这只是兜底：Warning/Error 级照常写盘且可能带 id（`BlobStore` 删除失败带 `{Path}`、`BlobService` 一致性错误带 `{BlobId}`、`Middleware` 未处理异常带原始路径）。`logs/` 应按**含凭证的文件**管理。
   - `deploy/service-run.cmd` 里的覆盖变量名**必须带 `ANYDROP__` 前缀**：裸写 `Logging__LogLevel__...` 会被 `anydrop.json` 盖掉（`anydrop.json` 在 `CreateSlimBuilder` 之后才加载，而构建器自带的无前缀环境变量源排在它前面，靠后的源赢）。
2. **密钥/密码不进代码、提交信息、日志和聊天。** 上传密钥只从环境变量读；`--set-password 明文` 这种命令行用法会打印告警（同机进程与 shell 历史可见）。
3. **上传密钥的哈希只存 SHA-256，明文只在创建时显示一次。** 别加"回显密钥"这类便利功能。
4. **下载响应类型固定 `application/octet-stream` + `Content-Disposition: attachment`。** 不能让上传者声明的 Content-Type 决定下载响应类型——上传一个 `text/html` 就能在同源下渲染。响应还要带 `Referrer-Policy: no-referrer` 与 `X-Robots-Tag: noindex`。
5. **`master.key` 丢失 = 所有密文永久无法解密。** 它在**首次启动**服务时生成（`--set-password` 不建它）。别把它写进仓库、别改默认位置。
6. **`data/` 里不能有明文。** 测试里已有"磁盘上检索不到明文特征串"的断言，别破坏它。
7. **数据库结构版本是 v2（`PRAGMA user_version = 2`），不提供迁移。** 版本不匹配或无版本号的旧库会**拒绝启动**并提示删 data 目录——这是有意为之，不要"顺手"加迁移。

## 承重实现约束（看着可以优化，其实不能动）

- **Serilog 只作为一个 `ILoggerProvider` 注册进 DI 单例**，不替换 `ILoggerFactory`。两个坑都踩过（`Program.cs` 有详细注释）：
  - 不能用 `Logging.AddSerilog(...)`：那样加进来的 provider **不受 MS 级别过滤器约束**，逐请求日志会带着 id 落盘，第 1 条红线直接失效。
  - 不能用 `Logging.AddProvider(实例)`：那样注册的 provider **永远不会被释放**，`dispose: true` 空转，buffered 时优雅退出会丢整个缓冲区。
- **中间件顺序**（`Program.cs`）：`UsePathBase` → `UseSecurityHeaders` → `UseApiErrorHandling` → **显式** `UseRouting` → 各 `Map*Endpoints`。显式 `UseRouting` 是必须的：否则 `WebApplication` 会把它自动插到管道最前，带 `/drop` 前缀的请求先落进兜底端点，轮不到剥前缀。
- **`pathBase` 与前端 `ANYDROP_WEB_BASE` 必须完全一致。** 前端产物里资源路径写死 `/drop/...`，不一致会白屏。本机调试也用 `/drop`。
- **构建顺序是先检查后构建**：`npm ci → vue-tsc → vitest → vite build → 复制 dist + 生成 wwwroot.g.props → dotnet publish`。任一步失败都算"构建未完成"，此时 `wwwroot/` 与 `wwwroot.g.props` 状态不明，**不要发布**。
- **配额必须原子预占**（`TryReserveQuotaAsync`），blob 行与幂等键同一事务写入。管理端上传（`token is null`）整段跳过配额与幂等——否则超管会被自己（不存在的）配额挡在 507 外面。
- **删除时扣减配额只在"删行确实由我完成"时执行**：`DeleteBlobAsync` 的返回值是唯一的并发闸门。无条件扣减会让并发删除各扣一次，导致 `used_bytes` 低于现存 blob 之和，密钥能超额占盘。
- **下载路径发现密文与元数据不一致时，必须在写任何字节之前返回 500 `integrity_error`**；正文已开始后的失败改为 `context.Abort()`，不能把"截断的 200"当成功。
- **`TreatWarningsAsErrors=true` + AOT/trim 分析器全开**：留 0 警告。`JsonSerializerIsReflectionEnabledByDefault=false`，**新增任何 JSON DTO 都要登记到 `JsonContext.cs` 的 `AppJsonContext`**，否则 AOT 下运行期才炸。
- **`MapFallback` 的 `:nonfile` 约束不匹配带扩展名的请求**，所以 `/assets/{**path}` 必须显式映射（`PageEndpoints.cs` 里有注释）。
- **CSRF**：所有管理端写操作（含 logout）必须校验 `X-CSRF-Token`。管理会话存在**进程内**（重启即失效，单实例部署）。

## 架构地图

```
src/AnyDrop.Server/          ASP.NET Core minimal API（AOT 单文件）
  Program.cs                 组合根：配置加载、Serilog 接线、中间件顺序、路由注册
  AppConfig.cs               配置模型 + ConfigLoader（启动期严格校验）
  Errors.cs                  ErrorCodes / ApiErrorEnvelope / ServiceResult / ApiErrors
  JsonContext.cs             System.Text.Json 源生成上下文（新增 DTO 必须登记）
  Middleware.cs              安全响应头（CSP 等）+ 统一异常→错误信封
  Cli.cs                     --set-password / --set-password-stdin
  Crypto/                    分块 AEAD（BlobFormat）、主密钥（KeyRing）、口令哈希（SecretHasher）、Id/时间（Primitives）
  Storage/                   SqliteIndex（元数据、建表、user_version）、BlobStore（布局与原子写）、Records
  Services/                  BlobService（业务编排）、TokenService、AdminAuth（+SessionStore）、RateLimiter
  Api/                       Blob / Admin / Health / Page 端点
  Pages/                     download.html、ui-not-built.html（服务端直出，不依赖前端构建）
  Logging/LogFileSetup.cs    Serilog 文件日志的组装
  wwwroot/ + wwwroot.g.props 前端内嵌产物（**gitignore，构建生成**）
web/                         Vue 3 + Vite + Element Plus + TS 管理界面
tests/AnyDrop.Tests/         xunit v3（进程内运行器），TestApp/TestHttp 是集成测试脚手架
capabilities/                给 agent 用的能力：capabilities/anydrop/（SKILL.md + 纯标准库 Python CLI）
scripts/                     构建、发布、打包、取件示例
deploy/                      nginx 样板、Windows 服务脚本、RUNBOOK-WINDOWS.md、第三方许可
docs/                        接口说明（AGENT_UPLOAD.md）、部署（DEPLOY.md）、计划（PLAN.md）
```

**分层职责不要串**：`Api` 只负责 HTTP 契约与错误码；`Services` 负责鉴权后的业务规则（不产生 HTTP 状态码）；`Storage` 不加密；`Crypto` 不碰路径与鉴权；`BlobStore` 只和文件系统打交道。

磁盘布局：`data/blobs/{id 前 2 位}/{id}` = `header(magic|ver|chunkSize|fileNonce|keyVersion) + N×(密文||16B tag)`，nonce = `fileNonce(4B) || 块序号(8B BE)`。上传先写 `data/tmp/*.part`，`fsync` 后 `rename` 到最终路径（`Commit` **不覆盖**已存在文件，id 撞车换 id 重试）。

## 接口速查

- 公开：`POST /v1/blobs`（Bearer 上传密钥 + `X-Filename` 或 `?name=` + 原始字节体，**必须带 Content-Length**，不接受 chunked → 411）、`GET|HEAD /v1/blobs/{id}`（可带 `Range` → 206）、`GET /f/{id}`（人类信息页）、`GET /healthz`。
- 管理（cookie 会话 + CSRF）：`/api/admin/{session,login,logout,files,files/{id}/expiry,files/{id}/pin,tokens,tokens/{id}/revoke,audit}`。
- 上传返回的 `url` 是**直链** `{publicBaseUrl}/v1/blobs/{id}`，不要自己拼链接、也不要把 `/f/` 改写成 `/v1/blobs/`（`/f/` 只是可选的人类视图，没有任何界面链接它）。
- 错误统一 `{"error":{"code","message"}}`，`message` 是中文可直接展示。code 清单见 `Errors.cs`；`scope_denied` 已随 keyless 重构删除。
- 时区口径是刻意的：管理面板按**浏览器本地时区**，`/f/{id}` 信息页固定 **UTC** 并标注。格式相同、时区不同，别"统一"掉。

## 代码约定

- **注释、文档、测试方法名、提交信息都用中文。** 测试方法名就是中文完整句子（例：`上传成功返回完整信息且磁盘上只有密文`）——跟着写，别引入英文命名风格。
- **注释解释"为什么"，尤其记录踩过的坑与实测结论**（现有代码大量如此，例如为什么不能用 `AddSerilog`、为什么 nginx `.conf` 不能补 BOM、为什么 5.1 解析失败）。新增非显然的决定请照此留痕。
- 前端：Vue 3 `<script setup>` + TS；API 调用走 `web/src/api/client.ts`（统一 `basePath()` 前缀拼接、`ApiError` 映射）；Element Plus 按需自动引入，`auto-imports.d.ts` / `components.d.ts` 是构建生成但**入库**的（否则新克隆无法 `typecheck`）。
- 前端只使用 cookie 会话，**没有 Bearer 用法**（与 AI 侧的接口用法不同）。
- 配置：新增键要有默认值与范围校验，写错要在**启动时**报错而不是静默回落。注意既有 `Bool/Int/Long` 辅助方法对非法值是**静默回落默认值**的（保持原语义，别改，会破坏既有配置兼容性）；新键请用 `RequireParsable` 那套严格策略。

## AI 能力目录（Python，零依赖）

`capabilities/<名字>/` 是"给 agent 用的能力"的家：根上放 `SKILL.md`（于是这个目录本身就是一个合法的 skill bundle），
工具代码放子目录。目前只有 `capabilities/anydrop/`。

```
capabilities/anydrop/
  SKILL.md             给模型看的「何时用、怎么用、边界」
  README.md            给人看的：设计理由、密钥来源、将来接 MCP 的配置片段
  scripts/anydrop.py   CLI（core + files + keyring）
  scripts/tests/       单测
```

```powershell
python capabilities/anydrop/scripts/tests/test_anydrop.py    # 零依赖，标准库 unittest
```

三条承重约束，改之前先读 `capabilities/anydrop/README.md`：

1. **只用标准库。** 这是随仓库分发的脚本，目标机器不该为它准备 venv 或依赖树。
2. **密钥拿不到环境变量。** `@deepseek-ai/dsh-subprocess` 的 `SENSITIVE_ENV_PATTERN = /KEY|PASSWORD|SECRET|TOKEN/i`
   会把匹配的环境变量从**所有**子进程里清掉（shell 与 MCP stdio 一样），`ANYDROP_KEY` 正好命中。
   所以 `resolve_key()` 有 Windows 注册表 `HKCU\Environment` 回退——**只读自己那两个名字，绝不枚举整个键**。
3. **必须显式带 Content-Length、不能分块。** 服务端对分块传输回 `411 length_required`。
   这也是用 `http.client` 手工 `putheader` 而不是 `urllib` 的原因。

约定与仓库其余部分一致：中文注释、中文测试方法名、注释解释"为什么"。
`.local-dev/python-tests/` 是测试的临时目录（gitignore），**不用系统临时目录**——与
`tests/AnyDrop.Tests/TestPaths.cs` 同一条理由。

⚠️ **这份 skill 不会被自动加载**：它不在任何被扫描的技能根里。启用方式是把它所在父目录加进
agent 的技能根（DSH 用 `customSkillDirs: ["<仓库>/capabilities"]`）。同理，`~/.dsh/skills/anydrop/`
里还留着一份**旧副本**（仍讲裸 HTTP），与新正本说法不同——这是有意暂缓清理的遗留状态。

## 常见坑与排障

| 现象 | 原因 |
|---|---|
| 管理页白屏 | `pathBase` 与前端 `ANYDROP_WEB_BASE` 不一致，或前端产物没跟着重新构建 |
| 503 `ui_not_built` | 发布时没带内嵌前端：重跑 `build-web.ps1` 再 publish |
| 上传 413 | nginx `client_max_body_size` 小于服务端 `maxUploadBytes` |
| 大文件上传卡住 | nginx 没关 `proxy_request_buffering`，或 `proxy_read_timeout` 太短 |
| HTTP 下登录状态丢失 | 会话 cookie 带 `Secure`，只在确实 HTTPS（或 `X-Forwarded-Proto: https`）时才打 |
| 启动即失败、应用日志文件没生成 | 先看 `logs/bootstrap.log`（JSON 写坏 / 漏拷 `e_sqlite3.dll` / exe 被搬走） |
| 旧库拒绝启动 | 结构版本不匹配，按提示删 data 目录（无迁移） |
| 前端 E2E | **没有** Playwright，靠人工端到端验证；这是已知未覆盖项 |

日志验收：`Select-String -Path logs/server-*.log -Pattern 'Request starting'` 应**无输出**（证明逐请求日志已关闭，**不**证明日志里一定没有 id）。每条日志以一行开始，带异常的条目后面跟多行堆栈——别拿行数当事件数。

## 文档优先级

1. **本文件** —— 给 AI 的操作约束。
2. **`README.md`** —— 用户视角的现状说明（快速上手、密钥模型、安全边界、常用命令）。
3. **`docs/DEPLOY.md`**、**`deploy/RUNBOOK-WINDOWS.md`** —— 部署现状。
4. **`docs/AGENT_UPLOAD.md`** —— 对外接口契约现状（会被打进发布包）。
5. **`docs/PLAN.md`** —— ⚠ **决策历史，不是现状描述。** 正文 §1–§11 中「读取密钥 / 能力位 / 命名空间 / 下载会话 / `POST /api/session` / `sessionDays` / `scope_denied` / 下载需鉴权 / `url` 指向 `/f/{id}`」等表述**全部已作废**，实际模型以文末两段附录（实施修正记录、keyless 下载重构「结构 v2」）为准。正文保留原样是为了留决策历史——**不要照抄正文，也不要"修正"它**。

## Git

- 远端：`https://github.com/f-shake/AnyDrop.git`。
- 提交信息风格：Conventional Commits + **中文描述**，例：`feat: 服务端自写文件日志，包装脚本日志分流`、`refactor!: 下载改为免密钥直链，删除读取密钥与命名空间（结构 v2）`。破坏性变更用 `!`。
- 分支：`main` 与 `feat/*`。写作时当前分支是 `feat/keyless-download`，**领先 `origin/main` 4 个提交**（keyless 重构、移动端适配、Windows 部署资产、文件日志都还没进 `main`）。动 `main` 之前先确认这条拓扑。
- 一次提交尽量对齐现有粒度：一个功能主题 + 对应的测试与文档同步更新（历史提交普遍是 `src` + `web` + `tests` + `docs` 一起改）。

## 一个容易混的角色问题

本仓库**就是**那个投递服务。而 DSH 里有个 `anydrop` 技能，作用是"把本地文件变成直链交付给用户"，它是**这个服务的客户端**，打的是公网 `https://fshake.com/drop`。在本仓库里开发/调试时不要把它们混起来：本地调试实例在 `http://127.0.0.1:8790/drop`，且本机配置需要 `cookieSecure=false`。
