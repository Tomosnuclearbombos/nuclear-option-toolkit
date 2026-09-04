@echo off
REM Build NukeStats.dll. Needs the .NET SDK (winget install Microsoft.DotNet.SDK.8)
REM and the reference DLLs in .\libs\ (see README.md). Output: bin\Release\NukeStats.dll
setlocal
set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
"%DOTNET%" build "%~dp0NukeStats.csproj" -c Release
echo.
echo (build done -- look for bin\Release\NukeStats.dll above)
