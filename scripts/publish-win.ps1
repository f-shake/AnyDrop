# 发布服务端（默认 AOT 单文件）。注意：AOT 不能跨操作系统交叉编译，
# 要发布 linux-x64 请在 Linux 机器上跑 scripts/publish-linux.sh。
#
# Native AOT 需要本机具备原生链接器：
#   Windows：Visual Studio 的「使用 C++ 的桌面开发」工作负载（含 MSVC 链接器）
#   Linux  ：clang + zlib1g-dev
# 没装工具链时用 -NoAot 退回自包含单文件（体积更大、启动稍慢，但同样只依赖一个 exe）。
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$Output = 'publish',
    [switch]$NoAot
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$extra = @()
if ($NoAot) { $extra = @('-p:PublishAot=false', '-p:PublishSingleFile=true', '-p:SelfContained=true') }
else { $extra = @('-p:PublishSingleFile=true') }

Write-Host "==> dotnet publish -c Release -r $Runtime $(if ($NoAot) { '(非 AOT)' } else { '(AOT)' })" -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src/AnyDrop.Server/AnyDrop.Server.csproj') `
    -c Release -r $Runtime -o (Join-Path $root $Output) @extra --nologo
if ($LASTEXITCODE -ne 0) {
    if (-not $NoAot) {
        Write-Host "AOT 发布失败。最常见原因：缺少原生链接器（Windows 需装 VS 的「使用 C++ 的桌面开发」工作负载）。" -ForegroundColor Yellow
        Write-Host "可以先改用：pwsh ./scripts/publish-win.ps1 -NoAot" -ForegroundColor Yellow
    }
    throw "dotnet publish 失败（exit $LASTEXITCODE）"
}

$exeName = if ($Runtime.StartsWith('win')) { 'AnyDrop.Server.exe' } else { 'AnyDrop.Server' }
$exe = Join-Path $root "$Output/$exeName"
if (-not (Test-Path $exe)) { throw "没有找到发布产物：$exe" }

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "完成：$exe（$size MB）" -ForegroundColor Green
Write-Host "发布目录里还需要一起带上：anydrop.json 与 e_sqlite3.dll（SQLite 原生库）。" -ForegroundColor Yellow
