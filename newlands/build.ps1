# Builds KingdomBot.NewLands.dll (compiled against the ORIGINAL game assembly: private members only via Acc/reflection).
param([string]$GamePath = $(if ($env:KINGDOM_NL_PATH) { $env:KINGDOM_NL_PATH } else { 'C:\Program Files (x86)\Steam\steamapps\common\Kingdom New Lands' }))
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$managed = "$GamePath\Kingdom_Data\Managed"
$core = "$GamePath\BepInEx\core"
if (-not (Test-Path $core)) {
    # BepInEx not installed in New Lands yet: compile against the Classic copy (same BepInEx 5 version)
    $core = Join-Path (& "$root\..\tools\find-game.ps1") 'BepInEx\core'
}
$csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
New-Item -ItemType Directory -Force "$root\bin" | Out-Null
$refs = @("$managed\mscorlib.dll", "$managed\System.dll", "$managed\System.Core.dll", "$managed\UnityEngine.dll",
          "$managed\Assembly-CSharp.dll", "$managed\Assembly-CSharp-firstpass.dll", "$core\BepInEx.dll", "$core\0Harmony.dll")
foreach ($m in 'UnityEngine.CoreModule', 'UnityEngine.UI') { if (Test-Path "$managed\$m.dll") { $refs += "$managed\$m.dll" } }
$refs = $refs | ForEach-Object { "/r:$_" }
& $csc /nologo /target:library /nostdlib+ /noconfig /optimize+ /out:"$root\bin\KingdomBot.NewLands.dll" @refs (Get-ChildItem "$root\src\*.cs").FullName
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
if (Test-Path "$GamePath\BepInEx\plugins") { Copy-Item "$root\bin\KingdomBot.NewLands.dll" "$GamePath\BepInEx\plugins\" -Force; Write-Output "OK -> $GamePath\BepInEx\plugins" }
else { Write-Output "OK -> $root\bin (BepInEx not installed in New Lands yet)" }
