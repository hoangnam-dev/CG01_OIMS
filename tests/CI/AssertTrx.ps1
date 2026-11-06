param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
{
    throw "Test result file is missing: $Path"
}

[xml]$results = Get-Content -LiteralPath $Path -Raw
$cases = @($results.TestRun.Results.UnitTestResult)
if ($cases.Count -eq 0)
{
    throw "Test result file contains no tests: $Path"
}

$unexpected = @($cases | Where-Object { $_.outcome -ne 'Passed' })
if ($unexpected.Count -gt 0)
{
    $outcomes = ($unexpected | ForEach-Object { "$($_.testName): $($_.outcome)" }) -join ', '
    throw "Test result contains failed or skipped tests: $outcomes"
}

Write-Output "Verified $($cases.Count) passed tests with no skips in $Path"
