<#
.SYNOPSIS
    按 id 从 AnyDrop 取一个文件（NAS / 其他终端上的示例取件脚本）。

.DESCRIPTION
    用 nas-pull 类型的读取密钥取件；下载后校验明文 SHA-256；可选取完即删。
    轮询收件箱不在本脚本范围内（按 id 取是当前约定的用法）。

.EXAMPLE
    pwsh ./fetch-by-id.ps1 -Id K7F3... -Key $env:ANYDROP_NAS_KEY -OutDir D:\inbox -Delete
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Id,
    [Parameter(Mandatory = $true)][string]$Key,
    [string]$BaseUrl = 'https://fshake.com/drop',
    [string]$OutDir = '.',
    [string]$Name,
    [switch]$Delete
)

$ErrorActionPreference = 'Stop'
$url = "$($BaseUrl.TrimEnd('/'))/v1/blobs/$Id"
$headers = @{ Authorization = "Bearer $Key" }

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
# 注意：未绑定的 [string] 参数是空串而不是 $null，所以不能用 `??` 回退
$target = Join-Path $OutDir $(if ([string]::IsNullOrWhiteSpace($Name)) { $Id } else { $Name })

# 先 HEAD 取元数据：X-File-Sha256 是明文哈希，取不到就说明服务端不对，
# 这时必须失败退出，绝不能"跳过校验但报告成功"。
$expected = ''
try {
    $head = Invoke-WebRequest -Uri $url -Method Head -Headers $headers
    $expected = ([string]($head.Headers['X-File-Sha256'] | Select-Object -First 1)).Trim().ToLowerInvariant()
}
catch {
    Write-Error "取元数据失败：$($_.Exception.Message)"
    exit 1
}
if ([string]::IsNullOrWhiteSpace($expected)) {
    Write-Error '服务端没有返回 X-File-Sha256，无法校验完整性，已中止（不会保存文件）'
    exit 2
}

Write-Host "下载 $url（expect sha256=$expected）"
try {
    Invoke-WebRequest -Uri $url -Headers $headers -OutFile $target | Out-Null
}
catch {
    Write-Error "下载失败：$($_.Exception.Message)"
    exit 1
}

$actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expected -ne $actual) {
    Remove-Item -LiteralPath $target -Force
    Write-Error "SHA-256 校验失败：期望 $expected，实际 $actual（文件已删除）"
    exit 3
}

Write-Host "已保存 $target（sha256=$actual）" -ForegroundColor Green

if ($Delete) {
    Invoke-RestMethod -Uri $url -Method Delete -Headers $headers | Out-Null
    Write-Host '服务端副本已删除' -ForegroundColor Green
}
