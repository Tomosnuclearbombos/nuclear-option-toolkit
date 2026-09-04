@echo off
setlocal
REM Send a chat message to the game.
REM   * Double-click this file and type your message when prompted, OR
REM   * run from a terminal:   say.bat your message here
if "%~1"=="" (
    set /p "msg=Message to send to game chat: "
) else (
    set "msg=%*"
)
if not defined msg ( echo No message entered. & pause & exit /b )
REM quote %msg% so &, |, <, > in the message aren't re-parsed by cmd
call "%~dp0run.bat" --say "%msg%"
echo.
echo (sent -- check game chat)
pause
