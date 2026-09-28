$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Invoke-NestedPwsh.ps1')

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

function Get-RunBlocks {
    param([Parameter(Mandatory = $true)][string]$Path)

    $Lines = @(Get-Content -LiteralPath $Path)

    $blocks = @()
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        if ($Lines[$index].Trim() -ne 'run: |') {
            continue
        }

        $runIndent = $Lines[$index].Length - $Lines[$index].TrimStart().Length
        $body = [System.Collections.Generic.List[string]]::new()
        for ($bodyIndex = $index + 1; $bodyIndex -lt $Lines.Count; $bodyIndex++) {
            $line = $Lines[$bodyIndex]
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                $indent = $line.Length - $line.TrimStart().Length
                if ($indent -le $runIndent) {
                    break
                }
                [void]$body.Add($line)
            }
            else {
                [void]$body.Add($line)
            }
        }

        $bodyIndent = @($body | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Length - $_.TrimStart().Length } | Measure-Object -Minimum).Minimum
        if ($null -eq $bodyIndent) {
            continue
        }
        $normalized = @($body | ForEach-Object {
                if ($_.Length -ge $bodyIndent) { $_.Substring($bodyIndent) } else { $_ }
            })
        $blocks += ,($normalized -join "`n")
    }
    return $blocks
}

$releaseBodies = @(Get-RunBlocks -Path $workflowPath | Where-Object { $_ -match '(?m)^\s*gh\s+release\s+create\b' })
if ($releaseBodies.Count -ne 1) {
    throw "Release workflow contract failed: expected one executable gh release create run block, found $($releaseBodies.Count)."
}
$releaseBody = [string]$releaseBodies[0]
$commandHasExplicitRepository = $releaseBody -match '(?m)(--repo\s+|-R\s+)'
$environmentHasExplicitRepository = $workflow -match '(?m)^\s*GH_REPO:\s*\$\{\{\s*github\.repository\s*\}\}\s*$'
if (-not ($commandHasExplicitRepository -or $environmentHasExplicitRepository)) {
    throw 'Release workflow contract failed: the release command has no explicit repository target.'
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('logschema-release-workflow-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null
$oldLocation = Get-Location
$oldPath = $env:Path
$oldRepo = $env:GH_REPO
$oldToken = $env:GH_TOKEN
$oldProof = $env:GH_RELEASE_TEST_PROOF
try {
    $stubScript = Join-Path $temporaryRoot 'gh-stub.ps1'
    @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
$repo = $env:GH_REPO
$repoIndex = [Array]::IndexOf($Arguments, '--repo')
if ($repoIndex -ge 0 -and $repoIndex + 1 -lt $Arguments.Count) {
    $repo = $Arguments[$repoIndex + 1]
}
if ($repo -ne 'KeelMatrix/LogSchema') {
    exit 17
}
Set-Content -LiteralPath $env:GH_RELEASE_TEST_PROOF -Value ($Arguments -join "`n") -Encoding utf8NoBOM
exit 0
'@ | Set-Content -LiteralPath $stubScript -Encoding utf8NoBOM
    $runScript = Join-Path $temporaryRoot 'release.ps1'
    $normalizedBody = $releaseBody.Replace('${{ github.ref_name }}', 'v0.1.0', [StringComparison]::Ordinal).Replace('${{ needs.validate.outputs.release_version }}', '0.1.0', [StringComparison]::Ordinal).Replace('${{ github.repository }}', 'KeelMatrix/LogSchema', [StringComparison]::Ordinal)
    $stubPathLiteral = $stubScript.Replace("'", "''", [StringComparison]::Ordinal)
    $stubPrelude = "function gh { & '$stubPathLiteral' @args; exit `$LASTEXITCODE }`n"
    $normalizedBody = $stubPrelude + $normalizedBody
    $runnerFooter = "`nif (`$LASTEXITCODE -ne 0) { exit `$LASTEXITCODE }`n"
    $normalizedBody += $runnerFooter
    $normalizedBody | Set-Content -LiteralPath $runScript -Encoding utf8NoBOM
    $proofPath = Join-Path $temporaryRoot 'positive-proof.txt'
    $env:Path = "$temporaryRoot$([IO.Path]::PathSeparator)$oldPath"
    $env:GH_TOKEN = 'test-token'
    $env:GH_RELEASE_TEST_PROOF = $proofPath
    if ($environmentHasExplicitRepository) { $env:GH_REPO = 'KeelMatrix/LogSchema' } else { Remove-Item Env:GH_REPO -ErrorAction SilentlyContinue }
    Set-Location -LiteralPath $temporaryRoot
    Invoke-NestedPwsh -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $runScript)
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $proofPath)) {
        throw 'Release workflow contract failed: the no-checkout release command did not resolve the explicit repository target.'
    }
    Write-Host 'Release command no-checkout execution passed.'

    $negativeBody = $normalizedBody
    if ($commandHasExplicitRepository) {
        $negativeBody = [regex]::Replace($negativeBody, "(?m)^\s*(?:--repo\s+|-R\s+)[^\r\n]+\r?\n?", '')
    }
    $negativeBody += $runnerFooter
    $negativeScript = Join-Path $temporaryRoot 'release-negative.ps1'
    $negativeBody | Set-Content -LiteralPath $negativeScript -Encoding utf8NoBOM
    $negativeProof = Join-Path $temporaryRoot 'negative-proof.txt'
    $env:GH_RELEASE_TEST_PROOF = $negativeProof
    Remove-Item Env:GH_REPO -ErrorAction SilentlyContinue
    Invoke-NestedPwsh -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $negativeScript)
    if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $negativeProof)) {
        throw 'Release workflow contract failed: removing the explicit repository target was accepted.'
    }
    Write-Host 'Release command negative no-target control passed.'
}
finally {
    Set-Location $oldLocation
    $env:Path = $oldPath
    if ($null -eq $oldRepo) { Remove-Item Env:GH_REPO -ErrorAction SilentlyContinue } else { $env:GH_REPO = $oldRepo }
    if ($null -eq $oldToken) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } else { $env:GH_TOKEN = $oldToken }
    if ($null -eq $oldProof) { Remove-Item Env:GH_RELEASE_TEST_PROOF -ErrorAction SilentlyContinue } else { $env:GH_RELEASE_TEST_PROOF = $oldProof }
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}

Write-Host 'Release workflow contract passed.'
