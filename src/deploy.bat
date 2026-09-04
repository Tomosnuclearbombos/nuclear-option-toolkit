@echo off
REM ===========================================================================
REM  Automated daily plugin deploy (registered as a Windows Scheduled Task at
REM  05:00). Goes THROUGH run.bat so the SFTP env (NO_SFTP_*) is set for any
REM  pending-plugin upload; run.bat forwards --deploy-plugin to the bot's
REM  one-shot, which stages pending_plugin.dll (if new) then stop->start the
REM  game server via the Pterodactyl API and verifies it via the relay.
REM  GUARDRAIL: it never knowingly leaves the server offline.
REM  Manual run:   .\deploy.bat            (real deploy + restart)
REM  Safe preview: .\run.bat --deploy-plugin-dry
REM
REM  2026-06-28: this ALSO restarts the long-running bot + cc_web AFTER the plugin deploy, so their
REM  STAGED code changes go live at 05:00 too (the --deploy-plugin step only restarts the GAME server).
REM  Restarting these LOCAL processes does NOT drop game players. If the restart hiccups, the OPS
REM  monitoring loop is the safety net (it relaunches a dead bot/cc_web within ~25 min).
REM
REM  Kills are FOLDER-SCOPED (CommandLine -like "*<this folder>\*") so other servers' bots survive.
REM ===========================================================================
call "%~dp0run.bat" --deploy-plugin
if errorlevel 1 (
  echo [deploy] %date% %time% deploy-plugin FAILED - NOT restarting bot/webcc>>"%~dp0deploy_plugin.log"
  exit /b 1
)

REM --- restart the bot + cc_web to load staged code. The --deploy-plugin one-shot above has already
REM exited (`call` blocks until it returns), so killing python here cannot hit the deploy worker. ---
echo [deploy] %date% %time% restarting bot + cc_web to load staged code...>>"%~dp0deploy_plugin.log"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p=(Split-Path -Parent '%~f0'); if (-not $p.EndsWith([char]92)) { $p=$p+[char]92 }; Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | Where-Object { $_.CommandLine -match 'no_mapvote_bot\.py|cc_web\.py' -and $_.CommandLine -like ('*' + $p + '*') } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; Start-Sleep -Seconds 4; Start-Process -FilePath (Join-Path $p 'run.bat') -WorkingDirectory $p -WindowStyle Minimized; Start-Sleep -Seconds 2; Start-Process -FilePath (Join-Path $p 'webcc.bat') -WorkingDirectory $p -WindowStyle Minimized"
