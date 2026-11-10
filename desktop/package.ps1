param([string]$PublishDirectory = 'dist/publish', [string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'BoothShelf.csproj') -Raw)
$version = $project.Project.PropertyGroup.Version
$output = Join-Path $root $OutputDirectory
$name = 'BOOTH-Shelf-v' + $version + '-win-x64'
$bundle = Join-Path $output $name
if (Test-Path -LiteralPath $bundle) { throw 'Package directory already exists; select a fresh OutputDirectory.' }
New-Item -ItemType Directory -Path $bundle -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root "$PublishDirectory/BOOTH-Shelf.exe") -Destination $bundle
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $bundle
Copy-Item -LiteralPath (Join-Path $root 'docs/USAGE.zh-CN.md') -Destination (Join-Path $bundle '使用说明.md')
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $bundle -Recurse
$archive = Join-Path $output ($name + '.zip')
Compress-Archive -LiteralPath $bundle -DestinationPath $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $name.zip`n", [Text.UTF8Encoding]::new($false))
Write-Output $archive
