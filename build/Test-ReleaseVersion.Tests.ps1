$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$testRoot = Join-Path $repositoryRoot 'artifacts/validation/release-contract-tests'
$validator = Join-Path $PSScriptRoot 'Test-ReleaseVersion.ps1'

if (Test-Path -LiteralPath $testRoot) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

function Invoke-Case {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ProjectVersion,
        [Parameter(Mandatory = $true)][string]$ReleaseVersion,
        [Parameter(Mandatory = $true)][string]$Changelog,
        [Parameter(Mandatory = $true)][bool]$ShouldPass,
        [string]$ExpectedMessage
    )

    $caseRoot = Join-Path $testRoot $Name
    New-Item -ItemType Directory -Force -Path $caseRoot | Out-Null
    $projectPath = Join-Path $caseRoot 'Package.csproj'
    $changelogPath = Join-Path $caseRoot 'CHANGELOG.md'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <PackageVersion>$ProjectVersion</PackageVersion>
  </PropertyGroup>
</Project>
"@ | Set-Content -LiteralPath $projectPath -Encoding utf8NoBOM
    $Changelog | Set-Content -LiteralPath $changelogPath -Encoding utf8NoBOM

    try {
        $output = (& $validator `
                -Version $ReleaseVersion `
                -ExpectedReleaseDate '2026-09-23' `
                -ChangelogPath $changelogPath `
                -ProjectPath $projectPath 2>&1 | Out-String)
        $exitCode = 0
    }
    catch {
        $output = $_.Exception.Message
        $exitCode = 1
    }
    Write-Host "Release contract case '$Name': exit=$exitCode"

    if ($ShouldPass) {
        if ($exitCode -ne 0) {
            throw "Release contract case '$Name' should pass. Output: $output"
        }
        return
    }

    if ($exitCode -eq 0) {
        throw "Release contract case '$Name' should fail."
    }
    if ($ExpectedMessage -and $output.IndexOf($ExpectedMessage, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Release contract case '$Name' did not report '$ExpectedMessage'. Output: $output"
    }
}

$finalized = @'
# Changelog

## [Unreleased]

## [0.1.0] - 2026-09-23

### Added

- Provides the initial package capability.
'@

$unreleased = @'
# Changelog

## [Unreleased]

### Added

- Provides the initial package capability.
'@

$planned = @'
# Changelog

## [Unreleased]

## [0.1.0] - Planned

### Added

- Provides the initial package capability.
'@

Invoke-Case -Name 'finalized' -ProjectVersion '0.1.0' -ReleaseVersion '0.1.0' -Changelog $finalized -ShouldPass $true
Invoke-Case -Name 'unreleased' -ProjectVersion '0.1.0' -ReleaseVersion '0.1.0' -Changelog $unreleased -ShouldPass $false -ExpectedMessage 'must contain exactly one finalized [0.1.0] section'
Invoke-Case -Name 'planned' -ProjectVersion '0.1.0' -ReleaseVersion '0.1.0' -Changelog $planned -ShouldPass $false -ExpectedMessage "date 'Planned' does not match"
Invoke-Case -Name 'version-mismatch' -ProjectVersion '0.1.1' -ReleaseVersion '0.1.0' -Changelog $finalized -ShouldPass $false -ExpectedMessage "package version '0.1.1' does not match release version '0.1.0'"

Write-Host 'Release contract regression tests passed.'
