# Starts Kingdom windowed on a chosen screen and optionally minimizes it.
#   .\start.ps1                     -> screen from screen.txt (next to this script), else the widest screen
#   .\start.ps1 -Screen portrait    -> the portrait (rotated) monitor
#   .\start.ps1 -Screen DISPLAY2    -> by device name (part of it is enough)
#   .\start.ps1 -Minimize  -> start, move, then minimize (bot keeps running)
param([switch]$Minimize, [string]$Screen = "", [int]$Width = 1600, [int]$Height = 900, [string]$GamePath = (& "$PSScriptRoot\tools\find-game.ps1"), [string]$AppId = "368230")
$game = $GamePath
if (Get-Process Kingdom -ErrorAction SilentlyContinue) { Write-Output 'A Kingdom game (Classic or New Lands) is already running.'; return }
# keep the previous run's log (BepInEx overwrites LogOutput.log on every start): last 10 in BepInEx\KingdomBot\logs
$log = "$game\BepInEx\LogOutput.log"
if (Test-Path $log) {
    $arch = "$game\BepInEx\KingdomBot\logs"; New-Item -ItemType Directory -Force $arch | Out-Null
    Copy-Item $log "$arch\LogOutput-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
    Get-ChildItem $arch -Filter 'LogOutput-*.log' | Sort-Object Name -Descending | Select-Object -Skip 10 | Remove-Item
}
# lets Kingdom.exe be started directly (Steam must be running; achievements still count)
Set-Content "$game\steam_appid.txt" $AppId -Encoding ascii

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Win {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
'@
# target screen: -Screen, else screen.txt, else the widest screen (32:9 Odyssey)
if (-not $Screen -and (Test-Path "$PSScriptRoot\screen.txt")) { $Screen = (Get-Content "$PSScriptRoot\screen.txt" -Raw).Trim() }
$screens = [System.Windows.Forms.Screen]::AllScreens
$target = $null
if ($Screen -eq 'portrait') { $target = $screens | Where-Object { $_.Bounds.Height -gt $_.Bounds.Width } | Select-Object -First 1 }
elseif ($Screen -and $Screen -ne 'widest') { $target = $screens | Where-Object { $_.DeviceName -like "*$Screen*" } | Select-Object -First 1 }
if (-not $target) { $target = $screens | Sort-Object { $_.Bounds.Width / $_.Bounds.Height } -Descending | Select-Object -First 1 }
$b = $target.Bounds
if ($b.Width -lt $Width + 40) { $Width = $b.Width - 40; $Height = [int]($Width * 9 / 16) }   # narrow (portrait) screen: shrink the window to fit
$x = $b.X + [int](($b.Width - $Width) / 2); $y = $b.Y + [int](($b.Height - $Height) / 2)

$p = Start-Process "$game\Kingdom.exe" -ArgumentList "-screen-fullscreen 0 -screen-width $Width -screen-height $Height" -WorkingDirectory $game -PassThru
# move the window as soon as it exists (polls quickly to minimise time on the primary screen)
for ($i = 0; $i -lt 300; $i++) {
  Start-Sleep -Milliseconds 50
  $p.Refresh()
  if ($p.MainWindowHandle -ne [IntPtr]::Zero) { break }
}
if ($p.MainWindowHandle -eq [IntPtr]::Zero) { Write-Output 'Fenster nicht gefunden.'; return }
[Win]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, $x, $y, 0, 0, 0x0001 -bor 0x0004) | Out-Null  # NOSIZE|NOZORDER
if ($Minimize) { Start-Sleep 2; [Win]::ShowWindow($p.MainWindowHandle, 6) | Out-Null }  # SW_MINIMIZE
Write-Output "Kingdom gestartet (PID $($p.Id)) auf $($target.DeviceName)."
