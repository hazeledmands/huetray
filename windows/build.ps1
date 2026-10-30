# Builds build\windows\HueTray.exe (the tray app) and build\windows\hue-tray.exe (the terminal
# commands) from this folder. Needs the .NET 10 SDK: winget install Microsoft.DotNet.SDK.10
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$out = Join-Path $PSScriptRoot '..\build\windows'

dotnet publish HueTray.csproj -c Release -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw 'Building HueTray.exe failed' }

# The same project again as a console program (it uses its own obj folder; see HueTray.csproj).
dotnet publish HueTray.csproj -c Release -o $out --nologo -p:Cli=true
if ($LASTEXITCODE -ne 0) { throw 'Building hue-tray.exe failed' }

Write-Output "Built $(Resolve-Path $out)"
