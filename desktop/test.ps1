param([string]$Executable = 'dist/publish/BOOTH-Shelf.exe')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $root $Executable
$testRoot = Join-Path $root ('core-data-test-' + [guid]::NewGuid().ToString('N'))
$process = Start-Process -FilePath $target -ArgumentList ('--core-test --data-dir "' + $testRoot + '"') -WorkingDirectory $root -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(120000)) { $process.Kill(); throw 'Core tests exceeded two minutes' }
$reportPath = Join-Path $testRoot 'core-result.json'
if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Core tests did not produce a report' }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or -not $report.passed) { throw ('Core tests failed: ' + $report.error) }
Write-Output ('Core tests passed: ' + $reportPath)
