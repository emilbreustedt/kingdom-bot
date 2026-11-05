# Updates the bot without losing the current run: save & quit -> build -> start again (the game loads the AUTO save).
param([string]$GamePath = (& "$PSScriptRoot\tools\find-game.ps1"))
$ErrorActionPreference = 'Stop'
$proc = Get-Process Kingdom -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$GamePath*" }
if ($proc) {
    # save only while no enemies exist (daytime, after they retreated): saved enemies come back as frozen "ghosts"
    for ($i = 0; $i -lt 120; $i++) {
        try { $st = Get-Content "$GamePath\BepInEx\KingdomBot\state.json" -ErrorAction Stop | ConvertFrom-Json } catch { Start-Sleep 1; continue }
        if ($st.gameState -ne 'Game' -or ($st.enemies.Count -eq 0 -and $st.hour -ge 7 -and $st.hour -lt 16)) { break }
        Start-Sleep 1
    }
    Set-Content "$GamePath\BepInEx\KingdomBot\cmd.txt" 'savequit' -Encoding ascii
    if (-not $proc.WaitForExit(20000)) { Write-Output 'Game did not quit in time - killing it'; $proc | Stop-Process -Force; Start-Sleep 2 }
}
& "$PSScriptRoot\build.ps1" -GamePath $GamePath
& "$PSScriptRoot\start.ps1" -GamePath $GamePath
