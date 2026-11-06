# Updates the bot without losing the current run: save & quit -> build -> start again (the game loads the AUTO save).
param([string]$GamePath = (& "$PSScriptRoot\tools\find-game.ps1"), [switch]$Force)
$ErrorActionPreference = 'Stop'
$pause = "$GamePath\BepInEx\KingdomBot\watchdog.pause"
New-Item -ItemType File -Force $pause | Out-Null   # keep tools\watchdog.ps1 from restarting the game mid-deploy
try {
$proc = Get-Process Kingdom -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$GamePath*" }
if ($proc) {
    # never interrupt a promising run: save & quit late in a run (e.g. during the final wave) can lose it
    try { $st0 = Get-Content "$GamePath\BepInEx\KingdomBot\state.json" -ErrorAction Stop | ConvertFrom-Json } catch { $st0 = $null }
    if (-not $Force -and $st0 -and $st0.gameState -eq 'Game' -and $st0.day -ge 15) {
        Write-Output "Run is on day $($st0.day) - not deploying (would risk the run). Use -Force to override."
        exit 1
    }
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
} finally { Remove-Item $pause -ErrorAction SilentlyContinue }
