# AnyDrop 上线路书（Windows Server + nginx 反代）

这份文档从「拿到本发布包」写到「公网可用」。每一步都给了**命令**和**你应该看到什么**；
看到的不一样就停下，对照第 11 步的排障表。

## 本包是什么

| 项 | 值 |
| --- | --- |
| 形态 | **非 AOT 自包含单文件**，`AnyDrop.Server.exe` 约 100 MB，启动 1~2 秒 |
| 运行依赖 | Windows Server 2016+ / Windows 10+；**不需要**预装 .NET 运行时 |
| 必需文件 | `AnyDrop.Server.exe`、`e_sqlite3.dll`、`anydrop.json`（三者必须同目录） |
| 为什么不 AOT | Native AOT 不能跨操作系统交叉编译，且没有原生链接器（VS 的「使用 C++ 的桌面开发」或 clang）时编译不出来。功能完全一致，代价只有体积与启动时间 |
| 已预填域名 | `fshake.com`（DNS 已指向 111.229.139.140，nginx 1.26.2 已在 443 上跑并持有证书） |

---

## 0. 上线前自查（VPS 上，管理员 PowerShell）

```powershell
# 磁盘剩余：storage.minFreeBytes 默认 2 GiB，低于水位线会**直接拒绝上传**
Get-PSDrive C | Select-Object Name, @{n='FreeGB'; e={[math]::Round($_.Free/1GB,1)}}

# 8790 现在不该被任何进程占用（占用说明你之前已经起过一个）
Get-NetTCPConnection -LocalPort 8790 -State Listen -ErrorAction SilentlyContinue

# 系统时间与时区（过期时间按 UTC 存，排障时对时间敏感）
Get-Date; (Get-TimeZone).Id
```

**你应该看到**：`FreeGB` 明显大于 2；8790 没有任何输出。

---

## 1. 传文件并解压

把 `AnyDrop-win-x64-<提交>.zip` 传到 VPS，解压到 `C:\`：

```powershell
# 例：zip 在 C:\Users\你\Downloads。文件名**写全** —— 用 *.zip 通配符的话，
# 一旦 Downloads 里留着两个版本的包（或一个没压完的残包），解压出来的就不是你以为的那个。
Get-ChildItem "$env:USERPROFILE\Downloads\AnyDrop-win-x64-*.zip" | Select-Object Name, Length
Expand-Archive -Path "$env:USERPROFILE\Downloads\AnyDrop-win-x64-<提交>.zip" -DestinationPath 'C:\' -Force

# 确认目录内容（zip 里带顶层 AnyDrop 目录，所以解压出来就是 C:\AnyDrop）
Get-ChildItem C:\AnyDrop | Select-Object Name, @{n='MB'; e={[math]::Round($_.Length/1MB,2)}}
```

**你应该看到**至少有：`AnyDrop.Server.exe`、`e_sqlite3.dll`、`anydrop.json`、`service-run.cmd`、
`install-service.ps1`、`nginx-anydrop.conf.sample`、`RUNBOOK-WINDOWS.md`、`AGENT_UPLOAD.md`、`BUILD-INFO.txt`。

> `BUILD-INFO.txt` 里有本次包的提交号与 exe 的 SHA256。要核对文件没在传输中损坏：
> ```powershell
> (Get-FileHash C:\AnyDrop\AnyDrop.Server.exe -Algorithm SHA256).Hash
> ```

---

## 2. 核对 `C:\AnyDrop\anydrop.json`

```powershell
Get-Content C:\AnyDrop\anydrop.json
```

要确认这 5 项（**已经按公网形态填好，一般不用改**）：

| 键 | 值 | 为什么必须是这个 |
| --- | --- | --- |
| `server.urls` | `http://127.0.0.1:8790` | 只监听回环。公网进不来，只能经 nginx；同时因为直连方是回环，服务端才会信任 nginx 写的 `X-Real-IP`（限流按真实 IP 生效） |
| `server.publicBaseUrl` | `https://fshake.com/drop` | 上传接口返回的直链前缀。**填错 = 发给别人的链接全是坏的** |
| `server.pathBase` | `/drop` | 前端资源路径在构建时写死为 `/drop/...`，改它必须用 `ANYDROP_WEB_BASE` 重新构建前端 |
| `server.cookieSecure` | `true` | 管理会话 cookie 只在 HTTPS 下发送。用 http 访问会表现为"登录成功后立刻变回未登录" |
| `logging.logLevel.Microsoft.AspNetCore` | `Warning` | **安全相关**：`Information` 会把每个请求的完整 URL（含下载 id）写进日志，而 id 就是下载凭证 —— 日志文件等于链接清单。这里关掉 |

`logging` 里还有一条 `AnyDrop.Server.Storage.BlobStore: Warning`：GC 回收孤儿密文时会打印
`blobs/xx/<id>` 这样的路径（密文文件名就是 id），那条是 Information 级，靠这条压掉。

⚠ **别把这两条过滤器读成"日志里保证不会出现 id"。** 它们压掉的只是 Information 级的逐请求日志；
**Warning 与 Error 级照常写盘，而里面就可能带 id**。源码里至少三处：

| 位置 | 级别 | 带的东西 |
| --- | --- | --- |
| `BlobStore.cs` 删除/回退密文失败 | Warning | `{Path}` = `blobs/xx/<id>`（这条类别**在**过滤名单里，但设的级别就是 Warning，等于放行） |
| `BlobService.cs` 密文与元数据不一致、打开密文失败、审计写入失败 | Error / Warning | `{BlobId}` 或 `{Path}`（类别**不在**过滤名单里） |
| `Middleware.cs` 未处理异常 | Error | `{Path}` = 原始请求路径，含 id |

所以 **`logs\server.log` 要当成"含凭证的文件"对待**：别放共享盘，别贴进聊天/工单/Issue，
要给人看排障时只贴相关几行。彻底的做法是改代码不再打印 id（本版未做）。

⚠ **如果你要给 `service-run.cmd` 加环境变量，名字必须带 `ANYDROP__` 前缀**
（`ANYDROP__Logging__LogLevel__...`）。写成裸的 `Logging__LogLevel__...` **完全没有作用** ——
实测（用本包里的 exe，把 `anydrop.json` 改成 `Information`，发一个请求再看日志）：裸名字下日志里
照常出现 `Request starting`，带前缀才压得住。原因：`Program.cs` 是在 `CreateSlimBuilder` **之后**
才 `AddJsonFile(anydrop.json)`，而构建器自带的那个「无前缀环境变量」源排在它**前面** ——
靠后的源赢，所以 JSON 盖掉了裸名字；后加的 `ANYDROP__` 前缀源排在**最后**，才压得住 JSON。
`service-run.cmd` 里用的就是带前缀的写法，别改回裸的。

改完 `anydrop.json` 可以先验一眼 JSON 语法（**只读文件，不启动服务**）：

```powershell
try { Get-Content C:\AnyDrop\anydrop.json -Raw | ConvertFrom-Json -ErrorAction Stop | Out-Null; 'JSON 语法 OK' }
catch { 'JSON 语法错误：' + $_.Exception.Message }
```

> `-ErrorAction Stop` 与 `try/catch` 不是啰嗦：`ConvertFrom-Json` 碰到坏 JSON 抛的是**非终止错误**。
> 写成 `… | ConvertFrom-Json | Out-Null; 'JSON 语法 OK'` 的话，**JSON 坏掉时它照样打印
> "JSON 语法 OK" 并且退出码为 0**（pwsh 7 与 Windows PowerShell 5.1 实测都一样）—— 那就又是一句
> 假绿灯，而这条命令的全部价值就在于给出明确判据。

> ⚠ 这个程序**没有**"只校验配置"的命令行开关：它只认 `--set-password` 与 `--set-password-stdin`
> （见 `src/AnyDrop.Server/Program.cs`），**其余参数一律忽略并正常启动服务**。所以别拿 `--help`
> 之类的东西去"试配置" —— 那等于又起一个实例，会和正在跑的服务抢 8790；而 `| Select-Object -First 3`
> 这种管道会在拿到几行后把进程直接掐掉，看起来像"它自己正常退出了"，其实是假的。
>
> 配置的**语义**校验只发生在启动时：写错会在启动日志里以人话报错并退出，不会静默。
> 所以最终判据还是第 5 步的前台试跑。

---

## 3. 设置管理员密码

```powershell
cd C:\AnyDrop
.\AnyDrop.Server.exe --set-password
```

提示 `设置管理员密码（用户名 admin，至少 16 位）：` 时输入（**输入不回显**），回车。

**你应该看到**：`管理员密码已写入 C:\AnyDrop\data\anydrop.db`

> - 别用 `--set-password 密码` 这种写法：同机其他进程和 shell 历史都能看到它。
> - **位数下限是 16**（源码 `AdminAuth.MinPasswordLength`）：少于 16 位会被拒绝并要求重来。
> - 这一步创建 `data\anydrop.db`（如果还没有）。
> - **`data\master.key` 不是这一步生成的**：它由第一次启动服务时创建。实测 —— 跑完 `--set-password`
>   目录里只有 `anydrop.db`，启动过一次之后才出现 `master.key`。所以第 10 步的备份要放在**首次启动之后**做。
> - 密码忘了不用删库：停掉服务后再跑一次这个命令就是覆盖设置。
> - **`master.key` 丢了 = 所有已加密文件永久无法解密**，见第 10 步。

---

## 4. 收紧数据目录权限

`data\` 里是主密钥与全部密文，只应让服务账号和本机管理员读到：

```powershell
# 用 SID 而不是组名，避免非英文版 Windows 上的名字差异
icacls C:\AnyDrop\data /inheritance:r /grant "*S-1-5-18:(OI)(CI)F" /grant "*S-1-5-32-544:(OI)(CI)F"

# 复核
icacls C:\AnyDrop\data
```

**你应该看到**：`已成功处理 1 个文件`（或 2 个），列出的授权只有 `NT AUTHORITY\SYSTEM` 与
`BUILTIN\Administrators`，且带 `(OI)(CI)(F)`。

> `S-1-5-18` = SYSTEM，`S-1-5-32-544` = Administrators。
> 如果你打算让服务用别的账号跑，把那个账号一起加进来，否则它读不到主密钥。

---

## 5. 前台试跑一次（**先别装服务**）

```powershell
cd C:\AnyDrop
.\AnyDrop.Server.exe
```

**你应该看到**（三行关键信息）：

```
内嵌前端资源 5 个（index.html 存在：True）
AnyDrop 启动：urls=http://127.0.0.1:8790 pathBase='/drop' publicBaseUrl=https://fshake.com/drop dataDir=C:\AnyDrop\data 内嵌前端=True
Now listening on: http://127.0.0.1:8790
```

> 这三行的格式是从本发布包实测出来的。`内嵌前端资源 5 个` = 本次前端产物（index.html + 2 个 CSS
> + 2 个 JS）的数量；数量对不上说明 wwwroot 里的东西不是这次构建的产物。

⚠ **`内嵌前端=True` 必须是 True**。如果是 `False`，管理页会显示「管理界面未构建」，
说明这个发布包没带上前端产物 —— 别继续，重新打包。

⚠ 全程**不应该出现** `Request starting HTTP/1.1 GET ...` 这类逐请求日志行。
出现了说明第 2 步的 `logging` 没生效。

另开一个 PowerShell 窗口做本机验收：

```powershell
# 健康检查
Invoke-WebRequest http://127.0.0.1:8790/drop/healthz -UseBasicParsing | Select-Object -ExpandProperty Content
```

**你应该看到**：`{"status":"ok","diskFreeBytes":...,"blobCount":0,"uptimeSeconds":...}`

```powershell
# 管理页能出 HTML（前端已内嵌）
(Invoke-WebRequest http://127.0.0.1:8790/drop/admin -UseBasicParsing).Content.Substring(0,200)
```

**你应该看到**：一段 HTML，里面有 `<div id="app">`。

验证完回到第一个窗口按 **Ctrl+C** 停掉。

---

## 6. 装成计划任务（开机自启 + 崩溃自动重启）

```powershell
cd C:\AnyDrop
powershell -ExecutionPolicy Bypass -File .\install-service.ps1
```

**为什么不能"双击 exe 就算了"**：远程桌面会话注销时，该会话里的进程会被结束，服务就没了；
服务器重启后也不会自己起来。计划任务同时解决这两件事（它注册在 SYSTEM 账户下，`At startup` 触发）。

脚本会做这几件事并自检：

1. 检查同目录必须有 `AnyDrop.Server.exe`、`service-run.cmd`、`anydrop.json`、`e_sqlite3.dll`
   四个文件（少任何一个都**直接报错退出**，不会给你留一个装了一半的任务）
2. 若 `AnyDrop` 任务已存在，**先停掉正在运行的旧实例**并等 8790 端口真的空出来
   （否则 `Register -Force` 不动老进程、`IgnoreNew` 又让新的 Start 被静默忽略，
   自检看到的其实是旧进程 —— "改了配置却好像没生效"就是这么来的）
3. 注册任务 `AnyDrop`：SYSTEM / 开机启动 / 崩溃 1 分钟后重启（最多 5 次）/ **无运行时长上限**
   （默认的 3 天上限会把正在传输的下载直接杀掉，所以显式关掉）
4. 启动它
5. 检查 8790 的**监听地址**
6. 请求一次 `healthz`

> 第 3 步那条"崩溃自动重启"能成立，靠的是 `service-run.cmd` 把服务进程的**退出码透传给计划任务**
> —— 计划任务只在退出码非 0 时才判定失败并重启。早先的写法以裸 `endlocal` 收尾，退出码会被抹成 0，
> 于是"崩了也不重启"、任务状态却显示成功。现在末行是 `endlocal & exit /b %EXITCODE%`。
>
> 自检失败时脚本会把**本次新建**的任务回滚掉（本来就在的任务不动），不给你留半配置状态。

**你应该看到**：

```
已注册计划任务：AnyDrop（SYSTEM / 开机启动 / 崩溃 1 分钟后重启，最多 5 次 / 无运行时长上限）
已请求启动，等 5 秒做自检……
监听地址：127.0.0.1
健康检查：HTTP 200  {"status":"ok",...}
```

⚠ **「监听地址」必须只有 `127.0.0.1`**（允许出现 `::1`）。出现 `0.0.0.0` 就是 `server.urls` 配错了，
服务直接暴露在公网 —— 先停服务、改回 `http://127.0.0.1:8790` 再继续。

日常命令：

```powershell
Stop-ScheduledTask  -TaskName AnyDrop    # 停
Start-ScheduledTask -TaskName AnyDrop    # 起
Get-ScheduledTask   -TaskName AnyDrop | Select-Object TaskName,State
Get-Content C:\AnyDrop\logs\server.log -Tail 30 -Wait   # 跟着看日志
```

> 手工测试服务路径时**必须用全路径**：`cmd /c C:\AnyDrop\service-run.cmd`。
> 如果这台机器设了 `NoDefaultCurrentDirectoryInExePath=1`，`cmd /c service-run.cmd` 会报
> "不是内部或外部命令" —— 计划任务用的是全路径，不受影响。
>
> ⚠ 但**别在任务正在跑的时候手工执行它**：`service-run.cmd` 自己不检查单实例，第二个实例会和
> 计划任务起的那个抢 8790 端口、抢同一个 `logs\server.log`。要手工试就先 `Stop-ScheduledTask`，
> 或者干脆在装任务之前试；只是想知道"起没起来"，用 `Start-ScheduledTask` + 看日志。

---

## 7. 配置 nginx

把本包里的 `nginx-anydrop.conf.sample` 中的 `location` 片段贴进 `fshake.com` 的 **443 server{}** 里。

必须保留的三项（否则会出现「小文件能传、大文件 413 或卡死」）：

| 指令 | 值 | 作用 |
| --- | --- | --- |
| `client_max_body_size` | `256m` | 必须 ≥ 服务端 `maxUploadBytes`（268435456）。**相等就行**，小于它 nginx 先回 413 |
| `proxy_request_buffering off` | — | 256 MiB 上传不要先缓冲到 nginx 的磁盘 |
| `proxy_read_timeout` / `proxy_send_timeout` | `3600s` | 上传/下载可能跑很久 |

```powershell
cd <你的 nginx 目录>
.\nginx.exe -t
.\nginx.exe -s reload
```

**你应该看到**：`syntax is ok` + `test is successful`。**只有看到这两行才 reload。**

> 想把 nginx 访问日志里的下载 id 也打码（第二道防线）：按 sample 顶部注释操作 ——
> **先在 `http{}` 里加 `map` 与 `log_format`，`nginx -t` 通过后**，再去 `location` 里取消
> `access_log ... anydrop;` 那行的注释。顺序反了 nginx 会因为 `unknown log format "anydrop"` 拒绝启动。
> 应用侧的 `logging` 过滤关掉的只是逐请求那条 Information 日志（第 2 步说过：Warning/Error 仍可能
> 带 id），所以别把"应用侧"当成泄漏面已经关掉了。这一条挡的是另一个面 —— nginx 自己的
> `access.log`，别让它变成一份链接清单。两边都要做 —— 不过样例里那行 `access_log ... anydrop;`
> 默认是**注释掉**的：不打开的话，`access.log` 会以明文记录下载 id，而应用侧的过滤器管不到它。

> ⚠ **不要把同一个 `location /drop/` 也贴进 80 端口的 `server{}`。** 会话 cookie 的 `Secure`
> 标志是**按请求**判定的：只有确实走 HTTPS 时（也就是 nginx 传了 `X-Forwarded-Proto: https`）才打上。
> 如果 `http://fshake.com/drop/admin` 也能到达服务，登录会发下一个**不带 `Secure`** 的会话 cookie，
> 之后浏览器连 http 请求都会带上它。80 端口要么只做 301 跳转到 https，要么干脆不给这个域名配。

---

## 8. 防火墙与暴露面自查

```powershell
# 1) 确认 8790 只在回环上监听
Get-NetTCPConnection -LocalPort 8790 -State Listen | Select-Object LocalAddress,LocalPort

# 2) 确认没有把 8790 放给公网的入站规则（没有输出才是对的）
Get-NetFirewallPortFilter | Where-Object LocalPort -eq 8790 | Get-NetFirewallRule |
    Select-Object DisplayName,Enabled,Direction,Action
```

**你应该看到**：第一步只有 `127.0.0.1`（或 `::1`）；第二步**没有任何输出**。

如果第二步列出了 `Enabled: True / Direction: Inbound / Action: Allow` 的规则，删掉它：

```powershell
Remove-NetFirewallRule -DisplayName '<上一步显示的 DisplayName>'
```

公网只应放行 **80/443**（80 建议只用来跳 443）：

```powershell
Get-NetFirewallPortFilter | Where-Object { $_.LocalPort -eq 443 -or $_.LocalPort -eq 80 } |
    Get-NetFirewallRule | Select-Object DisplayName,Enabled,Direction,Action
```

---

## 9. 公网端到端验收

### 9.1 健康检查（从你自己的电脑或手机浏览器）

```powershell
Invoke-WebRequest https://fshake.com/drop/healthz -UseBasicParsing | Select-Object -ExpandProperty Content
```

**你应该看到**：和本机一样的 JSON（这一步同时证明了证书、nginx、反代、服务四层都通）。

### 9.2 管理页

浏览器打开 `https://fshake.com/drop/admin`，用第 3 步的密码登录。

进「上传密钥」新建一把（名字随便，如 `ai-upload`），**把它显示的 key 复制下来 —— 只显示这一次**。

### 9.3 上传一把，拿到直链

```powershell
$key  = 'ad_你复制的那串'
$file = 'C:\Windows\win.ini'          # 换成一个真实的小文件路径

$bytes = [IO.File]::ReadAllBytes($file)
$r = Invoke-WebRequest -Uri 'https://fshake.com/drop/v1/blobs' -Method Post -Body $bytes `
        -ContentType 'application/octet-stream' `
        -Headers @{ Authorization = "Bearer $key"; 'X-Filename' = (Split-Path $file -Leaf) } `
        -UseBasicParsing -TimeoutSec 60
$r.Content
```

**你应该看到**：`{"id":"...","url":"https://fshake.com/drop/v1/blobs/<id>","sha256":"...","size":...,"expiresAt":"..."}`

⚠ `url` 必须以 `https://fshake.com/drop/v1/blobs/` 开头。不是的话说明第 2 步的 `publicBaseUrl` 不对。

```powershell
# 下载：不需要任何密钥，id 就是凭证
$j = $r.Content | ConvertFrom-Json
Invoke-WebRequest -Uri $j.url -OutFile "$env:TEMP\anydrop-out.bin" -UseBasicParsing -TimeoutSec 60
(Get-FileHash "$env:TEMP\anydrop-out.bin" -Algorithm SHA256).Hash.ToLower()   # 应等于 $j.sha256
```

> 大文件建议直接用 `curl.exe`（Server 2019+ 自带；注意 PowerShell 里的 `curl` 是
> `Invoke-WebRequest` 的别名，必须写 `curl.exe`）：
> ```powershell
> curl.exe -fSs -X POST "https://fshake.com/drop/v1/blobs" -H "Authorization: Bearer $key" -H "X-Filename: big.bin" --data-binary "@C:\path\big.bin"
> ```

### 9.4 关键安全断言：逐请求日志必须被压掉

```powershell
Select-String -Path C:\AnyDrop\logs\server.log -Pattern 'Request starting'   # 期望：无输出
```

**这条必须没有任何输出。** 有输出就意味着日志正在积累可用的下载链接 —— 去第 2 步确认
`logging` 配置，并确认服务是经 `service-run.cmd` 启动的（`service-run.cmd` 里的环境变量只保证
"被换掉的 `anydrop.json` 不会把这一条重新打开"，**不是**"有它就安全"）。

**但别顺手改成 `-Pattern <刚才那个 id>` 去当"零泄漏"证明。** 它现在多半也是空的，可那是侥幸：
只要发生过一次 Warning/Error（删密文失败、密文与元数据不一致、一个未处理异常），id 就会出现在
日志里 —— 过滤器管不到那些级别（第 2 步列了具体位置）。这条断言能证明的只有"逐请求日志已经关掉"。

配套动作因此是：把 `logs\server.log` 当含凭证的文件管，排障要外发时只贴相关几行。

### 9.5 手机

用手机浏览器（**不要用微信内置浏览器**，它的下载行为会让人误判）打开那条直链，应该直接开始下载。
管理页也顺手看一眼版式。

---

## 10. 备份与升级

**要备份的只有两样**（`data\` 里的密文 + `data\master.key`）：

```powershell
Stop-ScheduledTask -TaskName AnyDrop
Compress-Archive -Path C:\AnyDrop\data -DestinationPath "D:\backup\anydrop-$(Get-Date -f yyyyMMdd).zip"
Start-ScheduledTask -TaskName AnyDrop
```

⚠ **`master.key` 必须离线单独存一份**。它丢了，`blobs\` 里的密文就永久无法解密 ——
这不是"麻烦"，是文件真的没了。

**升级**：停任务 → 覆盖 `AnyDrop.Server.exe`（与 `e_sqlite3.dll`）→ 起任务。

```powershell
Stop-ScheduledTask -TaskName AnyDrop
Copy-Item <新包>\AnyDrop.Server.exe C:\AnyDrop\ -Force
Copy-Item <新包>\e_sqlite3.dll     C:\AnyDrop\ -Force
Start-ScheduledTask -TaskName AnyDrop
Get-Content C:\AnyDrop\logs\server.log -Tail 10
```

数据表结构由程序启动时建立，**当前结构版本 v2**。遇到版本不匹配的旧库，程序会拒绝启动并打印
可操作的提示（不自动迁移）—— 按提示处理，不要盲目删数据。

---

## 11. 排障表

| 现象 | 原因与处理 |
| --- | --- |
| 管理页显示「管理界面未构建」 | 启动日志 `内嵌前端=False`：这个包没带前端产物，重新打包发布 |
| 从 https 换到 http 访问后变成未登录 | 这是**对的行为**：会话 cookie 带 `Secure`，浏览器不会把它发到 http。统一用 https 即可 |
| 用 http 访问管理页居然也能登录 | 说明 `http://` 也能到达服务（80 端口也代理了 `/drop`）：这时发的 cookie 不带 `Secure`，见第 7 步的警告 |
| 经 HTTPS 访问，cookie 却没有 `Secure` 标志 | nginx 少写了 `proxy_set_header X-Forwarded-Proto $scheme`（Secure 是按请求判定的，见第 7 步）；同时确认没把 `/drop` 也代理到 80 端口 |
| 日志里出现 `Request starting ... /v1/blobs/<id>` | `logging` 配置被改回 `Information` 了；`service-run.cmd` 里的环境变量只保证这一条不会被重新打开（它挡不住 Warning/Error，见第 2 步），别删 |
| 上传返回 **413** | nginx `client_max_body_size` 小于服务端 `maxUploadBytes` |
| 大文件上传卡住/中断 | nginx 没关 `proxy_request_buffering`，或 `proxy_read_timeout` 太短 |
| 上传报磁盘相关错误 | `storage.minFreeBytes`（默认 2 GiB）水位线被触发，或 `maxUsedPercent`（默认 90%）到了 |
| **502 Bad Gateway** | 服务没在跑：`Get-ScheduledTask -TaskName AnyDrop`、`Get-Content C:\AnyDrop\logs\server.log -Tail 30` |
| 任务状态是"正在运行"但端口没监听 | 看 `logs\server.log`：常见是漏拷 `e_sqlite3.dll`、`data` 目录权限不足、或表结构版本不匹配 |
| 日志末尾 `exited with code 9009` | 找不到 `AnyDrop.Server.exe`：解包不完整、`-InstallDir` 指错目录，或 exe 被搬走了。cmd 自己那句"不是内部或外部命令"也在这行**上面**（stderr 被 `service-run.cmd` 重定向进了日志），先往上翻两行看它指的是哪个路径 |
| 任务"上次运行结果"不是 `0x0` | 服务非正常退出，退出码就是 `logs\server.log` 末尾那行 `exited with code N`。崩了之后计划任务会在 1 分钟后自动重启，最多 5 次；连续失败就看日志最后一屏 |
| 日志里出现 `blobs/xx/<id>` 或 `{BlobId}` 字样 | 这是 Warning/Error 级日志，过滤器压不掉（第 2 步有说明）。属已知残留风险：日志文件按"含凭证"对待，外发只贴相关几行 |
| 计划任务启动失败（事件查看器） | 任务历史：事件查看器 → 应用程序和服务日志 → Microsoft → Windows → TaskScheduler → Operational |
| 下载 404 但文件刚传过 | 已过期被 GC 回收，或 id 被截断/改写（id 本身就是凭证，必须完整） |
| 日志疯涨 | 检查 `logging` 是否被人改回 `Information`；`service-run.cmd` 里的环境变量只防这一条，别删 |
| 想换域名或换前缀 | 换前缀（`/drop` → 别的）必须用 `ANYDROP_WEB_BASE=/新前缀/` **重新构建前端**，并同步改 `pathBase` 与 `publicBaseUrl` |

---

## 交给 AI 用

`AGENT_UPLOAD.md`（同目录）是给 AI/脚本看的接口说明。给 AI 的东西只有两样：

1. 上传地址：`https://fshake.com/drop`
2. 上传密钥：管理页里新建的那串 `ad_...`

它就能上传并把直链给你。**注意：上传密钥只能上传，读不到任何文件** ——
所以它可以安全地放进 AI 的环境变量里（泄漏的后果只是别人能往你的服务里传东西，受配额与有效期限制）。
