# Starts Kingdom windowed on the widest connected screen and optionally minimizes it.
#   .\start.ps1            -> windowed on the widest screen (e.g. a 32:9 monitor)
#   .\start.ps1 -Minimize  -> start, move, then minimize (bot keeps running)
param([switch]$Minimize, [int]$Width = 1600, [int]$Height = 900, [string]$GamePath = (& "$PSScriptRoot\tools\find-game.ps1"))
$game = $GamePath
if (Get-Process Kingdom -ErrorAction SilentlyContinue) { Write-Output 'Kingdom läuft bereits.'; return }
# lets Kingdom.exe be started directly (Steam must be running; achievements still count)
Set-Content "$game\steam_appid.txt" '368230' -Encoding ascii

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Win {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
'@
# target = widest screen (32:9 Odyssey); fallback: first non-primary
$screens = [System.Windows.Forms.Screen]::AllScreens
$target = $screens | Sort-Object { $_.Bounds.Width / $_.Bounds.Height } -Descending | Select-Object -First 1
$b = $target.Bounds
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
