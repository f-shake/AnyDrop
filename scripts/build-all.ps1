# 一条命令：构建前端 → 发布服务端单文件。
# 用法：pwsh ./scripts/build-all.ps1 [-Runtime win-x64] [-Output publish]
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$Output = 'publish'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

& (Join-Path $PSScriptRoot 'build-web.ps1')
if ($LASTEXITCODE -ne 0) { throw '前端构建失败' }

Write-Host "==> dotnet publish ($Runtime)" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'publish-win.ps1') -Runtime $Runtime -Output $Output
if ($LASTEXITCODE -ne 0) { throw '服务端发布失败' }

$exeName = if ($Runtime.StartsWith('win')) { 'AnyDrop.Server.exe' } else { 'AnyDrop.Server' }
$exe = Join-Path (Join-Path $root $Output) $exeName
Write-Host "完成：$exe" -ForegroundColor Green
Write-Host "记得一起带上 anydrop.json 与 e_sqlite3.dll。" -ForegroundColor Yellow
