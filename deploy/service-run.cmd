@echo off
rem ---------------------------------------------------------------------------
rem AnyDrop service wrapper.
rem Started by the "AnyDrop" scheduled task (SYSTEM account, at startup).
rem Do NOT start this by hand inside an RDP session and then sign out: the
rem process is killed when the session ends. Use install-service.ps1 instead.
rem
rem ASCII only on purpose: cmd.exe reads this file with the OEM codepage, so
rem non-ASCII comments would show up as garbage in a console.
rem ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0"

rem Defence in depth -- but NOT a guarantee that the log stays credential-free.
rem The download URL contains the blob id, and that id IS the download credential, so the
rem per-request log lines that ASP.NET Core emits at Information level would turn this log
rem file into a list of working links. anydrop.json raises these two filters by default; the
rem two variables below re-assert them, so a replaced or hand-edited anydrop.json cannot
rem re-enable that flood without also beating a later configuration source.
rem
rem THE ANYDROP__ PREFIX IS LOAD-BEARING. Do not "simplify" these to bare
rem Logging__LogLevel__... names. Measured against the very binary in this package
rem (anydrop.json rewritten to Information, one request, then look in this log):
rem
rem   env var name                                            observed
rem   Logging__LogLevel__Microsoft.AspNetCore=Warning          STILL LOGGED
rem   ANYDROP__Logging__LogLevel__Microsoft.AspNetCore=Warning  suppressed
rem
rem Why: Program.cs calls AddJsonFile(anydrop.json) AFTER CreateSlimBuilder has already
rem registered the default UNPREFIXED environment provider. Later sources win, so the JSON
rem beats a bare name; the ANYDROP__-prefixed provider is registered last and wins in turn.
rem A bare name is therefore read but silently overridden whenever anydrop.json defines the
rem same category -- which the shipped anydrop.json.sample does.
rem
rem Residual risk, deliberately accepted: lines at Warning and above still carry the id,
rem from these and from other logger categories. Verified against the source:
rem   BlobStore.LogWarning    "...: {Path}"          gives blobs/xx/ID   (this category IS in
rem                           the filter list, but the level set is Warning, so Warning and
rem                           above still get written)
rem   BlobService.LogError    "{BlobId}" / "{Path}"  (category NOT in the filter list)
rem   Middleware.LogError     "... {Method} {Path}"  (raw request path, which contains the id)
rem So treat logs\server.log as credential-bearing: keep it off shared drives, do not paste
rem it into chats, issues or tickets, and let the rotation below bound how long it lives.
rem The client IP is still recorded by nginx, whose access_log is masked separately.
set ANYDROP__Logging__LogLevel__Microsoft.AspNetCore=Warning
set ANYDROP__Logging__LogLevel__AnyDrop.Server.Storage.BlobStore=Warning

if not exist "logs" mkdir "logs"

rem Rotate only when the file grows past 32 MiB, so a crash-loop still keeps the
rem log from the run that crashed.
if exist "logs\server.log" for %%A in ("logs\server.log") do if %%~zA GTR 33554432 move /y "logs\server.log" "logs\server.previous.log" >nul

"%~dp0AnyDrop.Server.exe" >> "logs\server.log" 2>&1
set "EXITCODE=%ERRORLEVEL%"
echo [%DATE% %TIME%] AnyDrop.Server exited with code %EXITCODE% >> "logs\server.log"
rem 9009 is cmd's "command not found". cmd writes its own "not recognized as an internal or
rem external command" text straight to the stderr handle that is in effect for the failing
rem command -- so it lands in server.log only because the redirect sits on that very line
rem (verified with the unmodified script in a folder without the exe: the raw text is in the
rem log). This hint is therefore a convenience, not a substitute: it makes the cause readable
rem from the "exited with code" line alone, which is what you scan a long log for. Note the
rem dependency -- do not move the redirect off that line, and keep the RUNBOOK troubleshooting
rem row in sync with both.
if "%EXITCODE%"=="9009" echo   hint: 9009 = AnyDrop.Server.exe not found (incomplete unpack, or wrong folder) >> "logs\server.log"

rem `endlocal & exit /b %EXITCODE%`, NOT a bare `endlocal`. The task's restart-on-failure only
rem fires when the action returns a NON-ZERO exit code, and a bare endlocal always succeeds:
rem verified -- with the exe missing (real code 9009) cmd.exe still exited 0, so the task
rem reported success and -RestartCount 5 never triggered. %EXITCODE% is expanded before
rem endlocal runs, so the real code survives, while a healthy server still exits 0.
endlocal & exit /b %EXITCODE%
