# Prints the Kingdom (Classic) install folder: $env:KINGDOM_PATH, or searched in all Steam libraries.
if ($env:KINGDOM_PATH) { return $env:KINGDOM_PATH }
$steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
if (-not $steam) { $steam = 'C:\Program Files (x86)\Steam' }
$libs = @($steam)
$vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
if (Test-Path $vdf) {
    Select-String -Path $vdf -Pattern '"path"\s+"(.+)"' | ForEach-Object { $libs += $_.Matches[0].Groups[1].Value -replace '\\\\', '\' }
}
foreach ($l in $libs | Select-Object -Unique) {
    $p = Join-Path $l 'steamapps\common\Kingdom'
    if (Test-Path (Join-Path $p 'Kingdom.exe')) { return (Resolve-Path $p).Path }
}
throw 'Kingdom not found - set $env:KINGDOM_PATH to the game folder.'
