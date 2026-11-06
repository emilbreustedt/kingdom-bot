# Starts Kingdom: New Lands windowed (same launcher as Classic, different folder and Steam app id).
param([switch]$Minimize)
& "$PSScriptRoot\..\start.ps1" -GamePath 'C:\Program Files (x86)\Steam\steamapps\common\Kingdom New Lands' -AppId '496300' -Minimize:$Minimize
