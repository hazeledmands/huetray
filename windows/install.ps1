# Builds Hue Tray, installs it to %LOCALAPPDATA%\Programs\HueTray, sets it to start at login,
# and launches it. Run with:  powershell -ExecutionPolicy Bypass -File windows\install.ps1
$ErrorActionPreference = 'Stop'

# Stop any running copy (tray app or a terminal hue-tray) first, so its files can be replaced.
$running = Get-Process -Name HueTray, hue-tray -ErrorAction SilentlyContinue
if ($running) { $running | Stop-Process -Force; Start-Sleep -Seconds 1 }

& (Join-Path $PSScriptRoot 'build.ps1')

$dest = Join-Path $env:LOCALAPPDATA 'Programs\HueTray'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item (Join-Path $PSScriptRoot '..\build\windows\*.exe') $dest -Force

# A Start menu shortcut, so it can be found and started from there.
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Hue Tray.lnk'))
$shortcut.TargetPath = Join-Path $dest 'HueTray.exe'
$shortcut.WorkingDirectory = $dest
$shortcut.Description = 'Controls Philips Hue lights from the notification area'
$shortcut.Save()

& (Join-Path $dest 'hue-tray.exe') login on
Start-Process (Join-Path $dest 'HueTray.exe')
Write-Output "Installed $dest"
