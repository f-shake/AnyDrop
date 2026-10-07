# install-service.ps1 —— 把 AnyDrop 注册成 Windows 计划任务：开机自启 + 崩溃自动重启
#
# 为什么不用 sc.exe create：AnyDrop.Server.exe 是普通控制台程序，不实现服务控制管理器
# (SCM) 的协议，sc.exe 装上去启动会报「错误 1053 服务没有及时响应」。
#
# 为什么不能「在远程桌面里双击 exe」：RDP 会话注销时该会话的进程会被结束，
# 服务就没了；而且服务器重启后也不会自己起来。计划任务解决这两个问题。
#
# 用法（VPS 上，以管理员身份运行的 Windows PowerShell）：
#   powershell -ExecutionPolicy Bypass -File .\install-service.ps1
# 自定义安装目录 / 任务名 / 端口：
#   powershell -ExecutionPolicy Bypass -File .\install-service.ps1 -InstallDir D:\AnyDrop -Port 8790
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\AnyDrop',
    [string]$TaskName   = 'AnyDrop',
    [int]$Port          = 8790
)

$ErrorActionPreference = 'Stop'

# --- 权限 ---------------------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '需要管理员权限：请用「以管理员身份运行」的 PowerShell 重新执行本脚本。'
}

# --- 文件是否齐 ---------------------------------------------------------
# e_sqlite3.dll 必须一起查：它是 Microsoft.Data.Sqlite 的原生依赖，缺了 exe 会在启动时
# 直接抛异常退出；而 service-run.cmd 是"exe 退出就正常结束"的包装，计划任务会把这记成
# 一次成功运行 —— 于是"少拷了一个文件"会被后面的端口自检伪装成"配置/端口问题"。
$exe     = Join-Path $InstallDir 'AnyDrop.Server.exe'
$wrapper = Join-Path $InstallDir 'service-run.cmd'
$config  = Join-Path $InstallDir 'anydrop.json'
$sqlite  = Join-Path $InstallDir 'e_sqlite3.dll'
foreach ($path in @($exe, $wrapper, $config, $sqlite)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "找不到 $path —— 请确认 -InstallDir 指向解压后的目录（当前：$InstallDir）。"
    }
}
New-Item -ItemType Directory -Force -Path (Join-Path $InstallDir 'logs') | Out-Null

# --- 幂等：先收掉可能正在跑的旧实例 --------------------------------------
# 重复执行本脚本时，Register -Force 只更新任务定义，**不会动**正在运行的那个进程，而
# MultipleInstances=IgnoreNew 会让新的 Start 请求被静默忽略。这时若直接去看端口，看到的是
# 旧进程 —— "改了 anydrop.json 却好像没生效"会被自检判成成功。所以先停，并等端口真的空出来。
# 前提：-Port 必须与服务端 anydrop.json 里 server.urls 的端口一致；不一致时这一步等的是一个
# 没人监听的端口，等于没等（自检也查不出真相）。
$existed = $null -ne (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)
if ($existed) {
    Write-Host "任务 $TaskName 已存在（本次是覆盖更新），先停掉正在运行的实例……" -ForegroundColor Yellow
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 30; $i++) {
        if (@(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue).Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    $still = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    if ($still.Count -gt 0) {
        $ownerPids  = ($still | Select-Object -ExpandProperty OwningProcess -Unique) -join ', '
        $ownerNames = ($still | ForEach-Object { (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName } |
                        Sort-Object -Unique) -join ', '
        # 上面是"先等到端口空、再复查一次"，两次之间理论上有窗口：旧实例可能刚好被
        # RestartOnFailure 又拉起来。所以这里按"占用者是谁"给不同话术，别把自家进程说成外人。
        if ($ownerNames -like '*AnyDrop.Server*') {
            throw "$Port 端口又被 AnyDrop.Server 占上了（PID: $ownerPids）—— 多半是刚才那个实例被计划任务的失败重启又拉了起来。直接再运行一次本脚本即可。"
        }
        throw "停掉任务后 $Port 端口仍被占用（PID: $ownerPids，进程: $ownerNames）。这不是本任务起的进程，脚本不会替你杀掉它 —— 确认后重试。"
    }
}
else {
    # 任务还不存在，但端口上已经有人在听 —— 多半是你先前手工启动的实例（或别的程序）。
    # 不拦住的话，后面 Register + Start 之后的自检会看到那个进程，把"新配置已生效"判成成功。
    $busy = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    if ($busy.Count -gt 0) {
        $ownerPids  = ($busy | Select-Object -ExpandProperty OwningProcess -Unique) -join ', '
        $ownerNames = ($busy | ForEach-Object { (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName } |
                        Sort-Object -Unique) -join ', '
        throw "$Port 端口上已经有监听，但 $TaskName 任务还不存在（PID: $ownerPids，进程: $ownerNames）。先停掉那个实例再运行本脚本，否则自检会把旧进程误判成新配置启动成功。"
    }
}
$script:registeredNow = -not $existed

# 自检失败时统一收尾：本次新建的任务就回滚，之前就存在的不动它（那可能是别人正在用的服务）。
function Fail-SelfCheck([string]$reason) {
    Write-Host "自检失败：$reason" -ForegroundColor Red
    Write-Host "看日志：$InstallDir\logs\server.log" -ForegroundColor Yellow
    Write-Host "看任务历史：事件查看器 → 应用程序和服务日志 → Microsoft → Windows → TaskScheduler → Operational" -ForegroundColor Yellow
    if ($script:registeredNow) {
        Write-Host "本次新建的任务已回滚（Unregister-ScheduledTask $TaskName）—— 修好问题后重新运行本脚本。" -ForegroundColor Yellow
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    } else {
        Write-Host "任务 $TaskName 在本次之前就存在，**没有**替你删掉它；修好问题后重新运行本脚本即可。" -ForegroundColor Yellow
    }
    exit 1
}

# --- 注册任务 -----------------------------------------------------------
# ExecutionTimeLimit 0 = 不限制运行时长。默认是 3 天，对常驻服务是致命的：
# 到了时限 Windows 会把进程杀掉，而任何一次正常的下载都可能正在传输中。
# MultipleInstances IgnoreNew = 绝不出现第二个实例（两个进程共用一个 SQLite 不是设计目标）。
$action        = New-ScheduledTaskAction -Execute $wrapper -WorkingDirectory $InstallDir
$trigger       = New-ScheduledTaskTrigger -AtStartup
$taskPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings      = New-ScheduledTaskSettingsSet `
                    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                    -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) `
                    -ExecutionTimeLimit ([TimeSpan]::Zero) `
                    -StartWhenAvailable -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $taskPrincipal -Settings $settings -Force | Out-Null
Write-Host "已注册计划任务：$TaskName（SYSTEM / 开机启动 / 崩溃 1 分钟后重启，最多 5 次 / 无运行时长上限）" -ForegroundColor Green

# --- 启动并自检 ---------------------------------------------------------
Start-ScheduledTask -TaskName $TaskName
Write-Host '已请求启动，等 5 秒做自检……' -ForegroundColor Cyan
Start-Sleep -Seconds 5

$listening = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
if ($listening.Count -eq 0) {
    Fail-SelfCheck "$Port 端口上没有监听（常见原因：漏拷 e_sqlite3.dll、data 目录权限不足、表结构版本不匹配、端口已被别的程序占用）。"
}

$addresses = ($listening | Select-Object -ExpandProperty LocalAddress -Unique) -join ', '
Write-Host "监听地址：$addresses" -ForegroundColor Yellow
if ($addresses -notmatch '^(127\.0\.0\.1|::1)$') {
    Write-Host '警告：监听地址不只是回环地址！请确认 anydrop.json 里 server.urls 是 http://127.0.0.1:8790，' -ForegroundColor Red
    Write-Host '      并用防火墙封掉该端口，公网只应暴露 nginx 的 443。' -ForegroundColor Red
}

try {
    $health = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/drop/healthz" -UseBasicParsing -TimeoutSec 10
    Write-Host "健康检查：HTTP $($health.StatusCode)  $($health.Content)" -ForegroundColor Green
} catch {
    Fail-SelfCheck "健康检查失败：$($_.Exception.Message)（若你改过 server.pathBase，请把本脚本里的自检 URL 一并改成对应前缀）"
}

Write-Host ''
Write-Host '下一步：' -ForegroundColor Cyan
Write-Host '  1) 确认上面的「监听地址」只有 127.0.0.1（公网只应暴露 nginx 的 443）。'
Write-Host '  2) 配 nginx 反向代理，然后 nginx -t && nginx -s reload。'
Write-Host '  3) 打开 https://<域名>/drop/admin 登录验收。'
Write-Host ''
Write-Host "停止服务：Stop-ScheduledTask -TaskName $TaskName"
Write-Host "启动服务：Start-ScheduledTask -TaskName $TaskName"
Write-Host '卸载：.\uninstall-service.ps1'
