$ErrorActionPreference = 'Stop'

$workflowPath = Join-Path $PSScriptRoot '../.github/workflows/release.yml'
if (-not (Test-Path -LiteralPath $workflowPath -PathType Leaf)) {
    throw "Release workflow not found: $workflowPath"
}

$workflow = Get-Content -LiteralPath $workflowPath -Raw

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if ($workflow -notmatch $Pattern) {
        throw "Release workflow contract failed: $Message"
    }
}

Assert-Match '(?m)^\s*workflow_dispatch:\s*$' 'workflow_dispatch dry-run trigger is required'
Assert-Match '(?m)^\s*tags:\s*\r?\n\s*- ''v\*''\s*$' 'tag trigger is required'
Assert-Match 'Manual release runs are validation-only; dry_run must be true\.' 'manual execution must fail closed to validation only'
Assert-Match 'Test-ReleaseVersion\.ps1 -Version' 'the tag path must enforce the reusable changelog/version gate'
Assert-Match 'validate\.ps1 -ReleaseVersion' 'the release path must run the release-equivalent repository gate'
Assert-Match "if: github\.event_name == 'push' && startsWith\(github\.ref, 'refs/tags/v'\)" 'publication must be restricted to version-tag pushes'
Assert-Match '(?m)^\s*id-token: write\s*$' 'the publish job must request OIDC permission'
Assert-Match 'uses: NuGet/login@v1\s+with:\s+user: dmitriyzen' 'NuGet Trusted Publishing must use the dmitriyzen username'
Assert-Match 'dotnet nuget push "artifacts/release/KeelMatrix\.LogSchema\.\$version\.nupkg"' 'only the exact validated primary package may be pushed'
Assert-Match 'Create GitHub Release after publication' 'GitHub Release creation must follow publication'
Assert-Match 'gh release create' 'the workflow must create the GitHub Release'

if ($workflow -match '\$\{\{\s*secrets\.[^}]*NUGET[^}]*\}\}') {
    throw 'Release workflow contract failed: long-lived NuGet API-key secrets are not allowed.'
}

$publishIndex = $workflow.IndexOf('- name: Publish package and symbols', [StringComparison]::Ordinal)
$releaseIndex = $workflow.IndexOf('- name: Create GitHub Release after publication', [StringComparison]::Ordinal)
if ($publishIndex -lt 0 -or $releaseIndex -le $publishIndex) {
    throw 'Release workflow contract failed: GitHub Release creation must occur after package publication.'
}

Write-Host 'Release workflow contract passed.'
