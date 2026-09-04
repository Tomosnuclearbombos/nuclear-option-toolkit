@echo off
REM Double-click this any time to check whether the bot is running and see what
REM it's been doing. (It just READS status - it does not start or stop anything.)
chcp 65001 >nul
title Nuke Option Bot - status
echo.
echo   ============================================
echo     Nuclear Option Bot - status check
echo   ============================================
echo.
powershell -NoProfile -Command "$d='%~dp0'; if (-not $d.EndsWith([char]92)) { $d=$d+[char]92 }; $b=@(Get-CimInstance Win32_Process -Filter 'Name=''python.exe''' | Where-Object { $_.CommandLine -like '*no_mapvote_bot*' -and $_.CommandLine -like ('*'+$d+'*') }); if($b.Count){ Write-Host ('   BOT IS RUNNING   (python.exe, PID ' + $b[0].ProcessId + ')') -ForegroundColor Green } else { Write-Host '   BOT IS NOT RUNNING' -ForegroundColor Red }; $k=@(Get-CimInstance Win32_Process -Filter 'Name=''cmd.exe''' | Where-Object { $_.CommandLine -like '*run_keepalive*' -and $_.CommandLine -like ('*'+$d+'*') }); if($k.Count){ Write-Host ('   Keep-alive babysitter: running (PID ' + $k[0].ProcessId + ')') -ForegroundColor Green } else { Write-Host '   Keep-alive babysitter: NOT running' -ForegroundColor Yellow }"
echo.
echo   --- recent activity ---
echo.
powershell -NoProfile -Command "if(Test-Path -LiteralPath '%~dp0activity.log'){ Get-Content -LiteralPath '%~dp0activity.log' -Encoding UTF8 -Tail 14 | ForEach-Object { $c='Gray'; switch -Regex ($_){ '======' {$c='White'; break} '\[WIN\]|\[JOIN\]|\[OK\]' {$c='Green'; break} '\[LOSS\]|\[!\]' {$c='Red'; break} '\[CAP\]' {$c='Yellow'; break} '\[RANK\]' {$c='Magenta'; break} '\[VOTE\]|\[MAP\]' {$c='Cyan'; break} '\[BOT\]' {$c='DarkCyan'; break} '\[LEFT\]|\[INFO\]' {$c='DarkGray'; break} }; Write-Host $_ -ForegroundColor $c } } else { Write-Host '   (no activity logged yet)' }"
echo.
echo   ============================================
echo   ( Tip: double-click watch.bat for a live, scrolling view )
pause
