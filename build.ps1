# Builds KingdomBot.dll and copies it into the game's BepInEx plugins folder.
# Uses the C# compiler that ships with Windows (.NET Framework 4) - no SDK needed.
param([string]$GamePath = (& "$PSScriptRoot\tools\find-game.ps1"))
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$managed = "$GamePath\Kingdom_Data\Managed"
$core = "$GamePath\BepInEx\core"
$csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if (-not (Test-Path $core)) { throw "BepInEx not found in '$GamePath' - see README (Installation)." }
if (-not (Test-Path "$root\lib\Assembly-CSharp.publicized.dll")) { & "$root\tools\publicize.ps1" -GamePath $GamePath }
New-Item -ItemType Directory -Force "$root\bin" | Out-Null
$refs = @("$managed\mscorlib.dll", "$managed\System.dll", "$managed\System.Core.dll", "$managed\UnityEngine.dll", "$managed\Assembly-CSharp-firstpass.dll",
          "$root\lib\Assembly-CSharp.publicized.dll", "$core\BepInEx.dll", "$core\0Harmony.dll") | ForEach-Object { "/r:$_" }
& $csc /nologo /target:library /nostdlib+ /noconfig /optimize+ /out:"$root\bin\KingdomBot.dll" @refs (Get-ChildItem "$root\src\*.cs").FullName
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
New-Item -ItemType Directory -Force "$GamePath\BepInEx\plugins" | Out-Null
Copy-Item "$root\bin\KingdomBot.dll" "$GamePath\BepInEx\plugins\" -Force
Write-Output "OK -> $GamePath\BepInEx\plugins\KingdomBot.dll"
