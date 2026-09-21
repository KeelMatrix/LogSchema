$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repositoryRoot 'artifacts/validation'
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

function Invoke-Timed {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    & $Action
    $actionExitCode = $LASTEXITCODE
    $timer.Stop()
    Write-Host ("{0}: {1:N2}s" -f $Label, $timer.Elapsed.TotalSeconds)
    if ($actionExitCode -ne 0) {
        throw "$Label failed with exit code $actionExitCode"
    }
}

function Assert-That {
    param([Parameter(Mandatory = $true)][bool]$Condition, [Parameter(Mandatory = $true)][string]$Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

$env:KEELMATRIX_NO_TELEMETRY = '1'

Invoke-Timed 'Restore solution' { dotnet restore (Join-Path $repositoryRoot 'LogSchema.slnx') --configfile (Join-Path $repositoryRoot 'NuGet.config') --nologo }
foreach ($fixture in @(
    'fixtures/Phase0.Net8/Phase0.Net8.csproj',
    'fixtures/Phase0.Stable/Phase0.Stable.csproj',
    'fixtures/Phase0.Multi/Phase0.Multi.csproj',
    'fixtures/Phase0.Pairing/Phase0.Pairing.csproj',
    'fixtures/Phase0.Sentinel/Phase0.Sentinel.csproj',
    'fixtures/Phase0.GeneratedDeclarationGenerator/Phase0.GeneratedDeclarationGenerator.csproj')) {
    Invoke-Timed "Restore $fixture" { dotnet restore (Join-Path $repositoryRoot $fixture) --configfile (Join-Path $repositoryRoot 'NuGet.config') --nologo }
}

Invoke-Timed 'Format and analyzer validation' { dotnet format (Join-Path $repositoryRoot 'LogSchema.slnx') --no-restore --verify-no-changes --verbosity minimal }
Invoke-Timed 'Release build' { dotnet build (Join-Path $repositoryRoot 'LogSchema.slnx') -c Release --no-restore --nologo }
Invoke-Timed 'Unit and contract tests' { dotnet test (Join-Path $repositoryRoot 'tests/KeelMatrix.LogSchema.Tests/KeelMatrix.LogSchema.Tests.csproj') -c Release --no-build --no-restore --nologo }
Invoke-Timed 'Phase 0 regression matrix' { & pwsh -NoProfile -NonInteractive -File (Join-Path $repositoryRoot 'build/Test-Phase0Matrix.ps1') }

$tool = Join-Path $repositoryRoot 'src/KeelMatrix.LogSchema/bin/Release/net8.0/KeelMatrix.LogSchema.dll'
$fixtureProject = Join-Path $repositoryRoot 'fixtures/Phase0.Net8/Phase0.Net8.csproj'
$determinismA = Join-Path $artifactRoot 'determinism-a/logschema.json'
$determinismB = Join-Path $artifactRoot 'determinism-b/logschema.json'
Invoke-Timed 'Manifest determinism' {
    & dotnet $tool capture $fixtureProject --tfm net8.0 --output $determinismA --no-telemetry
    Assert-That ($LASTEXITCODE -eq 0) 'first deterministic capture must succeed'
    & dotnet $tool capture $fixtureProject --tfm net8.0 --output $determinismB --no-telemetry
    Assert-That ($LASTEXITCODE -eq 0) 'second deterministic capture must succeed'
    $hashA = (Get-FileHash $determinismA -Algorithm SHA256).Hash
    $hashB = (Get-FileHash $determinismB -Algorithm SHA256).Hash
    Write-Host "Windows manifest A SHA256: $hashA"
    Write-Host "Windows manifest B SHA256: $hashB"
    Assert-That ($hashA -eq $hashB) 'same source must produce byte-identical manifests'
}

Invoke-Timed 'CLI failure safety' {
    & dotnet $tool check (Join-Path $repositoryRoot 'does-not-exist.csproj') --baseline $determinismA --format json --no-telemetry | Out-Null
    Assert-That ($LASTEXITCODE -eq 3) 'project-load failure must return exit 3'
    $before = (Get-FileHash $determinismA -Algorithm SHA256).Hash
    & dotnet $tool diff (Join-Path $repositoryRoot 'does-not-exist.json') $determinismA --format json --no-telemetry | Out-Null
    Assert-That ($LASTEXITCODE -eq 3) 'missing manifest must return exit 3'
    $after = (Get-FileHash $determinismA -Algorithm SHA256).Hash
    Assert-That ($before -eq $after) 'failed comparison must not rewrite the baseline'
    $global:LASTEXITCODE = 0
}

Write-Host 'Validation passed.'
