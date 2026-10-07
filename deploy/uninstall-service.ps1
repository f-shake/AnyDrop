# uninstall-service.ps1 —— 停止并删除 AnyDrop 计划任务（不删数据、不删日志）
#
# 在 VPS 上以【管理员身份】运行 Windows PowerShell：
#   powershell -ExecutionPolicy Bypass -File .\uninstall-service.ps1
[CmdletBinding()]
param(
    [string]$TaskName = 'AnyDrop'
)

$ErrorActionPreference = 'Stop'

$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "没有找到计划任务 $TaskName，无需卸载。" -ForegroundColor Yellow
    return
}

Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# 计划任务的 Stop 会结束进程树，这里兜底确认一次（例如上次是被手工启动的）
$left = @(Get-Process -Name 'AnyDrop.Server' -ErrorAction SilentlyContinue)
if ($left.Count -gt 0) {
    Write-Host "还有 $($left.Count) 个 AnyDrop.Server 进程在跑，强制结束：$($left.Id -join ', ')" -ForegroundColor Yellow
    $left | Stop-Process -Force
    Start-Sleep -Seconds 1
}

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
Write-Host "已停止并删除计划任务 $TaskName。" -ForegroundColor Green
Write-Host '数据目录、主密钥与日志都保留着；要彻底清理请手动删除安装目录。' -ForegroundColor Cyan
