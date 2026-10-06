# AnyDrop

公网文件交换服务：**AI 用环境变量里的写密钥上传、拿回 id 与链接；你用读密钥下载**。
磁盘上以 AES-256-GCM 分块加密保存，元数据在 SQLite；单文件 AOT 发布，手动启动即可。

- 部署形态：VPS 上直接跑一个 exe，公网入口 `https://<域名>/drop` 由服务器 nginx 反代到 `127.0.0.1:8790`。
- 不依赖 Docker、外部数据库或云端对象存储。
- 管理界面是 Vue 3 + Element Plus 单页，构建产物内嵌进 exe。

## 快速上手（本机调试）

**本地也用 `/drop` 前缀**：前端构建产物里的资源路径是写死的 `/drop/...`，与后端 `server.pathBase` 必须一致，
否则管理页会白屏。改前缀要同时改后端配置并用同一个值重新构建前端（`ANYDROP_WEB_BASE`）。

```powershell
# 1. 构建前端（会生成内嵌资源清单 wwwroot.g.props）
pwsh ./scripts/build-web.ps1 -SkipInstall   # 首次去掉 -SkipInstall

# 2. 设置管理员密码（交互式输入，不走命令行参数）
dotnet run --project src/AnyDrop.Server -- --set-password

# 3. 启动（本机 anydrop.json：pathBase=/drop、publicBaseUrl=http://127.0.0.1:8790/drop、cookieSecure=false）
dotnet run --project src/AnyDrop.Server
# 打开 http://127.0.0.1:8790/drop/admin
```

本机调试的 `anydrop.json` 只要把 `server.publicBaseUrl` 换成 `http://127.0.0.1:8790/drop`、
`server.cookieSecure` 设为 `false`（HTTP 下 Secure cookie 会被浏览器丢掉），**`pathBase` 保持 `/drop`**。

前端开发模式：`cd web && npm run dev`（已配置把 `/drop/v1`、`/drop/api` 代理到 `127.0.0.1:8790`，
所以后端也要用 `pathBase=/drop`）。

## 上传与下载

```bash
# AI 侧：上传（原始字节体，不要用 multipart）
curl -X POST https://fshake.com/drop/v1/blobs \
  -H "Authorization: Bearer $ANYDROP_KEY" \
  -H "X-Filename: report.md" \
  --data-binary @report.md
# → {"id":"...","url":"https://fshake.com/drop/f/...","sha256":"...","size":123,"expiresAt":"..."}

# 下载（读密钥）
curl -H "Authorization: Bearer $ANYDROP_READ_KEY" -o report.md \
  https://fshake.com/drop/v1/blobs/<id>
```

浏览器里直接打开返回的 `url`，输入读密钥即可下载（会话 cookie 记住 30 天）。
更详细的接口说明见 [docs/AGENT_UPLOAD.md](docs/AGENT_UPLOAD.md)，部署步骤见 [docs/DEPLOY.md](docs/DEPLOY.md)。

## 密钥模型

| 预设 | 能力 | 用途 |
| --- | --- | --- |
| `ai-write` | 只写 | 给 AI 的环境变量，泄漏也无法读走任何文件 |
| `me-read` | 只读 | 你自己取件 |
| `nas-pull` | 读 + 删 | NAS 按 id 取件，取完自删 |

密钥只存 SHA-256，创建时**只显示一次**；支持绑定命名空间、有效期、配额与单文件上限。
AI 会把密钥写进日志和报告里，所以按"必然泄漏"设计：一把密钥一个命名空间，只给必须的能力。

## 安全边界（请知悉）

- 服务端加密（KEK + 每文件随机 DEK）防的是 **VPS 磁盘/快照/备份被拿走**；
  它**不防**服务器被入侵，也不防超管本人——管理界面本来就能解密。
- 服务只监听 `127.0.0.1`，公网唯一入口是 nginx；**必须走 HTTPS**，明文链路会泄漏 Bearer 密钥。
- 密钥绝不放在 URL 里；下载链接只带不可猜的 id（26 位 Base32）。
- 数据目录里**不要**挂载 NAS 真实共享；它只是交换区，靠 TTL + 配额自动回收。

## 目录结构

```
src/AnyDrop.Server/   ASP.NET Core minimal API（AOT 单文件）
  Crypto/             分块 AEAD、主密钥、口令哈希
  Storage/            SQLite 元数据、密文文件布局与原子写
  Services/           业务编排、密钥、会话、限流
  Api/                上传下载、管理、页面、健康检查
  Pages/              服务端直出的下载页与"未构建前端"兜底页
  Maintenance/        过期与孤儿回收
web/                  Vue 3 + Vite + Element Plus + TS 管理界面
tests/AnyDrop.Tests/  xUnit v3（进程内运行器）
scripts/              构建、发布、取件示例
deploy/               nginx 样板、第三方许可
docs/                 计划、接口说明、部署步骤
```

## 常用命令

```powershell
dotnet build AnyDrop.slnx                       # 编译（含 AOT 分析器，0 警告）
dotnet run  --project tests/AnyDrop.Tests       # 跑全部测试（xunit v3 进程内运行器）
cd web && npm run typecheck && npm run test     # 前端类型检查与单测
pwsh ./scripts/build-all.ps1                    # 前端 + 服务端一起发布（win-x64）
./scripts/build-web.sh                          # Linux/macOS 等价的前端构建 + 内嵌清单生成
pwsh ./scripts/publish-win.ps1 -NoAot           # 没有原生链接器时退回自包含单文件
```

### 发布前置条件

- **Native AOT 需要本机有原生链接器**：Windows 需要 Visual Studio 的「使用 C++ 的桌面开发」工作负载；
  Linux 需要 `clang` 与 `zlib1g-dev`。缺工具链时 `publish-win.ps1` 会失败并提示改用 `-NoAot`。
- Linux 发布请在 Linux 上执行 `scripts/publish-linux.sh`：**AOT 不支持跨操作系统交叉编译**。
- 发布目录里除 `AnyDrop.Server.exe` 外还有 `e_sqlite3.dll`（SQLite 原生库），
  它是 `Microsoft.Data.Sqlite` 的运行时依赖，部署时要一起拷过去。
