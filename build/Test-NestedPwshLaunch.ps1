$ErrorActionPreference = 'Stop'

$helperPath = Join-Path $PSScriptRoot 'Invoke-NestedPwsh.ps1'
$rawLaunches = @(
    Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File |
        Where-Object { $_.FullName -ne $helperPath } |
        Select-String -Pattern '&\s*pwsh(?:\s|$)'
)

if ($rawLaunches.Count -gt 0) {
    $details = $rawLaunches | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
    throw "Direct nested PowerShell launches must use Invoke-NestedPwsh:`n$($details -join "`n")"
}

if (-not (Test-Path -LiteralPath $helperPath)) {
    throw "Shared nested PowerShell launch helper is missing: $helperPath"
}

Write-Output 'Nested PowerShell launch guard passed.'
