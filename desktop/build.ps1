param([string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
$env:NUGET_PACKAGES = Join-Path $root '.packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
dotnet publish (Join-Path $PSScriptRoot 'BoothShelf.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $root $OutputDirectory) --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { throw 'EXE build failed' }
