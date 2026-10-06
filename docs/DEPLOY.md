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
C:\AnyDrop\run.cmd                   可选，手动启动用
```

`anydrop.json` 关键项：`server.pathBase = "/drop"`、`server.publicBaseUrl = "https://fshake.com/drop"`、
`server.cookieSecure = true`、`storage.dataDir = "data"`。

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
.\run.cmd            # 或直接 .\AnyDrop.Server.exe
```

启动日志会打印 `urls / pathBase / publicBaseUrl / dataDir / 内嵌前端`。
`内嵌前端=False` 说明这次发布没带前端产物，管理页会显示"管理界面未构建"。

首次启动会在 `data/` 下生成 `master.key`（Windows 上请自行确认 ACL 只对该账号可读）
与 `anydrop.db`。停止：前台 Ctrl+C。

## 5. 配置 nginx

把 `deploy/nginx-anydrop.conf.sample` 里的 `location` 片段放进 `fshake.com:443` 的 `server{}`：

```bash
nginx -t && nginx -s reload
```

必须保留的三项（否则会出现"能上传小文件、大文件 413/卡死"这类问题）：
`client_max_body_size`（≥ 服务端上限）、`proxy_request_buffering off`、`proxy_read_timeout`。

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

- 需要备份的只有两样：`data/`（`anydrop.db` + `blobs/`）与 `data/master.key`。
- **`master.key` 丢失 = 所有密文永久无法解密**，建议离线单独存一份。
- 升级：停进程 → 覆盖 exe → 启动。表结构由程序启动时建立，**当前结构版本 v2**：
  程序会写 `PRAGMA user_version`，遇到版本不匹配的旧库会拒绝启动并提示删除 data 目录（不自动迁移）。

## 8. 常见问题

| 现象 | 原因与处理 |
| --- | --- |
| 上传返回 413 | nginx 的 `client_max_body_size` 小于服务端 `maxUploadBytes` |
| 登录成功后立刻又变回未登录 | `cookieSecure=true` 但你在用 HTTP 访问；要么配 HTTPS，要么临时改成 `false` |
| 大文件上传卡住 | nginx 没关 `proxy_request_buffering`，或 `proxy_read_timeout` 太短 |
| 管理页 503「管理界面未构建」 | 发布时没带内嵌前端：重跑 `build-web.ps1` 再 publish |
| 下载 404 但文件刚传过 | 已过期被 GC 回收，或 id 被截断/改写过（id 本身就是凭证，必须完整） |
| 磁盘被占满 | 检查 `retention.defaultTtlDays`、各密钥配额，以及 `storage.minFreeBytes` 水位设置 |
