# pack-win.ps1 —— 打一个可以直接拷到 Windows VPS 的发布包
#
# 产物：
#   release/AnyDrop/                     可直接解压到 C:\ 的目录（含 exe、配置、运维脚本、路书）
#   release/AnyDrop-win-x64-<提交>.zip   发给 VPS 的那个文件（-NoZip 可跳过）
#
# 为什么默认非 AOT：Native AOT 不能跨操作系统交叉编译，而且没有原生链接器
# （Windows 的 VS「使用 C++ 的桌面开发」/ Linux 的 clang）时根本编译不出来。
# 非 AOT 自包含单文件功能完全一致，代价是体积约 100 MB、启动稍慢（1~2 秒）。
# 想要 AOT：先 pwsh ./scripts/publish-win.ps1（不带 -NoAot）产出发布目录，再用 -SkipBuild 打包。
#
# 用法：
#   pwsh ./scripts/pack-win.ps1                # 构建前端 + 发布 + 组装 + 压缩
#   pwsh ./scripts/pack-win.ps1 -NoZip         # 不压缩（调试用，快）
#   pwsh ./scripts/pack-win.ps1 -SkipBuild     # 复用已有 release/AnyDrop 里的二进制，只重新组装附加文件
[CmdletBinding()]
param(
    [string]$Output = 'release',
    [switch]$SkipBuild,
    [switch]$SkipInstall,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'

# --- 0. 宿主检查 -----------------------------------------------------------
# 必须在 PowerShell 7（pwsh）下运行。两条原因都不是"风格问题"：
#   1) 本脚本调用的 build-web.ps1 用了 [System.IO.Path]::GetRelativePath —— 那是 .NET Core 2.0+
#      的 API，而 Windows PowerShell 5.1 跑在 .NET Framework 上，没有这个方法（实测抛异常）。
#   2) build-web.ps1 / publish-win.ps1 / build-all.ps1 都是**无 BOM** 的 UTF-8 且含中文注释，
#      5.1 会按 ANSI(cp936) 解码，在**解析阶段**就报一串乱码语法错（实测 build-web.ps1 报 3 个错）。
# 在这里提前拦住：否则用户看到的第一现场是一句看不懂的 ParseException。
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "pack-win.ps1 需要 PowerShell 7（pwsh），当前是 Windows PowerShell $($PSVersionTable.PSVersion)。请改用：pwsh ./scripts/pack-win.ps1"
}

$root  = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root "$Output/AnyDrop"

# --- 1. 构建 ---------------------------------------------------------------
if ($SkipBuild) {
    if (-not (Test-Path (Join-Path $stage 'AnyDrop.Server.exe'))) {
        throw "-SkipBuild 需要 $stage 里已经有 AnyDrop.Server.exe，但没找到。"
    }
    Write-Host "==> 跳过构建，复用 $stage 里的二进制" -ForegroundColor Yellow
}
else {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    # Vite 开发服务器会把 node_modules 里的原生模块（rolldown-binding*.node）映射进自己的进程，
    # 而 npm ci 需要先删掉整个 node_modules —— Windows 上删不掉被映射的 .node 文件，
    # 报出来的是 EPERM unlink。这里提前说人话，别让它伪装成「前端构建未完成」。
    if (-not $SkipInstall -and (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue)) {
        $vite = @(Get-NetTCPConnection -LocalPort 5173 -State Listen -ErrorAction SilentlyContinue)
        if ($vite.Count -gt 0) {
            throw '检测到 Vite 开发服务器还在 5173 上监听：npm ci 会因为 node_modules 里的原生模块被占用而失败（EPERM unlink）。请先停掉 npm run dev，或加 -SkipInstall 跳过 npm ci。'
        }
    }

    Write-Host '==> 构建前端（类型检查 + 单测 + vite build + 内嵌清单）' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'build-web.ps1') -SkipInstall:$SkipInstall
    if ($LASTEXITCODE -ne 0) { throw 'build-web.ps1 失败，不要发布这次产物。' }

    Write-Host '==> 发布服务端（win-x64，非 AOT 自包含单文件）' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'publish-win.ps1') -NoAot -Output "$Output/AnyDrop"
    if ($LASTEXITCODE -ne 0) { throw 'publish-win.ps1 失败。' }

    # 这两个文件对本部署形态没用：pdb 是符号、aspnetcorev2_inprocess.dll 是 IIS 用的模块。
    # 我们是 Kestrel 自托管，不经 IIS，带上只会让人以为要配 IIS。
    foreach ($junk in @('AnyDrop.Server.pdb', 'aspnetcorev2_inprocess.dll')) {
        $path = Join-Path $stage $junk
        if (Test-Path $path) { Remove-Item $path -Force }
    }
}

# --- 2. 组装运维文件 -------------------------------------------------------
$assets = @(
    'service-run.cmd',
    'install-service.ps1',
    'uninstall-service.ps1',
    'RUNBOOK-WINDOWS.md',
    'nginx-anydrop.conf.sample',
    # 第三方许可声明。管理页的构建产物（Element Plus 等，MIT）被编译进 exe 一起分发，
    # 版权声明必须随二进制走 —— 不带它，这个包在合规上就是不完整的。
    'NOTICE-element-plus.txt'
)
foreach ($name in $assets) {
    $from = Join-Path $root "deploy/$name"
    if (-not (Test-Path $from)) { throw "缺少部署文件：$from" }
    Copy-Item $from $stage -Force
}
Copy-Item (Join-Path $root 'docs/AGENT_UPLOAD.md') $stage -Force

# anydrop.json 由样板生成：域名、pathBase、cookieSecure 都已按公网形态填好
Copy-Item (Join-Path $root 'anydrop.json.sample') (Join-Path $stage 'anydrop.json') -Force

# --- 3. 版本信息 -----------------------------------------------------------
$exe     = Join-Path $stage 'AnyDrop.Server.exe'
$commit  = (& git -C $root rev-parse HEAD)
$short   = (& git -C $root rev-parse --short HEAD)
$branch  = (& git -C $root rev-parse --abbrev-ref HEAD)
$dirty   = if (@(& git -C $root status --porcelain).Count -gt 0) { '有' } else { '无' }
$sizeMb  = [math]::Round((Get-Item $exe).Length / 1MB, 1)
$sha256  = (Get-FileHash $exe -Algorithm SHA256).Hash

$info = @(
    'AnyDrop Windows x64 发布包',
    '',
    "打包时间（本机）：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
    "分支：$branch",
    "提交：$commit",
    "工作区未提交改动：$dirty",
    '',
    "AnyDrop.Server.exe：$sizeMb MB",
    "SHA256：$sha256",
    '',
    '构建方式：非 AOT 自包含单文件（PublishSingleFile + SelfContained，PublishAot=false）',
    '运行依赖：Windows Server 2016+ / Windows 10+；不需要预装 .NET 运行时。',
    '必需同目录文件：anydrop.json、e_sqlite3.dll（SQLite 原生库，缺了服务起不来）。',
    '',
    '装着看：RUNBOOK-WINDOWS.md（第 1 步开始照做即可）。'
)
Set-Content -LiteralPath (Join-Path $stage 'BUILD-INFO.txt') -Value ($info -join "`r`n") -Encoding utf8

# --- 3.5 让包里的中文在别的机器上也不乱码 -----------------------------------
# 文件名一律 ASCII：'版本信息.txt' 这样的名字在 zip 里跨编码容易变乱码，Explorer / 7-zip / tar
# 各自表现不一致。文件**内容**里有中文的文本文件统一补 UTF-8 BOM —— Windows 记事本在没有 BOM 时
# 会按 ANSI(GBK) 解读 UTF-8，整篇路书会变成乱码。
#
# 用**扩展名白名单**，不是"含非 ASCII 就补"：补 BOM 是有副作用的，只对"给人读的文本"才划算。
#   .conf / .sample  绝不能补 —— nginx 把开头的 EF BB BF 当成第一个指令的一部分，于是直接拒绝
#                    整份配置。可复核的复现办法：把同一份 .conf 分别存成「UTF-8 无 BOM」与
#                    「UTF-8 带 BOM」，各跑一次 `nginx -t -c <该文件>`：无 BOM → "syntax is ok"；
#                    有 BOM → [emerg] unknown directive "﻿<第一个指令名>"。上面这个结果是用
#                    nginx 1.26.2（与线上反代同版本）实际跑出来的，两种输入各复现过一次。
#   .cmd             绝不能补 —— BOM 会污染第一行，cmd.exe 报错
#   .json            不补 —— 用户要手工编辑，且 JSON 解析器不依赖 BOM
$bomExts = @('.ps1', '.md', '.txt')
$patched = 0
$skipped = 0
foreach ($file in Get-ChildItem $stage -File) {
    if ($file.Extension -notin $bomExts) { continue }
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if ($bytes.Length -lt 3) { continue }
    if ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { continue }
    if (-not ($bytes | Where-Object { $_ -gt 0x7F } | Select-Object -First 1)) { continue }
    try { $text = (New-Object Text.UTF8Encoding $false, $true).GetString($bytes) }
    catch {
        Write-Host "    跳过（不是合法 UTF-8，未改动）：$($file.Name)" -ForegroundColor Yellow
        $skipped++
        continue
    }
    [IO.File]::WriteAllText($file.FullName, $text, (New-Object Text.UTF8Encoding $true))
    Write-Host "    补 BOM：$($file.Name)" -ForegroundColor DarkGray
    $patched++
}
if ($patched -gt 0) { Write-Host "已为 $patched 个含中文的文本文件补上 UTF-8 BOM" -ForegroundColor Green }
if ($skipped -gt 0) { Write-Host "注意：有 $skipped 个文件不是合法 UTF-8，**没有**补 BOM，到目标机器上可能乱码（见上面的文件名）" -ForegroundColor Yellow }

# --- 4. 压缩 ---------------------------------------------------------------
Write-Host ''
Write-Host "==> 发布目录就绪：$stage" -ForegroundColor Green
Get-ChildItem $stage | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize

if (-not $NoZip) {
    $zip     = Join-Path $root "$Output/AnyDrop-win-x64-$short.zip"
    $partial = "$zip.partial"
    Write-Host "==> 压缩到 $zip（100 MB 的单文件，可能要一两分钟）" -ForegroundColor Cyan

    # 先写 .partial 再改名，而且**先不动**已有的同名 zip。Compress-Archive 中途失败（文件被占、
    # 磁盘满）会留下一个没有中央目录的残包，而 `Expand-Archive -Path ...\AnyDrop-win-x64-*.zip`
    # 这种通配符照样会把它匹配上 —— 那是最坏的一种坏包：解压报错，或者更糟，解出一个不完整的
    # 安装目录还看着像成功了。改名是原子的，旧包要么被完整替换、要么原样留着。
    if (Test-Path $partial) { Remove-Item $partial -Force }
    try {
        Compress-Archive -Path $stage -DestinationPath $partial -CompressionLevel Optimal
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Move-Item -LiteralPath $partial -Destination $zip -Force
    }
    catch {
        if (Test-Path $partial) { Remove-Item $partial -Force -ErrorAction SilentlyContinue }
        throw "压缩失败，中间文件已清理，没有生成 $zip：$($_.Exception.Message)"
    }
    $zipMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "完成：$zip（$zipMb MB）" -ForegroundColor Green
    Write-Host '解压到 C:\ 会得到 C:\AnyDrop\（zip 里带顶层目录）。' -ForegroundColor Cyan
}
