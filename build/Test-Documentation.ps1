$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

$rootReadme = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'README.md')
$packageReadme = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'src/KeelMatrix.LogSchema/README.md')
foreach ($readme in @($rootReadme, $packageReadme)) {
    Assert-That ($readme -match '(?m)^dotnet new tool-manifest$') 'the recommended onboarding must create a local tool manifest'
    Assert-That ($readme -match '(?m)^dotnet tool install KeelMatrix\.LogSchema$') 'the recommended onboarding must install the local tool'
    Assert-That ($readme -match '(?m)^dotnet tool run logschema capture MyService\.csproj --output logschema\.json$') 'the recommended capture command must invoke the manifest-pinned tool'
    Assert-That ($readme -match '(?m)^dotnet tool run logschema check MyService\.csproj --baseline logschema\.json$') 'the recommended check command must invoke the manifest-pinned tool'
    Assert-That ($readme -match '(?m)^dotnet tool restore$') 'fresh-checkout instructions must restore the local tool manifest'
    Assert-That ($readme -match 'Commit `dotnet-tools\.json`') 'the supported SDK manifest path must be documented for source control'
    Assert-That ($readme -match '(?m)^### Global tool alternative$') 'the global installation path must be clearly separate'
    Assert-That ($readme -match '(?m)^dotnet tool install --global KeelMatrix\.LogSchema$') 'the global alternative must remain documented'
    Assert-That ($readme -match '\.NET 8 runtime' -and $readme -match '10\.0\.401') 'consumer runtime and verified project-loading SDK prerequisites must be explicit'
    Assert-That ($readme -match 'parses every declared type with Roslyn' -and $readme -match 'byte-identical to the canonical rendering' -and $readme -match 'wrong-arity' -and $readme -match 'derived exception') 'consumer documentation must describe canonical type authenticity and supported derived exceptions'
    Assert-That ($readme -match 'integrity' -and $readme -match 'placeholder' -and $readme -match 'level') 'consumer documentation must describe persisted manifest integrity and cross-field validation'
    Assert-That ($readme -match 'missing, malformed, or stale' -and $readme -match 'self-consistency check' -and $readme -match 'not a signature') 'consumer documentation must describe mandatory self-consistency integrity without claiming authenticity'
    Assert-That ($readme -notmatch '(?i)company integration|later package step|package step') 'product documentation must not contain obsolete internal integration or packaging-stage wording'
}

$phase0 = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'docs/PHASE0-FEASIBILITY.md')
Assert-That ($phase0 -match 'build/Test-ShippingMatrix\.ps1') 'semantic fixture documentation must identify the shipping implementation matrix'
Assert-That ($phase0 -match 'Success of the feasibility probe alone is not evidence') 'semantic fixture documentation must distinguish feasibility and shipping evidence'
Assert-That ($phase0 -match 'packaged type-by-form matrix' -and $phase0 -match 'symmetric forged-vs-forged') 'semantic fixture documentation must describe the packaged form matrix'
Assert-That ($phase0 -notmatch '(?i)candidate SHA|independent re-verification|specification section|contains no shipping CLI') 'semantic fixture documentation must not retain obsolete milestone or review material'

$security = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'SECURITY.md')
Assert-That ($security -match 'supports the `0\.1\.x` release line' -and $security -match '0\.1\.0') 'supported security versions must name the supported release line'
Assert-That ($security -notmatch '(?i)intended first|before the first public package|after publication') 'security documentation must remain release-state-neutral'
Assert-That ($security -match 'product-owned network requests' -and $security -match 'MSBuild project evaluation' -and $security -match 'target projects') 'security documentation must distinguish LogSchema network behavior from target-project evaluation'
Assert-That ($security -match 'recomputes every parameter form\s+solely from the canonical declared type' -and $security -match 'does not trust a manifest to assert arbitrary type-hierarchy semantics' -and $security -match 'integrity digest' -and $security -match 'self-consistency check' -and $security -match 'not a signature') 'security documentation must describe the reader-computed integrity boundary'
Assert-That ($security -notmatch 'current supported release line is v1') 'supported security versions must not use the obsolete v1 line'

$diagnosticCodes = @(1..11 | ForEach-Object { 'KMLOGP{0:D3}' -f $_ })
$diagnostics = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'COMPATIBILITY-RULES.md')
foreach ($code in $diagnosticCodes) {
    Assert-That ($diagnostics -match [regex]::Escape($code)) "the diagnostic reference must document $code"
}
Assert-That ($diagnostics -match 'additive, subtractive, or substituted difference' -and $diagnostics -match 'analysisErrors' -and $diagnostics -match 'integrity' -and $diagnostics -match 'unknown JSON fields' -and $diagnostics -match 'not a signature') 'the diagnostic reference must document canonical identity and manifest-integrity validation failure behavior'
Assert-That ($diagnostics -match 'comparison-time analysis errors' -and $diagnostics -match 'coverageComplete.*false') 'the diagnostic reference must document the comparison analysis-error coverage envelope'

$completeFamilySurfaces = @(
    'README.md',
    'src/KeelMatrix.LogSchema/README.md',
    'MANIFEST.md',
    'docs/PHASE0-FEASIBILITY.md',
    'src/KeelMatrix.LogSchema.Core/CommandRunner.cs'
)
foreach ($relativePath in $completeFamilySurfaces) {
    $surface = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot $relativePath)
    foreach ($code in $diagnosticCodes) {
        Assert-That ($surface -match [regex]::Escape($code)) "$relativePath must name the complete diagnostic family, including $code"
    }
}

$rangeStart = [regex]::Escape($diagnosticCodes[0])
$staleRangeEnd = [regex]::Escape(('KMLOGP{0:D3}' -f 8))
$staleRangePattern = "$rangeStart\s*(?:-|–|—|to|through)\s*$staleRangeEnd"
$trackedFiles = @(git -C $repositoryRoot ls-files)
Assert-That ($LASTEXITCODE -eq 0) 'git must enumerate tracked files for the diagnostic-family sweep'
foreach ($relativePath in $trackedFiles) {
    if ($relativePath -eq 'icon.png' -or [IO.Path]::GetExtension($relativePath).ToLowerInvariant() -in @('.png', '.jpg', '.jpeg', '.gif', '.ico', '.dll', '.pdb', '.nupkg', '.snupkg')) {
        continue
    }

    $absolutePath = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
        continue
    }
    $bytes = [IO.File]::ReadAllBytes($absolutePath)
    if (@($bytes | Where-Object { $_ -eq 0 }).Count -gt 0) {
        continue
    }
    $text = [Text.Encoding]::UTF8.GetString($bytes)
    Assert-That ($text -notmatch $staleRangePattern) "$relativePath contains a stale diagnostic-family upper bound"
}

$privacy = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'PRIVACY.md')
Assert-That ($privacy -match 'contains no telemetry client' -and $privacy -match 'makes no product-owned network requests') 'privacy claims must match the shipping implementation'
$manifest = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'MANIFEST.md')
Assert-That ($manifest -match 'contains no telemetry client' -and $manifest -match '10\.0\.401') 'manifest privacy and SDK claims must match the shipping implementation'
Assert-That ($manifest -match '<ref-kind>:<semantic-form>:<canonical-type>' -and $manifest -match 'parses it with Roslyn' -and $manifest -match 'byte-for-byte equality' -and $manifest -match 'recomputes the required form vector' -and $manifest -match 'additive, subtractive, or substituted') 'the manifest contract must document reader-computed canonical parameter forms'
foreach ($row in @(
        'exactly `Microsoft.Extensions.Logging.ILogger` with arity 0.*`ILogger`',
        'exactly top-level `Microsoft.Extensions.Logging.ILogger<T>` with arity 1.*`ILogger`',
        'exactly top-level `Microsoft.Extensions.Logging.LogLevel` with arity 0.*`LogLevel`',
        'exactly top-level `System.Exception` with arity 0.*`Exception`',
        'every other top-level canonical type.*`None`'
    )) {
    Assert-That ($manifest -match $row) "the manifest contract must contain decision-table row: $row"
}
Assert-That ($manifest -match 'V1 does not assert semantic base-type classification' -and $manifest -match 'source-semantic analysis to assign the generator-effective parameter role' -and $manifest -match 'SHA-256' -and $manifest -match 'placeholder occurrences' -and $manifest -match 'Status' -and $manifest -match 'Trusted-as-authored, non-contractual' -and $manifest -match 'Unknown extra fields') 'the manifest contract must distinguish declared-type forms from source-derived generator roles and persisted integrity'
Assert-That ($manifest -match 'comparison-time analysis error' -and $manifest -match 'coverageComplete: false') 'the manifest contract must document the comparison analysis-error envelope'
$dependencies = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'docs/DEPENDENCIES.md')
Assert-That ($dependencies -match 'nuspec intentionally exposes no external package dependencies' -and $dependencies -match '10\.0\.401') 'dependency documentation must describe the bundled shipping graph and verified SDK'

$gitignore = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot '.gitignore')
foreach ($entry in @('**/TestResults/', '**/coverage/', '.nuget/packages/', '.DS_Store', 'Thumbs.db', '*.local.json', '!dotnet-tools.json', '!.config/dotnet-tools.json')) {
    Assert-That ($gitignore -match [regex]::Escape($entry)) ".gitignore must contain $entry"
}

$ci = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot '.github/workflows/ci.yml')
foreach ($runner in @('ubuntu-latest', 'windows-latest', 'macos-latest')) {
    Assert-That ($ci -match [regex]::Escape($runner)) "CI must run the installed-tool and shipping gates on $runner"
}
Assert-That ($ci -match '\./build/validate\.ps1') 'CI must run the repository validation gate on every matrix leg'

$resourceDocs = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'docs/PROJECT-ANALYSIS-RESOURCES.md')
Assert-That ($resourceDocs -match '24 SDK-style' -and $resourceDocs -match '384 source documents' -and $resourceDocs -match '1,536 declarations') 'project-analysis resource documentation must identify the reproducible large fixture'
Assert-That ($resourceDocs -match '64 C# projects' -and $resourceDocs -match '512 project documents' -and $resourceDocs -match '4,096 source documents') 'project-analysis resource documentation must state the product graph ceilings'
Assert-That ($resourceDocs -match '120 seconds' -and $resourceDocs -match '1,536 MiB' -and $resourceDocs -match '1,024 MiB') 'project-analysis resource documentation must state the measured CI budget'
$validation = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'build/validate.ps1')
$hasResourceBudget = $validation -match 'MaxElapsedSeconds 120' -or $validation -match "'-MaxElapsedSeconds', '120'"
Assert-That ($validation -match 'Test-ProjectAnalysisResources\.ps1' -and $hasResourceBudget -and $validation -match 'Manifest integrity mutation matrix' -and $validation -match 'ManifestIntegrityTests') 'release validation must run the project-analysis resource and manifest-integrity gates'

Write-Host 'Documentation and repository-consistency regression tests passed.'
