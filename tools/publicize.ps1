# Creates lib\Assembly-CSharp.publicized.dll from YOUR OWN Kingdom installation.
# The game's code is not part of this repository - this script derives a compile-time reference
# (all members public) locally. Requires BepInEx to be installed in the game folder (for Mono.Cecil).
param([string]$GamePath = (& "$PSScriptRoot\find-game.ps1"))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cecil = Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll'
if (-not (Test-Path $cecil)) { throw "Mono.Cecil not found - install BepInEx 5 (x86) into '$GamePath' first." }
Add-Type -Path $cecil
$src = Join-Path $GamePath 'Kingdom_Data\Managed\Assembly-CSharp.dll'
$m = [Mono.Cecil.ModuleDefinition]::ReadModule($src)
foreach ($ty in $m.GetTypes()) {
    if ($ty.IsNested) { $ty.IsNestedPublic = $true } else { $ty.IsPublic = $true }
    foreach ($f in $ty.Fields) { if (-not ($ty.Events | Where-Object { $_.Name -eq $f.Name })) { $f.IsPublic = $true } }
    foreach ($me in $ty.Methods) { $me.IsPublic = $true }
}
New-Item -ItemType Directory -Force (Join-Path $root 'lib') | Out-Null
$out = Join-Path $root 'lib\Assembly-CSharp.publicized.dll'
$m.Write($out)
Write-Output "OK -> $out"
# Note: Unity 5.3's Mono still enforces visibility at runtime, so private members are accessed via Acc (reflection).
