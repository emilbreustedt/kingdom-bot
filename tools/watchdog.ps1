# Restarts Kingdom when it crashed or hangs (state.json not updated for $StaleSeconds).
# Usage: powershell -File tools\watchdog.ps1   (runs until stopped)
param([string]$GamePath = (& "$PSScriptRoot\find-game.ps1"), [int]$StaleSeconds = 90)
$state = "$GamePath\BepInEx\KingdomBot\state.json"
$lastRestart = Get-Date
while ($true) {
    Start-Sleep 20
    if (((Get-Date) - $lastRestart).TotalSeconds -lt 120) { continue }   # give a fresh start time to load
    if (Test-Path "$GamePath\BepInEx\KingdomBot\watchdog.pause") { continue }   # deploy in progress
    $proc = Get-Process Kingdom -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$GamePath*" }
    $age = if (Test-Path $state) { ((Get-Date) - (Get-Item $state).LastWriteTime).TotalSeconds } else { 9999 }
    if ($proc -and $age -lt $StaleSeconds) { continue }
    $why = if (-not $proc) { 'game not running' } else { "state.json stale for $([int]$age) s" }
    Write-Output "$(Get-Date -Format 'HH:mm:ss') watchdog: $why -> restart"
    if ($proc) { $proc | Stop-Process -Force; Start-Sleep 3 }
    & "$PSScriptRoot\..\start.ps1" -GamePath $GamePath | Out-Null
    $lastRestart = Get-Date
}
