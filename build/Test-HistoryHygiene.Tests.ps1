param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-NestedPwsh.ps1')

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Get-Text {
    param([Parameter(Mandatory = $true)][int[]]$CodePoint)

    return (($CodePoint | ForEach-Object { [char]$_ }) -join '')
}

$gate = Join-Path $PSScriptRoot 'Test-HistoryHygiene.ps1'
$positiveOutput = (Invoke-NestedPwsh -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $gate, '-RepositoryRoot', $RepositoryRoot) 2>&1 | Out-String)
$positiveExitCode = $LASTEXITCODE
Assert-That ($positiveExitCode -eq 0) "clean repository history must pass the hygiene gate: $positiveOutput"
Assert-That ($positiveOutput -match 'reachable commits scanned') 'clean positive control must report the reachable scan'
Write-Host 'History hygiene positive control passed.'

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('logschema-history-hygiene-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    & git -C $fixtureRoot init --initial-branch=main 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'could not initialize history hygiene fixture' }
    & git -C $fixtureRoot -c user.name='KeelMatrix' -c user.email='keelmatrix@gmail.com' commit --allow-empty -m 'Initial repository state' 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'could not create clean history hygiene fixture commit' }

    $trailer = Get-Text -CodePoint @(67, 111, 45, 65, 117, 116, 104, 111, 114, 101, 100, 45, 66, 121)
    $messagePath = Join-Path $fixtureRoot 'forbidden-message.txt'
    "Clean change`n`n$trailer`: Example <example@example.invalid>" | Set-Content -LiteralPath $messagePath -Encoding utf8NoBOM
    & git -C $fixtureRoot -c user.name='KeelMatrix' -c user.email='keelmatrix@gmail.com' commit --allow-empty -F $messagePath 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'could not create negative history hygiene fixture commit' }

    $negativeOutput = (Invoke-NestedPwsh -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $gate, '-RepositoryRoot', $fixtureRoot) 2>&1 | Out-String)
    $negativeExitCode = $LASTEXITCODE
    Assert-That ($negativeExitCode -ne 0) 'fixture containing forbidden commit metadata must fail the hygiene gate'
    Assert-That ($negativeOutput -match 'History hygiene failed') 'negative control must report a hygiene failure'
    Write-Host 'History hygiene negative control passed: planted commit metadata was rejected.'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}

Write-Host 'History hygiene regression tests passed.'
