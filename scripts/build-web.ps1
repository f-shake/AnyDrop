# 构建前端，并把 dist 登记成服务端的内嵌资源（生成 wwwroot.g.props）。
# 顺序与 docs/PLAN.md 一致：npm ci → 类型检查 → 单测 → vite build；
# 任何一步失败（含复制与清单生成）都视为"构建未完成"：此时 wwwroot 与 wwwroot.g.props
# 既可能是上一次成功构建的内容，也可能已被清空——不要在这种状态下发布。
# 用法：pwsh ./scripts/build-web.ps1 [-SkipInstall] [-SkipTests]
[CmdletBinding()]
param(
    [switch]$SkipInstall,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$web = Join-Path $root 'web'
$serverDir = Join-Path $root 'src/AnyDrop.Server'
$wwwroot = Join-Path $serverDir 'wwwroot'
$propsPath = Join-Path $serverDir 'wwwroot.g.props'

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name 失败（exit $LASTEXITCODE）" }
}

try {
    Push-Location $web
    try {
        if (-not $SkipInstall) { Invoke-Step 'npm ci' { npm ci } }
        Invoke-Step 'vue-tsc 类型检查' { npm run typecheck }
        if (-not $SkipTests) { Invoke-Step 'vitest' { npm run test } }
        Invoke-Step 'vite build' { npm run build }
    }
    finally {
        Pop-Location
    }

    # 复制与清单生成也在同一个 try 内：这一步失败同样属于"构建未完成"
    if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
    New-Item -ItemType Directory -Path $wwwroot | Out-Null
    Copy-Item (Join-Path $web 'dist/*') $wwwroot -Recurse -Force

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('<?xml version="1.0" encoding="utf-8"?>')
    $lines.Add('<!-- 由 scripts/build-web.ps1 生成，请勿手工编辑。 -->')
    $lines.Add('<Project>')
    $lines.Add('  <ItemGroup>')

    $files = Get-ChildItem $wwwroot -Recurse -File | Sort-Object FullName
    foreach ($file in $files) {
        $relative = [System.IO.Path]::GetRelativePath($wwwroot, $file.FullName).Replace('/', '\')
        $logical = 'web/' + $relative.Replace('\', '/')
        $lines.Add("    <EmbeddedResource Include=`"wwwroot\$relative`" LogicalName=`"$logical`" />")
    }

    $lines.Add('  </ItemGroup>')
    $lines.Add('</Project>')
    Set-Content -LiteralPath $propsPath -Value ($lines -join "`n") -Encoding utf8NoBOM

    Write-Host "已登记 $($files.Count) 个前端文件到 $propsPath" -ForegroundColor Green
}
catch {
    Write-Host "前端构建未完成：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host "src/AnyDrop.Server/wwwroot 与 wwwroot.g.props 现在既可能是上一次成功构建的内容，也可能已被清空——此状态下不要发布，先重跑本脚本。" -ForegroundColor Yellow
    throw
}
