# 部署到公网 VPS

前提：域名（本文以 `fshake.com` 为例）已解析到 VPS；443 上已有 nginx 在跑并持有证书
（AnyDrop 不自带 TLS 终结）；VPS 上可以放一个可执行文件。

## 1. 构建

Native AOT **不能跨操作系统交叉编译**：目标是什么系统，就在那个系统上发布。
另外 AOT 需要本机有原生链接器：Windows 需要 Visual Studio 的「使用 C++ 的桌面开发」工作负载，
Linux 需要 `clang` + `zlib1g-dev`。缺工具链时可用 `-NoAot` 退回自包含单文件（照样一个 exe，只是更大、启动稍慢）。

- VPS 是 Windows（在开发机上发布即可）：

  ```powershell
  pwsh ./scripts/build-web.ps1     # 前端构建 + 生成内嵌资源清单 wwwroot.g.props
  pwsh ./scripts/publish-win.ps1   # → publish/AnyDrop.Server.exe（AOT）
  pwsh ./scripts/publish-win.ps1 -NoAot   # 没有 C++ 工具链时的退路
  ```

  > ⚠ 这几个脚本**必须在 PowerShell 7（`pwsh`）下运行**，Windows PowerShell 5.1 不行，而且报错
  > 很难看懂：`build-web.ps1` / `publish-win.ps1` / `build-all.ps1` 是无 BOM 的 UTF-8 且含中文注释，
  > 5.1 按 ANSI(cp936) 解码后引号会错位，**在解析阶段**就抛乱码语法错；另外 `build-web.ps1` 用了
  > `[System.IO.Path]::GetRelativePath`，那是 .NET Core 2.0+ 的 API，5.1（.NET Framework）没有。
  > `pack-win.ps1` 会在开头直接拦住并提示改用 `pwsh`。

  > ⚠ **AOT 未验证的一环**：文件日志用的 Serilog 在**编译期**实测 0 警告（连 IL 分析器都跑过），
  > 但**原生 AOT 的 ILC 链接阶段未在本机验证**（本机没有原生链接器，`-NoAot` 是唯一能跑的路径）。
  > 首次在有工具链的机器上跑不带 `-NoAot` 的发布时，请留意 IL2xxx/IL3xxx 警告 ——
  > `NoWarn` 只覆盖 IL2026/IL3050。真在 ILC 阶段失败的话，退路是在 `Logging/LogFileSetup.cs`
  > 之外改用一个自写的 `ILoggerProvider`（不引入 Serilog 的 sink 包）。

  想一步到位（构建 + 发布 + 组装运维脚本 + 打 zip）：

  ```powershell
  pwsh ./scripts/pack-win.ps1      # → release/AnyDrop/ 与 release/AnyDrop-win-x64-<提交>.zip
  ```

  > `pack-win.ps1` 默认走非 AOT；跑之前**先停掉 `npm run dev`** —— Vite 会占着 `node_modules`
  > 里的原生模块，`npm ci` 会以 EPERM unlink 失败（脚本会提前拦住并提示）。

- VPS 是 Linux：把源码放到 Linux 上，用等价 shell 脚本一步到位（会自己生成 `wwwroot.g.props`）：

  ```bash
  ./scripts/build-web.sh          # 等价于 build-web.ps1：npm ci → 类型检查 → 单测 → build → 生成清单
  ./scripts/publish-linux.sh      # → publish/AnyDrop.Server
  ```

## 2. 拷贝到 VPS

```
C:\AnyDrop\AnyDrop.Server.exe        发布产物
C:\AnyDrop\e_sqlite3.dll             SQLite 原生库，必须一起拷（Microsoft.Data.Sqlite 的依赖）
C:\AnyDrop\anydrop.json              从 anydrop.json.sample 复制后修改
C:\AnyDrop\run.cmd                   可选，手动启动用（发布包里**不含**它；要用手工从 scripts\run.cmd 拷）
```

`anydrop.json` 关键项：`server.pathBase = "/drop"`、`server.publicBaseUrl = "https://fshake.com/drop"`、
`server.cookieSecure = true`、`storage.dataDir = "data"`、
`logging.logLevel` 里把 `Microsoft.AspNetCore` 与 `AnyDrop.Server.Storage.BlobStore` 提到 `Warning`（见第 4 步）。

> 前缀是**前后端一起约定**的：前端构建产物里的资源地址写死了 `/drop/...`。
> 要换前缀，必须用 `ANYDROP_WEB_BASE=/新前缀/ scripts/build-web.sh`（或 PowerShell 里设同名环境变量）
> 重新构建前端，并把后端 `server.pathBase`/`publicBaseUrl` 一起改掉。

## 3. 初始化管理员密码

```powershell
cd C:\AnyDrop
.\AnyDrop.Server.exe --set-password            # 交互式输入（掩码），推荐
# 或从标准输入读（脚本/CI 用）：
"你的长密码" | .\AnyDrop.Server.exe --set-password-stdin
```

**不要**把密码写成命令行参数（`--set-password 密码`）：同机其他进程与 shell 历史都能看到它，
该用法会打印告警但仍会执行。

未设置密码时管理界面登录会返回 `503 admin_not_initialized`（接口不受影响）。

## 4. 启动

```powershell
.\AnyDrop.Server.exe  # 发布包里没有 run.cmd，直接跑 exe 即可（手工拷过 scripts\run.cmd 才用 .\run.cmd）
```

启动日志会打印 `urls / pathBase / publicBaseUrl / dataDir / 内嵌前端`。
`内嵌前端=False` 说明这次发布没带前端产物，管理页会显示"管理界面未构建"。

首次启动会在 `data/` 下生成 `master.key`（Windows 上请自行确认 ACL 只对该账号可读）
与 `anydrop.db`。停止：前台 Ctrl+C。

> **逐请求日志里不能出现下载 id。** 下载链接里的 id 就是下载凭证，而 ASP.NET Core 默认会把每个
> 请求的完整 URL 记进 Information 日志 —— 日志文件于是变成一份可用的链接清单。样板配置已在
> `logging.logLevel` 里把 `Microsoft.AspNetCore` 提到 `Warning`（逐请求日志），把
> `AnyDrop.Server.Storage.BlobStore` 也提到 `Warning`（GC 回收孤儿密文时会打印
> `blobs/xx/<id>` 这样的路径，而密文文件名就是 id —— 那条是 Information 级）。用
> `deploy/service-run.cmd` 启动时还会再设一遍同类过滤器（那是对付"`anydrop.json` 被换掉"的兜底，
> 只覆盖这一条 Information 级日志，不改变下面的残留风险），**名字必须带 `ANYDROP__` 前缀**：
> 裸写的 `Logging__LogLevel__...` 会被 `anydrop.json` 盖掉、等于没设 —— 实测（用发包里的 exe，
> 把 `anydrop.json` 改成 `Information`，发一个请求再看日志：裸名字下照常出现 `Request starting`）。
> 原因见 `deploy/service-run.cmd` 的注释：`anydrop.json` 是在 `CreateSlimBuilder` **之后**才加进去的，
> 而构建器自带的无前缀环境变量源排在它**前面**，靠后的源赢。
>
> ⚠ 但这只挡得住 Information：**Warning/Error 级照常写盘，而且可能带 id** —— `BlobStore` 的
> 删除/回退失败警告带 `{Path}`（`blobs/xx/<id>`）、`BlobService` 的一致性与打开失败错误带
> `{BlobId}`、`Middleware` 的未处理异常带原始请求路径。所以 `logs/` 下的日志文件应当按
> **"含凭证的文件"**管理：别放共享盘、别整份外发。要彻底消除得改代码不再打印 id，目前没做。
>
> **文件日志由应用自己写**：`logs/server-<日期>.log`（默认 `logging:file:rollingInterval=Day` →
> `server-20261007.log`；改成 `Infinite` 就没有日期段，文件名就是 `server-.log`），保留份数与单文件
> 上限由 `logging:file` 控制（默认 14 份 / 32 MiB，到大小上限换新文件）。`service-run.cmd` 另把进程的
> stdout/stderr 重定向到 `logs/bootstrap.log`，用来兜住"日志系统起来之前就失败"的情况
> （JSON 写坏、漏拷 `e_sqlite3.dll`、exe 被搬走 —— 这几种情况下**应用日志文件根本不会被创建**，
> 所以排障时先看 `bootstrap.log`）。
> **每条日志以一行开始**（本地时间戳 + 时区 + 级别 + 来源类别 + 消息），例如
> `2026-10-07 13:16:35.710 +08:00 [INF] AnyDrop.Server: …`；**带异常的条目后面会跟堆栈多行**，
> 所以别拿"行数"当"事件数"。
>
> 验收：`Select-String -Path logs/server-*.log -Pattern 'Request starting'` 应当**没有输出**
> （它证明的是"逐请求日志已关闭"，而不是"日志里一定没有 id"）。

> Windows 上还要解决"会话注销就没了"和"重启不自启"：用 `deploy/install-service.ps1` 注册成
> 计划任务，详见 `deploy/RUNBOOK-WINDOWS.md`（那份是从拿到发布包到公网可用的完整路书）。

## 5. 配置 nginx

把 `deploy/nginx-anydrop.conf.sample` 里的 `location` 片段放进 `fshake.com:443` 的 `server{}`：

```bash
nginx -t && nginx -s reload
```

必须保留的三项（否则会出现"能上传小文件、大文件 413/卡死"这类问题）：
`client_max_body_size`（≥ 服务端上限）、`proxy_request_buffering off`、`proxy_read_timeout`。

> 别把同一个 `location /drop/` 也贴进 80 端口的 `server{}`：会话 cookie 的 `Secure` 标志是按请求判定
> 的，只有确实走 HTTPS（也就是 nginx 传了 `X-Forwarded-Proto: https`）时才打上。80 端口也能到达
> 服务的话，登录会发下一个不带 `Secure` 的会话 cookie。

## 6. 验收

```bash
curl -sS https://fshake.com/drop/healthz
# → {"status":"ok","diskFreeBytes":…,"blobCount":0,"uptimeSeconds":…}
```

1. 浏览器打开 `https://fshake.com/drop/admin`，用第 3 步的密码登录。
2. 在"上传密钥"里新建一把密钥，把显示的 key 存进 AI 所在机器的环境变量。
3. 让 AI 上传一个文件，把返回的 `url`（直链）发给自己——**直接打开就开始下载，不需要任何密钥**，核对 sha256。
   反过来也一样：在管理面板里上传一个文件，把面板给的直链发给 AI，AI 直接 curl 就能取到。

## 7. 备份与升级

- 需要备份的只有两样：`data/`（`anydrop.db` + `blobs/`）与 `data/master.key`
  （`master.key` 是**首次启动**服务时生成的，`--set-password` 不会建它 —— 所以备份要放在启动过一次之后）。
- **`master.key` 丢失 = 所有密文永久无法解密**，建议离线单独存一份。
- 升级：停进程 → 覆盖 exe → 启动。表结构由程序启动时建立，**当前结构版本 v2**：
  程序会写 `PRAGMA user_version`，遇到版本不匹配的旧库会拒绝启动并提示删除 data 目录（不自动迁移）。

## 8. 常见问题

| 现象 | 原因与处理 |
| --- | --- |
| 上传返回 413 | nginx 的 `client_max_body_size` 小于服务端 `maxUploadBytes` |
| 从 HTTPS 换到 HTTP 后变成未登录 | 正常：会话 cookie 带 `Secure`，浏览器不会把它发到 http。统一用 https |
| HTTP 访问管理页也能登录 | 说明 80 端口也代理到了服务：此时发出的会话 cookie 不带 `Secure`（`Secure` 只在确实 HTTPS 时才打，见 `Services/AdminAuth.cs`）。80 端口应只做 301 跳转到 https |
| 大文件上传卡住 | nginx 没关 `proxy_request_buffering`，或 `proxy_read_timeout` 太短 |
| 管理页 503「管理界面未构建」 | 发布时没带内嵌前端：重跑 `build-web.ps1` 再 publish |
| 下载 404 但文件刚传过 | 已过期被 GC 回收，或 id 被截断/改写过（id 本身就是凭证，必须完整） |
| 磁盘被占满 | 检查 `retention.defaultTtlDays`、各密钥配额，以及 `storage.minFreeBytes` 水位设置 |
