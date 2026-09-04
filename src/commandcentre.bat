@echo off
REM ===================================================================
REM  Nuclear Option - UNIFIED COMMAND CENTRE (single window)
REM  Live server console + players table + activity feed + a command
REM  console, all in one screen. Replaces watch.bat AND centre.bat.
REM  This is a VIEWER/controller - closing it does NOT stop the bot.
REM ===================================================================
chcp 65001 >nul
title Nuclear Option - Command Centre
cd /d "%~dp0"

REM Strip the trailing backslash from %~dp0. A path ending in "\" inside double
REM quotes makes the next char escape the quote, which breaks Windows Terminal's
REM argument parsing (the cause of the earlier "cannot find the file" error).
set "HERE=%~dp0"
if "%HERE:~-1%"=="\" set "HERE=%HERE:~0,-1%"

REM Prefer Windows Terminal (maximised) for the nicest rendering.
where wt >nul 2>nul
if errorlevel 1 goto plain
wt.exe --maximized -d "%HERE%" cmd /k python -u command_centre.py
exit /b

:plain
REM Fallback: run in this console (Textual works here too - just maximise the window).
mode con: cols=200 lines=55 >nul 2>nul
python -u command_centre.py
echo.
echo (command centre closed - the bot keeps running in the background)
pause
