# Updates the New Lands bot without losing the current run: save & quit -> build -> start.
$ErrorActionPreference = 'Stop'
$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Kingdom New Lands'
$proc = Get-Process Kingdom -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$GamePath*" }
if ($proc) {
    Set-Content "$GamePath\BepInEx\KingdomBot\cmd.txt" 'savequit' -Encoding ascii
    if (-not $proc.WaitForExit(20000)) { Write-Output 'Game did not quit in time - killing it'; $proc | Stop-Process -Force; Start-Sleep 2 }
}
& "$PSScriptRoot\build.ps1" -GamePath $GamePath
& "$PSScriptRoot\start.ps1"
