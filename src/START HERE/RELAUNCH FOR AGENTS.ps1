# Detached relaunch for automation/agent tooling — NEVER call the START HERE .bat files
# from an attached terminal. This script folder-scopes kill, then Start-Process exactly
# two bats (bot + webcc). Safe to run from an automated PowerShell session.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File "...\START HERE\RELAUNCH FOR AGENTS.ps1"
$ErrorActionPreference = 'SilentlyContinue'
$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here '..')).Path
if (-not $root.EndsWith('\')) { $root = $root + '\' }
$botBat = Join-Path $here '1. Start Bot.bat'
$webBat = Join-Path $here '2. Start Web Command Centre.bat'
if (-not (Test-Path -LiteralPath $botBat)) { throw "Missing: $botBat" }
if (-not (Test-Path -LiteralPath $webBat)) { throw "Missing: $webBat" }

Write-Host "[relaunch] folder-scoped stop: $root"
Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
  Where-Object { $_.CommandLine -and ($_.CommandLine -match 'no_mapvote_bot\.py|cc_web\.py') -and ($_.CommandLine -like ("*$root*")) } |
  ForEach-Object { Write-Host "  kill python PID $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force }
Start-Sleep -Seconds 1
Get-CimInstance Win32_Process -Filter "Name='cmd.exe'" |
  Where-Object {
    $_.CommandLine -and ($_.CommandLine -like ("*$root*")) -and
    ($_.CommandLine -match 'START HERE|Start Bot|Web Command|run\.bat|webcc\.bat')
  } |
  ForEach-Object { Write-Host "  kill cmd PID $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force }
Start-Sleep -Seconds 2

Write-Host '[relaunch] detached Start-Process x2 (not agent-terminal attached)'
Start-Process -FilePath $botBat -WorkingDirectory $root.TrimEnd('\')
Start-Sleep -Seconds 2
Start-Process -FilePath $webBat -WorkingDirectory $root.TrimEnd('\')
Start-Sleep -Seconds 4

$bots = @(Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
  Where-Object { $_.CommandLine -and ($_.CommandLine -match 'no_mapvote_bot\.py') -and ($_.CommandLine -like ("*$root*")) })
$webs = @(Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
  Where-Object { $_.CommandLine -and ($_.CommandLine -match 'cc_web\.py') -and ($_.CommandLine -like ("*$root*")) })
$cmds = @(Get-CimInstance Win32_Process -Filter "Name='cmd.exe'" |
  Where-Object { $_.CommandLine -and ($_.CommandLine -like ("*$root*")) -and ($_.CommandLine -match 'START HERE|Start Bot|Web Command|run\.bat|webcc\.bat') })
Write-Host ("[relaunch] assert bot={0} webcc={1} cmd_hosts={2}" -f $bots.Count, $webs.Count, $cmds.Count)
if ($bots.Count -ne 1 -or $webs.Count -ne 1) { exit 2 }
if ($cmds.Count -gt 2) {
  Write-Host '[relaunch] trimming extra empty cmd hosts...'
  foreach ($c in $cmds) {
    $hasPy = [bool](Get-CimInstance Win32_Process -Filter "Name='python.exe'" | Where-Object { $_.ParentProcessId -eq $c.ProcessId })
    if (-not $hasPy) { Stop-Process -Id $c.ProcessId -Force; Write-Host "  killed orphan cmd $($c.ProcessId)" }
  }
}
Write-Host '[relaunch] OK - 1 bot + 1 webcc detached'
exit 0
