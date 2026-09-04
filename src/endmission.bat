@echo off
setlocal
REM Force the current mission to end -- used to test the vote flow end to end.
REM WARNING: this really ends the current round for everyone on the server!
echo.
echo  *** This ENDS the current mission for everyone on the server. ***
echo.
set /p "ok=Type Y then Enter to do it (anything else cancels): "
if /i not "%ok%"=="Y" ( echo Cancelled. & pause & exit /b )
call "%~dp0run.bat" --endmission
echo.
pause
