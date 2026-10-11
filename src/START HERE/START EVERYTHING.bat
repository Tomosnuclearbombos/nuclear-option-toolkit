@echo off
REM One-click launcher: ONE bot window + ONE web command centre window.
REM Idempotent - it first stops ANY old bot, web CC, AND the run_keepalive babysitter
REM for THIS folder only (trailing \), so you can never end up with two bots /
REM cross-kill another server install.
title Nuke Option - launcher
REM PATH self-repair: started by a scheduled task or straight after a reboot, PATH can miss
REM powershell, and then the stop passes below would do nothing.
if not defined SystemRoot set "SystemRoot=C:\Windows"
set "PATH=%SystemRoot%\System32;%SystemRoot%;%SystemRoot%\System32\WindowsPowerShell\v1.0;%PATH%"
echo ============================================
echo   NUKE OPTION - starting everything
echo ============================================
echo Stopping any old copies first (THIS folder only: bot, web CC, keep-alive)...
REM Pass 1: kill the keep-alive babysitter so it can't respawn a bot. Exclude THIS killer (own PID).
powershell -NoProfile -Command "$d=(Resolve-Path '%~dp0..').Path; if (-not $d.EndsWith([char]92)) { $d=$d+[char]92 }; $me=$PID; Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $me -and $_.CommandLine -and ($_.CommandLine -match 'run_keepalive') -and ($_.CommandLine -like ('*' + $d + '*')) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>&1
ping -n 2 127.0.0.1 >nul
REM Pass 2: kill the actual bot + web python processes for THIS folder only.
REM Never a running plugin deploy (run.bat --deploy-plugin*): killing it mid-restart leaves the game
REM server offline and its deploy lock behind.
powershell -NoProfile -Command "$d=(Resolve-Path '%~dp0..').Path; if (-not $d.EndsWith([char]92)) { $d=$d+[char]92 }; Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | Where-Object { $_.CommandLine -match 'no_mapvote_bot\.py|cc_web\.py' -and $_.CommandLine -notmatch '--deploy-plugin' -and $_.CommandLine -like ('*' + $d + '*') } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>&1
REM Pass 3: close the CONSOLE WINDOWS those pythons were running in. Killing the python alone leaves
REM its cmd.exe host on screen as a dead window, so after a relaunch you would see two bot windows and
REM two web CC windows and not know which is live. Matched on THIS folder's two launcher bats only.
powershell -NoProfile -Command "$d=(Resolve-Path '%~dp0..').Path; if (-not $d.EndsWith([char]92)) { $d=$d+[char]92 }; $me=$PID; Get-CimInstance Win32_Process -Filter \"Name='cmd.exe'\" | Where-Object { $_.ProcessId -ne $me -and $_.CommandLine -and ($_.CommandLine -match '1\. Start Bot\.bat|2\. Start Web Command Centre\.bat') -and ($_.CommandLine -like ('*' + $d + '*')) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>&1
ping -n 3 127.0.0.1 >nul
echo Opening the BOT window...
start "Nuke Option - Bot" "%~dp01. Start Bot.bat"
ping -n 4 127.0.0.1 >nul
echo Opening the WEB COMMAND CENTRE window (a browser tab will open too)...
start "Nuke Option - Web Command Centre" "%~dp02. Start Web Command Centre.bat"
echo.
echo Done - ONE bot window and ONE web CC window have opened. Leave them OPEN.
echo You can close THIS window now.
ping -n 7 127.0.0.1 >nul
