$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Invoke-NestedPwsh.ps1')

$workflowPath = Join-Path $PSScriptRoot '../.github/workflows/release.yml'
if (-not (Test-Path -LiteralPath $workflowPath -PathType Leaf)) {
    throw "Release workflow not found: $workflowPath"
}

$workflowLines = [IO.File]::ReadAllLines((Resolve-Path -LiteralPath $workflowPath))
$workflow = $workflowLines -join "`n"

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if ($Text -notmatch $Pattern) {
        throw "Release workflow contract failed: $Message"
    }
}

Assert-Match $workflow '(?m)^\s*workflow_dispatch:\s*$' 'workflow_dispatch dry-run trigger is required'
Assert-Match $workflow '(?m)^\s*tags:\s*\r?\n\s*- ''v\*''\s*$' 'tag trigger is required'
Assert-Match $workflow 'Manual release runs are validation-only; dry_run must be true\.' 'manual execution must fail closed to validation only'
Assert-Match $workflow 'Test-ReleaseVersion\.ps1 -Version' 'the tag path must enforce the reusable changelog/version gate'
Assert-Match $workflow 'validate\.ps1 -ReleaseVersion' 'the release path must run the release-equivalent repository gate'
Assert-Match $workflow "if: github\.event_name == 'push' && startsWith\(github\.ref, 'refs/tags/v'\)" 'publication must be restricted to version-tag pushes'
Assert-Match $workflow '(?m)^\s*id-token: write\s*$' 'the publish job must request OIDC permission'
Assert-Match $workflow 'uses: NuGet/login@v1\s+with:\s+user: dmitriyzen' 'NuGet Trusted Publishing must use the dmitriyzen username'
Assert-Match $workflow 'dotnet nuget push "artifacts/release/KeelMatrix\.LogSchema\.\$version\.nupkg"' 'only the exact validated primary package may be pushed'
Assert-Match $workflow 'Create GitHub Release after publication' 'GitHub Release creation must follow publication'
Assert-Match $workflow 'gh release create' 'the workflow must create the GitHub Release'

if ($workflow -match '\$\{\{\s*secrets\.[^}]*NUGET[^}]*\}\}') {
    throw 'Release workflow contract failed: long-lived NuGet API-key secrets are not allowed.'
}

function Get-IndentedBlock {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$HeaderPattern
    )

    $start = -1
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        if ($Lines[$index] -match $HeaderPattern) {
            $start = $index
            break
        }
    }
    if ($start -lt 0) { return @() }

    $headerIndent = $Lines[$start].Length - $Lines[$start].TrimStart().Length
    $block = [System.Collections.Generic.List[string]]::new()
    [void]$block.Add($Lines[$start])
    for ($index = $start + 1; $index -lt $Lines.Count; $index++) {
        $line = $Lines[$index]
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $indent = $line.Length - $line.TrimStart().Length
            if ($indent -le $headerIndent) { break }
        }
        [void]$block.Add($line)
    }
    return $block.ToArray()
}

function Get-RunBodies {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string[]]$Lines)

    $bodies = @()
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        if ($Lines[$index].Trim() -ne 'run: |') { continue }
        $runIndent = $Lines[$index].Length - $Lines[$index].TrimStart().Length
        $body = [System.Collections.Generic.List[string]]::new()
        for ($bodyIndex = $index + 1; $bodyIndex -lt $Lines.Count; $bodyIndex++) {
            $line = $Lines[$bodyIndex]
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                $indent = $line.Length - $line.TrimStart().Length
                if ($indent -le $runIndent) { break }
            }
            [void]$body.Add($line)
        }
        $nonblank = @($body | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($nonblank.Count -eq 0) { continue }
        $bodyIndent = ($nonblank | ForEach-Object { $_.Length - $_.TrimStart().Length } | Measure-Object -Minimum).Minimum
        $normalized = @($body | ForEach-Object {
                if ($_.Length -ge $bodyIndent) { $_.Substring($bodyIndent) } else { $_ }
            })
        $bodies += ,($normalized -join "`n")
    }
    return $bodies
}

$publishBlock = @(Get-IndentedBlock -Lines $workflowLines -HeaderPattern '^\s{2}publish:\s*$')
if ($publishBlock.Count -eq 0) { throw 'Release workflow contract failed: publish job is missing.' }
$publishText = $publishBlock -join "`n"
$releaseStepBlock = @(Get-IndentedBlock -Lines $publishBlock -HeaderPattern '^\s*- name: Create GitHub Release after publication\s*$')
if ($releaseStepBlock.Count -eq 0) { throw 'Release workflow contract failed: publish release step is missing.' }
$releaseStepText = $releaseStepBlock -join "`n"
$releaseBodies = @(Get-RunBodies -Lines $releaseStepBlock | Where-Object { $_ -match '(?m)^\s*gh\s+release\s+create\b' })
if ($releaseBodies.Count -ne 1) {
    throw "Release workflow contract failed: expected one publish-job gh release create body, found $($releaseBodies.Count)."
}
$releaseBody = [string]$releaseBodies[0]

function Test-ScopedRepositoryTarget {
    param([Parameter(Mandatory = $true)][string]$PublishTextValue, [Parameter(Mandatory = $true)][string]$ReleaseStepTextValue, [Parameter(Mandatory = $true)][string]$ReleaseBodyValue)
    ($PublishTextValue -match '(?m)^\s*GH_REPO:\s*\$\{\{\s*github\.repository\s*\}\}\s*$' -or
        $ReleaseStepTextValue -match '(?m)^\s*GH_REPO:\s*\$\{\{\s*github\.repository\s*\}\}\s*$' -or
        $ReleaseBodyValue -match '(?m)(--repo\s+|-R\s+)')
}

if (-not (Test-ScopedRepositoryTarget $publishText $releaseStepText $releaseBody)) {
    throw 'Release workflow contract failed: publish job has no scoped explicit repository target.'
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('logschema-release-workflow-' + [Guid]::NewGuid().ToString('N'))
$oldLocation = Get-Location
$oldRepo = $env:GH_REPO
$oldToken = $env:GH_TOKEN
$oldProof = $env:GH_RELEASE_TEST_PROOF

function Invoke-ReleaseProbe {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Body,
        [Parameter(Mandatory = $true)][bool]$RepositoryInScope,
        [ValidateSet('valid', 'missing', 'misnamed', 'extra')][string]$Assets = 'valid',
        [Parameter(Mandatory = $true)][bool]$ShouldPass
    )

    $root = Join-Path $temporaryRoot $Name
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'artifacts/release') | Out-Null
    $packagePath = Join-Path $root 'artifacts/release/KeelMatrix.LogSchema.0.1.0.nupkg'
    $symbolsPath = Join-Path $root 'artifacts/release/KeelMatrix.LogSchema.0.1.0.snupkg'
    Set-Content -LiteralPath $packagePath -Value 'package' -Encoding utf8NoBOM
    Set-Content -LiteralPath $symbolsPath -Value 'symbols' -Encoding utf8NoBOM
    if ($Assets -eq 'missing') { Remove-Item -LiteralPath $symbolsPath }
    if ($Assets -eq 'misnamed') {
        Move-Item -LiteralPath $symbolsPath -Destination (Join-Path $root 'artifacts/release/KeelMatrix.LogSchema.0.1.0.symbols')
    }
    if ($Assets -eq 'extra') {
        Set-Content -LiteralPath (Join-Path $root 'artifacts/release/unexpected.txt') -Value 'extra' -Encoding utf8NoBOM
    }

    $stubScript = Join-Path $root 'gh-stub.ps1'
    @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
$repo = $env:GH_REPO
$repoIndex = [Array]::IndexOf($Arguments, '--repo')
if ($repoIndex -ge 0 -and $repoIndex + 1 -lt $Arguments.Count) { $repo = $Arguments[$repoIndex + 1] }
if ($repo -ne 'KeelMatrix/LogSchema') { exit 17 }
$createIndex = [Array]::IndexOf($Arguments, 'create')
if ($createIndex -lt 0 -or $createIndex + 1 -ge $Arguments.Count) { exit 18 }
$assetArguments = @()
foreach ($argument in $Arguments[($createIndex + 2)..($Arguments.Count - 1)]) {
    if ($argument -match '^--') { break }
    $assetArguments += $argument
}
$expectedAssets = @('artifacts/release/KeelMatrix.LogSchema.0.1.0.nupkg', 'artifacts/release/KeelMatrix.LogSchema.0.1.0.snupkg')
$actualAssetText = @($assetArguments | Sort-Object) -join "`n"
$expectedAssetText = @($expectedAssets | Sort-Object) -join "`n"
if ($actualAssetText -ne $expectedAssetText) { exit 19 }
$releaseFiles = @(Get-ChildItem -LiteralPath 'artifacts/release' -File | Select-Object -ExpandProperty Name | Sort-Object)
$expectedFileNames = @($expectedAssets | ForEach-Object { Split-Path -Leaf $_ } | Sort-Object)
if ((@($releaseFiles) -join "`n") -ne (@($expectedFileNames) -join "`n")) { exit 20 }
foreach ($asset in $assetArguments) { if (-not (Test-Path -LiteralPath $asset -PathType Leaf)) { exit 21 } }
Set-Content -LiteralPath $env:GH_RELEASE_TEST_PROOF -Value ($Arguments -join "`n") -Encoding utf8NoBOM
exit 0
'@ | Set-Content -LiteralPath $stubScript -Encoding utf8NoBOM

    $runScript = Join-Path $root 'release.ps1'
    $normalizedBody = $Body.Replace('${{ github.ref_name }}', 'v0.1.0', [StringComparison]::Ordinal).Replace('${{ needs.validate.outputs.release_version }}', '0.1.0', [StringComparison]::Ordinal).Replace('${{ github.repository }}', 'KeelMatrix/LogSchema', [StringComparison]::Ordinal)
    $stubPathLiteral = $stubScript.Replace("'", "''", [StringComparison]::Ordinal)
    $stubPrelude = "function gh { & '$stubPathLiteral' @args; exit `$LASTEXITCODE }`n"
    ($stubPrelude + $normalizedBody + "`nexit `$LASTEXITCODE`n") | Set-Content -LiteralPath $runScript -Encoding utf8NoBOM
    $proofPath = Join-Path $root 'proof.txt'
    $env:GH_RELEASE_TEST_PROOF = $proofPath
    if ($RepositoryInScope) { $env:GH_REPO = 'KeelMatrix/LogSchema' } else { Remove-Item Env:GH_REPO -ErrorAction SilentlyContinue }
    Set-Location -LiteralPath $root
    Invoke-NestedPwsh -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $runScript)
    $passed = ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $proofPath))
    if ($passed -ne $ShouldPass) { throw "Release workflow probe '$Name' had unexpected result (exit=$LASTEXITCODE, expected pass=$ShouldPass)." }
    Write-Host "Release workflow probe $Name passed (expected=$ShouldPass)."
}

try {
    New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null
    $repositoryInScope = $releaseStepText -match '(?m)^\s*GH_REPO:\s*\$\{\{\s*github\.repository\s*\}\}\s*$'
    Invoke-ReleaseProbe -Name 'positive' -Body $releaseBody -RepositoryInScope $repositoryInScope -ShouldPass $true
    Invoke-ReleaseProbe -Name 'missing-artifact' -Body $releaseBody -RepositoryInScope $repositoryInScope -Assets missing -ShouldPass $false
    Invoke-ReleaseProbe -Name 'misnamed-artifact' -Body $releaseBody -RepositoryInScope $repositoryInScope -Assets misnamed -ShouldPass $false
    Invoke-ReleaseProbe -Name 'extra-artifact' -Body $releaseBody -RepositoryInScope $repositoryInScope -Assets extra -ShouldPass $false

    $unreachableBody = $releaseBody.Replace('artifacts/release/KeelMatrix.LogSchema.$version.snupkg', 'artifacts/missing/KeelMatrix.LogSchema.$version.snupkg', [StringComparison]::Ordinal)
    if ($unreachableBody -eq $releaseBody) { throw 'Release workflow contract failed: artifact-path mutation did not match the publish command.' }
    Invoke-ReleaseProbe -Name 'unreachable-artifact-path' -Body $unreachableBody -RepositoryInScope $repositoryInScope -ShouldPass $false

    $missingScopeStep = $releaseStepText -replace '(?m)^\s*GH_REPO:.*\r?\n', ''
    $missingScopePublish = $publishText -replace '(?m)^\s*GH_REPO:.*\r?\n', ''
    if (Test-ScopedRepositoryTarget $missingScopePublish $missingScopeStep $releaseBody) {
        throw 'Release workflow contract failed: removing GH_REPO from the publish release step was accepted.'
    }
    Write-Host 'Publish-scope repository negative control passed.'

    if (Test-ScopedRepositoryTarget $missingScopePublish $missingScopeStep $releaseBody) {
        throw 'Release workflow contract failed: a repository target outside publish scope was accepted.'
    }
    Write-Host 'Moved-repository-scope negative control passed.'
}
finally {
    Set-Location $oldLocation
    if ($null -eq $oldRepo) { Remove-Item Env:GH_REPO -ErrorAction SilentlyContinue } else { $env:GH_REPO = $oldRepo }
    if ($null -eq $oldToken) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } else { $env:GH_TOKEN = $oldToken }
    if ($null -eq $oldProof) { Remove-Item Env:GH_RELEASE_TEST_PROOF -ErrorAction SilentlyContinue } else { $env:GH_RELEASE_TEST_PROOF = $oldProof }
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}

Write-Host 'Release workflow contract passed.'
