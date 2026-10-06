@echo off
rem 把本文件与 AnyDrop.Server.exe、anydrop.json 放在同一目录，双击或从命令行运行即可。
rem 停止服务：Ctrl+C（前台运行）。
setlocal
cd /d "%~dp0"
set ANYDROP__SERVER__URLS=http://127.0.0.1:8790
"%~dp0AnyDrop.Server.exe" %*
endlocal
