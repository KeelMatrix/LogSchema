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
    Assert-That ($readme -notmatch '(?i)company integration|later package step|package step') 'product documentation must not contain obsolete internal integration or packaging-stage wording'
}

$phase0 = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'docs/PHASE0-FEASIBILITY.md')
Assert-That ($phase0 -match 'build/Test-ShippingMatrix\.ps1') 'semantic fixture documentation must identify the shipping implementation matrix'
Assert-That ($phase0 -match 'Success of the feasibility probe alone is not evidence') 'semantic fixture documentation must distinguish feasibility and shipping evidence'
Assert-That ($phase0 -notmatch '(?i)candidate SHA|independent re-verification|specification section|contains no shipping CLI') 'semantic fixture documentation must not retain obsolete milestone or review material'

$security = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'SECURITY.md')
Assert-That ($security -match '0\.1\.x' -and $security -match '0\.1\.0') 'supported security versions must name the intended first release line'
Assert-That ($security -notmatch 'current supported release line is v1') 'supported security versions must not use the obsolete v1 line'

$diagnostics = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'COMPATIBILITY-RULES.md')
foreach ($number in 1..7) {
    $code = 'KMLOGP{0:D3}' -f $number
    Assert-That ($diagnostics -match [regex]::Escape($code)) "the diagnostic reference must document $code"
}
Assert-That ($diagnostics -match 'additive, subtractive, or substituted difference' -and $diagnostics -match 'analysisErrors') 'the diagnostic reference must document canonical identity validation failure behavior'

$privacy = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'PRIVACY.md')
Assert-That ($privacy -match 'contains no telemetry client' -and $privacy -match 'makes no network requests') 'privacy claims must match the shipping implementation'
$manifest = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'MANIFEST.md')
Assert-That ($manifest -match 'contains no telemetry client' -and $manifest -match '10\.0\.401') 'manifest privacy and SDK claims must match the shipping implementation'
Assert-That ($manifest -match 'parameterRefKinds.*same count and values' -and $manifest -match 'parameterForms.*positional' -and $manifest -match 'additive as well as subtractive') 'the manifest contract must document exact canonical identity tuple validation'
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

Write-Host 'Documentation and repository-consistency regression tests passed.'
