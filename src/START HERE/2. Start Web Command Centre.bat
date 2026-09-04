@echo off
for %%I in ("%~dp0..\.") do set "NOST_FOLDER=%%~nxI"
title Nuke Option WEBCC - %NOST_FOLDER%
echo Folder: %NOST_FOLDER%
set "NOST_DATA_DIR=%~dp0..\.nost-data"
call "%~dp0..\webcc.bat"
REM webcc.bat shows its own 10s self-close countdown when it stops.
exit
