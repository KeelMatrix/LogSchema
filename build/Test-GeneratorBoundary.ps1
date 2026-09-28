param(
    [string]$RepositoryRoot,
    [string]$ToolAssembly
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
else {
    $RepositoryRoot = (Resolve-Path $RepositoryRoot).Path
}

if ([string]::IsNullOrWhiteSpace($ToolAssembly)) {
    $ToolAssembly = Join-Path $RepositoryRoot 'src/KeelMatrix.LogSchema/bin/Release/net8.0/KeelMatrix.LogSchema.dll'
}

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Invoke-Capture {
    param(
        [Parameter(Mandatory = $true)][string]$InputPath,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][string]$TargetFramework
    )

    $output = (& dotnet $ToolAssembly capture $InputPath --tfm $TargetFramework --format json --output $OutputPath --no-telemetry 2>&1 | Out-String).TrimEnd()
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Invoke-Restore {
    param([Parameter(Mandatory = $true)][string]$InputPath)

    & dotnet restore $InputPath --configfile (Join-Path $RepositoryRoot 'NuGet.config') --nologo | Out-Host
    Assert-That ($LASTEXITCODE -eq 0) "fixture restore failed: $InputPath"
}

function Assert-SupportedPair {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$InputPath,
        [Parameter(Mandatory = $true)][string]$TargetFramework,
        [Parameter(Mandatory = $true)][string]$ExpectedGenerator,
        [Parameter(Mandatory = $true)][string]$ExpectedAbstractions
    )

    Invoke-Restore -InputPath $InputPath
    $outputPath = Join-Path $outputRoot "$Name.json"
    $capture = Invoke-Capture -InputPath $InputPath -OutputPath $outputPath -TargetFramework $TargetFramework
    Assert-That ($capture.ExitCode -eq 0) "$Name supported pair must capture successfully: $($capture.Output)"
    $manifest = Get-Content -Raw -LiteralPath $outputPath | ConvertFrom-Json
    Assert-That (@($manifest.events).Count -gt 0 -and @($manifest.unsupported).Count -eq 0) "$Name supported pair must produce supported events without unsupported declarations"
    Assert-That (@($manifest.analysisIssues | Where-Object severity -eq 'error').Count -eq 0) "$Name supported pair must have no analysis errors"
    Write-Host "${Name}: abstractions=$ExpectedAbstractions generator=$ExpectedGenerator events=$(@($manifest.events).Count)"
}

function Assert-RejectedPair {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$InputPath,
        [Parameter(Mandatory = $true)][string]$TargetFramework,
        [Parameter(Mandatory = $true)][string]$ExpectedCode
    )

    Invoke-Restore -InputPath $InputPath
    $outputPath = Join-Path $outputRoot "$Name.json"
    $capture = Invoke-Capture -InputPath $InputPath -OutputPath $outputPath -TargetFramework $TargetFramework
    Assert-That ($capture.ExitCode -eq 3) "$Name must fail closed with exit 3: $($capture.Output)"
    Assert-That ($capture.Output -match $ExpectedCode) "${Name} must report ${ExpectedCode}: $($capture.Output)"
    Write-Host "${Name}: rejected with $ExpectedCode"
}

$outputRoot = Join-Path ([IO.Path]::GetTempPath()) ('logschema-generator-boundary-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
try {
    Assert-SupportedPair -Name 'supported-10.0.1' -InputPath (Join-Path $RepositoryRoot 'tests/PackageConsumerFixture/PackageConsumerFixture.csproj') -TargetFramework 'net8.0' -ExpectedGenerator '10.0.13.7005' -ExpectedAbstractions '10.0.1'
    Assert-SupportedPair -Name 'supported-10.0.12' -InputPath (Join-Path $RepositoryRoot 'fixtures/Phase0.CurrentStable/Phase0.CurrentStable.csproj') -TargetFramework 'net10.0' -ExpectedGenerator '10.0.14.42308' -ExpectedAbstractions '10.0.12'
    Assert-RejectedPair -Name 'unsupported-10.0.0' -InputPath (Join-Path $RepositoryRoot 'fixtures/Phase0.UnsupportedGenerator/Phase0.UnsupportedGenerator.csproj') -TargetFramework 'net8.0' -ExpectedCode 'KMLOGP009'
    Assert-RejectedPair -Name 'mismatch-10.0.12-with-10.0.13.7005' -InputPath (Join-Path $RepositoryRoot 'fixtures/Phase0.Mismatch10_12Generator10_13/Phase0.Mismatch10_12Generator10_13.csproj') -TargetFramework 'net8.0' -ExpectedCode 'KMLOGP009'
    Assert-RejectedPair -Name 'mismatch-10.0.1-with-10.0.14.42308' -InputPath (Join-Path $RepositoryRoot 'fixtures/Phase0.Mismatch10_1Generator10_14/Phase0.Mismatch10_1Generator10_14.csproj') -TargetFramework 'net8.0' -ExpectedCode 'KMLOGP009'
    Assert-RejectedPair -Name 'user-authored-spoofed-generated' -InputPath (Join-Path $RepositoryRoot 'fixtures/Phase0.FakeGenerated/Phase0.FakeGenerated.csproj') -TargetFramework 'net8.0' -ExpectedCode 'KMLOGP001'
    Assert-RejectedPair -Name 'mixed-forward' -InputPath (Join-Path $RepositoryRoot 'fixtures/Phase0.Mixed/Phase0.Mixed.sln') -TargetFramework 'net8.0' -ExpectedCode 'KMLOGP010'
    Assert-RejectedPair -Name 'mixed-reverse' -InputPath (Join-Path $RepositoryRoot 'fixtures/Phase0.Mixed/Phase0.Mixed.Reverse.sln') -TargetFramework 'net8.0' -ExpectedCode 'KMLOGP010'
    Write-Host 'Generator boundary pair and solution-scope matrix passed.'
}
finally {
    if (Test-Path -LiteralPath $outputRoot) {
        Remove-Item -LiteralPath $outputRoot -Recurse -Force
    }
}
