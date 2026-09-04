@echo off
cd /d "%~dp0"
REM Resolve WebCC port from .nost-data\config.json (web.port) so S2 opens :8771, not hardcoded :8770.
if not defined NOST_DATA_DIR set "NOST_DATA_DIR=%~dp0.nost-data"
set "WEBPORT=8770"
if exist "%NOST_DATA_DIR%\config.json" (
  for /f "usebackq delims=" %%P in (`powershell -NoProfile -Command "try { $p=(Get-Content -Raw '%NOST_DATA_DIR%\config.json' | ConvertFrom-Json).web.port; if ($p) { $p } else { 8770 } } catch { 8770 }"`) do set "WEBPORT=%%P"
)
echo ============================================
echo   Nuke Option - Web Command Centre
echo   http://127.0.0.1:%WEBPORT%
echo ============================================
echo Stopping any old command-centre instances for THIS folder only...
powershell -NoProfile -Command "$d='%~dp0'; if (-not $d.EndsWith([char]92)) { $d=$d+[char]92 }; Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | Where-Object { $_.CommandLine -like '*cc_web.py*' -and $_.CommandLine -like ('*' + $d + '*') } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>&1
REM --- Resolve Python the same way as run.bat: prefer a known install path (bare `python`
REM     is often missing from PATH for cmd/batch children after reboot), then where.exe, then py.
set "PYEXE="
set "PYARGS="
if exist "%LocalAppData%\Programs\Python\Python314\python.exe" set "PYEXE=%LocalAppData%\Programs\Python\Python314\python.exe"
if not defined PYEXE if exist "%LocalAppData%\Programs\Python\Python313\python.exe" set "PYEXE=%LocalAppData%\Programs\Python\Python313\python.exe"
if not defined PYEXE if exist "%LocalAppData%\Programs\Python\Python312\python.exe" set "PYEXE=%LocalAppData%\Programs\Python\Python312\python.exe"
if not defined PYEXE for /f "delims=" %%P in ('where python 2^>nul') do ( set "PYEXE=%%P" & goto :have_py )
if not defined PYEXE for /f "delims=" %%P in ('where py 2^>nul') do ( set "PYEXE=%%P" & set "PYARGS=-3" & goto :have_py )
:have_py
if not defined PYEXE (
  echo.
  echo Python 3 was not found. Install it from python.org ^(tick "Add to PATH"^),
  echo or ensure %%LocalAppData%%\Programs\Python\Python3xx\python.exe exists.
  echo.
  echo Server stopped.
  pause
  exit /b 9009
)
echo Opening your browser... (Ctrl+F5 to hard-refresh if it looks stale)
start "" "http://127.0.0.1:%WEBPORT%"
"%PYEXE%" %PYARGS% -u "%~dp0cc_web.py"
echo.
echo Server stopped.
pause
