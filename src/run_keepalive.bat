@echo off
REM ===================================================================
REM  Nuclear Option map-vote bot - keep-alive wrapper.
REM  Runs run.bat (which sets the SFTP env vars and starts the bot) in a
REM  loop. If the bot process ever dies UNEXPECTEDLY (killed / OOM / a
REM  fatal crash) it is relaunched after a short pause. A CLEAN stop
REM  (Ctrl-C -> the bot exits with code 0) ends the loop so you can stop
REM  it on purpose. All restarts are timestamped in keepalive.log.
REM
REM  The bot ALSO self-heals internally (no_mapvote_bot.py restarts main()
REM  on any unhandled exception), so this wrapper is the outer safety net
REM  for process-level death that Python can't catch.
REM
REM  To stop completely: close this window, or press Ctrl-C and choose to
REM  terminate. To restart the bot with new code: kill the python.exe
REM  child -- this wrapper will relaunch it on the new code automatically.
REM ===================================================================
title Nuke Option Bot - keep-alive
cd /d "%~dp0"
REM start a fresh bot output log for this wrapper session
type nul > "%~dp0bot_output.log"

:loop
echo [keepalive] launching bot at %date% %time%>>"%~dp0keepalive.log"
call "%~dp0run.bat" >> "%~dp0bot_output.log" 2>&1
set "RC=%errorlevel%"
echo [keepalive] bot exited (code %RC%) at %date% %time%>>"%~dp0keepalive.log"
if "%RC%"=="0" (
    echo [keepalive] clean exit - not relaunching.>>"%~dp0keepalive.log"
    goto end
)
echo [keepalive] relaunching in 5s...>>"%~dp0keepalive.log"
timeout /t 5 /nobreak >nul 2>&1
goto loop

:end
echo [keepalive] stopped at %date% %time%>>"%~dp0keepalive.log"
