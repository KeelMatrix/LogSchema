param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$PackageId,
    [Parameter(Mandatory = $true)][string]$PackageVersion,
    [Parameter(Mandatory = $true)][string]$ConsumerFixture,
    [Parameter(Mandatory = $true)][string]$WorkRoot
)

$ErrorActionPreference = 'Stop'

$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$ConsumerFixture = [IO.Path]::GetFullPath($ConsumerFixture)
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)

foreach ($file in @($PackagePath, (Join-Path $ConsumerFixture 'PackageConsumerFixture.csproj'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required local-tool smoke input was not found: $file"
    }
}

if (Test-Path -LiteralPath $WorkRoot) {
    Remove-Item -LiteralPath $WorkRoot -Recurse -Force
}

$feed = Join-Path $WorkRoot 'feed'
$manifestAuthor = Join-Path $WorkRoot 'manifest-author'
$freshCheckout = Join-Path $WorkRoot 'fresh-checkout'
$isolatedHome = Join-Path $WorkRoot 'isolated-home'
$authorPackages = Join-Path $WorkRoot 'author-packages'
$authorHttpCache = Join-Path $WorkRoot 'author-http-cache'
$freshPackages = Join-Path $WorkRoot 'fresh-packages'
$freshHttpCache = Join-Path $WorkRoot 'fresh-http-cache'
foreach ($path in @($feed, $manifestAuthor, $freshCheckout, $isolatedHome, $authorPackages, $authorHttpCache, $freshPackages, $freshHttpCache)) {
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}
Copy-Item -LiteralPath $PackagePath -Destination $feed

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Invoke-LocalTool {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $global:LASTEXITCODE = 0
    $output = (& dotnet tool run logschema @Arguments 2>&1 | Out-String).TrimEnd()
    $exitCode = $LASTEXITCODE
    Write-Host "--- $Label ---"
    Write-Host ("Input: dotnet tool run logschema {0}" -f ($Arguments -join ' '))
    Write-Host "Exit code: $exitCode"
    if ($output.Length -gt 0) {
        Write-Host $output
    }
    return [pscustomobject]@{ Label = $Label; ExitCode = $exitCode; Output = $output }
}

function Assert-AnalysisErrorEnvelope {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$ForbiddenPath
    )

    Assert-That ($Result.ExitCode -eq 3) "$Label must return exit code 3"
    try {
        $envelope = $Result.Output | ConvertFrom-Json
    }
    catch {
        throw "Assertion failed: $Label must return a JSON envelope"
    }
    Assert-That (@($envelope.toolErrors).Count -eq 0) "$Label must not report an invocation error"
    Assert-That (@($envelope.analysisErrors).Count -gt 0) "$Label must report an analysis error"
    Assert-That (@($envelope.findings).Count -eq 0) "$Label must not report compatibility findings"
    Assert-That ($envelope.coverageComplete -eq $false) "$Label must report incomplete coverage"
    Assert-That (-not $Result.Output.Contains($ForbiddenPath, [StringComparison]::OrdinalIgnoreCase)) "$Label must not expose an absolute path"
    Assert-That ($Result.Output -notmatch '(?m)^\s*at KeelMatrix\.') "$Label must not expose a stack trace"
}

$escapedFeed = [System.Security.SecurityElement]::Escape($feed)
$config = Join-Path $WorkRoot 'NuGet.config'
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="$PackageId" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $config -Encoding utf8NoBOM

$savedEnvironment = @{
    DOTNET_CLI_HOME = $env:DOTNET_CLI_HOME
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH
    DOTNET_CLI_TELEMETRY_OPTOUT = $env:DOTNET_CLI_TELEMETRY_OPTOUT
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
    DOTNET_NOLOGO = $env:DOTNET_NOLOGO
    HOME = $env:HOME
    USERPROFILE = $env:USERPROFILE
    NUGET_PACKAGES = $env:NUGET_PACKAGES
    NUGET_HTTP_CACHE_PATH = $env:NUGET_HTTP_CACHE_PATH
}
$oldLocation = Get-Location
try {
    $env:DOTNET_CLI_HOME = $isolatedHome
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:HOME = $isolatedHome
    $env:USERPROFILE = $isolatedHome
    $env:NUGET_PACKAGES = $authorPackages
    $env:NUGET_HTTP_CACHE_PATH = $authorHttpCache

    Set-Location $manifestAuthor
    & dotnet new tool-manifest | Out-Host
    Assert-That ($LASTEXITCODE -eq 0) 'dotnet new tool-manifest must succeed'
    & dotnet tool install $PackageId --configfile $config --no-cache --ignore-failed-sources | Out-Host
    Assert-That ($LASTEXITCODE -eq 0) 'local manifest tool installation must succeed from the isolated feed'

    $manifestCandidates = @(@('dotnet-tools.json', '.config/dotnet-tools.json') | ForEach-Object { Join-Path $manifestAuthor $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    Assert-That ($manifestCandidates.Count -eq 1) 'local tool installation must create exactly one supported dotnet-tools.json manifest'
    $manifestPath = $manifestCandidates[0]
    $manifestRelativePath = [IO.Path]::GetRelativePath($manifestAuthor, $manifestPath)
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    $toolProperty = @($manifest.tools.PSObject.Properties | Where-Object Name -eq $PackageId.ToLowerInvariant())
    Assert-That ($toolProperty.Count -eq 1) 'the local manifest must pin the package under its canonical lowercase id'
    Assert-That ([string]$toolProperty[0].Value.version -eq $PackageVersion) "the local manifest must pin package version $PackageVersion"
    Assert-That ((@($toolProperty[0].Value.commands) -join ',') -eq 'logschema') 'the local manifest must pin the logschema command'

    $freshManifestPath = Join-Path $freshCheckout $manifestRelativePath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $freshManifestPath) | Out-Null
    Copy-Item -LiteralPath $manifestPath -Destination $freshManifestPath
    Copy-Item -LiteralPath (Join-Path $ConsumerFixture 'PackageConsumerFixture.csproj') -Destination $freshCheckout
    Get-ChildItem -LiteralPath $ConsumerFixture -Filter '*.cs' -File | Copy-Item -Destination $freshCheckout

    Assert-That (@(Get-ChildItem -LiteralPath $freshPackages -Force).Count -eq 0) 'the fresh-checkout package cache must start empty'
    Assert-That (@(Get-ChildItem -LiteralPath $freshHttpCache -Force).Count -eq 0) 'the fresh-checkout HTTP cache must start empty'
    $env:NUGET_PACKAGES = $freshPackages
    $env:NUGET_HTTP_CACHE_PATH = $freshHttpCache

    Set-Location $freshCheckout
    $globalTools = (& dotnet tool list --global 2>&1 | Out-String)
    Assert-That ($LASTEXITCODE -eq 0) 'isolated global-tool inventory must be readable'
    Assert-That ($globalTools -notmatch '(?im)^KeelMatrix\.LogSchema\s') 'the isolated CLI home must have no global LogSchema installation'

    & dotnet tool restore --configfile $config --no-cache --ignore-failed-sources | Out-Host
    Assert-That ($LASTEXITCODE -eq 0) 'dotnet tool restore must restore the manifest-pinned package in the fresh checkout'
    $localTools = (& dotnet tool list --local 2>&1 | Out-String)
    Assert-That ($LASTEXITCODE -eq 0) 'local tool inventory must be readable after restore'
    $localToolPattern = '(?im)^' + [regex]::Escape($PackageId) + '\s+' + [regex]::Escape($PackageVersion) + '\s+logschema'
    Assert-That ($localTools -match $localToolPattern) 'the fresh checkout must resolve the manifest-pinned local LogSchema tool'

    $consumerProject = Join-Path $freshCheckout 'PackageConsumerFixture.csproj'
    & dotnet restore $consumerProject --configfile $config --no-cache --nologo | Out-Host
    Assert-That ($LASTEXITCODE -eq 0) 'consumer fixture restore must succeed from controlled sources'

    $help = Invoke-LocalTool -Label 'local-manifest help' -Arguments @('--', '--help')
    Assert-That ($help.ExitCode -eq 0 -and $help.Output -match 'logschema capture' -and $help.Output -match 'Usage:') 'manifest-pinned help must identify the logschema command'

    $baseline = Join-Path $freshCheckout 'logschema.json'
    $capture = Invoke-LocalTool -Label 'local-manifest capture' -Arguments @('capture', $consumerProject, '--output', $baseline, '--no-telemetry')
    Assert-That ($capture.ExitCode -eq 0 -and (Test-Path -LiteralPath $baseline)) 'manifest-pinned capture must return exit 0 and write a manifest'
    $capturedManifest = Get-Content -Raw -LiteralPath $baseline | ConvertFrom-Json
    $omittedEvent = @($capturedManifest.events | Where-Object method -eq 'OmittedEventId')
    Assert-That ($omittedEvent.Count -eq 1 -and $omittedEvent[0].eventId -ne 0 -and $omittedEvent[0].eventName -eq 'OmittedEventId' -and $omittedEvent[0].level -eq 'Information') 'installed capture must record generator-derived EventId, default EventName, and fixed level'
    $dynamicEvent = @($capturedManifest.events | Where-Object method -eq 'DynamicLevel')
    Assert-That ($dynamicEvent.Count -eq 1 -and $dynamicEvent[0].level -eq 'Dynamic') 'installed capture must distinguish a dynamic LogLevel parameter from fixed None'
    $fixedNoneEvent = @($capturedManifest.events | Where-Object method -eq 'FixedNone')
    Assert-That ($fixedNoneEvent.Count -eq 1 -and $fixedNoneEvent[0].level -eq 'None') 'installed capture must preserve explicit LogLevel.None'

    $clean = Invoke-LocalTool -Label 'local-manifest clean check' -Arguments @('check', $consumerProject, '--baseline', $baseline, '--no-telemetry')
    Assert-That ($clean.ExitCode -eq 0 -and $clean.Output -match 'LogSchema: no gated incompatibilities found\.') 'manifest-pinned clean check must return exit 0'

    $cleanDiff = Invoke-LocalTool -Label 'local-manifest clean diff' -Arguments @('diff', $baseline, $baseline, '--no-telemetry')
    Assert-That ($cleanDiff.ExitCode -eq 0 -and $cleanDiff.Output -match 'LogSchema: no gated incompatibilities found\.') 'manifest-pinned clean diff must return exit 0'

    function Write-IdentityMutation {
        param(
            [Parameter(Mandatory = $true)][string]$Mutation,
            [Parameter(Mandatory = $true)][string]$Destination
        )

        $tamperedManifest = Get-Content -Raw -LiteralPath $baseline | ConvertFrom-Json
        $targetMethod = if ($Mutation -in @('derived-as-none', 'derived-coordinated-none')) { 'DerivedException' } else { 'Event' }
        $targetEvents = @($tamperedManifest.events | Where-Object method -eq $targetMethod)
        Assert-That ($targetEvents.Count -eq 1) "the installed capture must contain the canonical $targetMethod identity"
        $targetEvent = $targetEvents[0]
        switch ($Mutation) {
            'reviewer-exact' {
                $targetEvent.parameterForms = @('Exception', 'ILogger')
            }
            'string-as-exception' {
                $forms = @($targetEvent.parameterForms)
                $forms[1] = 'Exception'
                $targetEvent.parameterForms = $forms
            }
            'derived-as-none' {
                $forms = @($targetEvent.parameterForms)
                $forms[1] = 'None'
                $targetEvent.parameterForms = $forms
            }
            'derived-coordinated-none' {
                $targetEvent.identity = ([string]$targetEvent.identity).Replace(':Exception:DerivedProblem', ':None:DerivedProblem')
                $forms = @($targetEvent.parameterForms)
                $forms[1] = 'None'
                $targetEvent.parameterForms = $forms
            }
            'wrong-containing-type' { $targetEvent.containingType = 'Totally.Wrong.Type' }
            'wrong-method' { $targetEvent.method = 'WrongMethod' }
            'wrong-generic-arity' { $targetEvent.genericArity = 99 }
            'wrong-parameter-count' { $targetEvent.parameterRefKinds = @('None') }
            'wrong-ref-kind' { $targetEvent.parameterRefKinds = @('None', 'Ref', 'None', 'None') }
            'unknown-ref-kind' { $targetEvent.parameterRefKinds = @('None', 'UnknownRefKind', 'None', 'None') }
            'wrong-parameter-form' { $targetEvent.parameterForms = @('Exception') }
            'unknown-parameter-form' { $targetEvent.parameterForms = @('UnknownParameterForm') }
            'additive-containing-type' { $targetEvent.containingType = $targetEvent.containingType + '.Extra' }
            'additive-method' { $targetEvent.method = $targetEvent.method + 'Extra' }
            'additive-generic-arity' { $targetEvent.genericArity = $targetEvent.genericArity + 1 }
            'additive-parameter-count' { $targetEvent.parameterRefKinds = @($targetEvent.parameterRefKinds) + 'None' }
            'additive-ref-kind' { $targetEvent.parameterRefKinds = @($targetEvent.parameterRefKinds) + 'Ref' }
            'additive-form-exception' { $targetEvent.parameterForms = @($targetEvent.parameterForms) + 'Exception' }
            'additive-form-ilogger' { $targetEvent.parameterForms = @($targetEvent.parameterForms) + 'ILogger' }
            'additive-form-loglevel' { $targetEvent.parameterForms = @($targetEvent.parameterForms) + 'LogLevel' }
            'additive-form-none' { $targetEvent.parameterForms = @($targetEvent.parameterForms) + 'None' }
            default { throw "Unknown identity mutation: $Mutation" }
        }
        $tamperedManifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Destination -Encoding utf8NoBOM
    }

    $identityMutations = @(
        'reviewer-exact',
        'wrong-containing-type',
        'wrong-method',
        'wrong-generic-arity',
        'wrong-parameter-count',
        'wrong-ref-kind',
        'unknown-ref-kind',
        'wrong-parameter-form',
        'unknown-parameter-form',
        'additive-containing-type',
        'additive-method',
        'additive-generic-arity',
        'additive-parameter-count',
        'additive-ref-kind',
        'additive-form-exception',
        'additive-form-ilogger',
        'additive-form-loglevel',
        'additive-form-none'
    )
    foreach ($mutation in $identityMutations) {
        $tamperedBaseline = Join-Path $freshCheckout "tampered-$mutation.json"
        Write-IdentityMutation -Mutation $mutation -Destination $tamperedBaseline
        $tamperedCheck = Invoke-LocalTool -Label "local-manifest $mutation check" -Arguments @('check', $consumerProject, '--baseline', $tamperedBaseline, '--format', 'json', '--severity', 'all', '--no-telemetry')
        Assert-AnalysisErrorEnvelope -Result $tamperedCheck -Label "installed $mutation check" -ForbiddenPath $freshCheckout
        $tamperedDiff = Invoke-LocalTool -Label "local-manifest $mutation diff" -Arguments @('diff', $baseline, $tamperedBaseline, '--format', 'json', '--severity', 'all', '--no-telemetry')
        Assert-AnalysisErrorEnvelope -Result $tamperedDiff -Label "installed $mutation diff" -ForbiddenPath $freshCheckout
    }

    foreach ($mutation in @('string-as-exception', 'derived-as-none')) {
        $symmetricManifest = Join-Path $freshCheckout "symmetric-$mutation.json"
        Write-IdentityMutation -Mutation $mutation -Destination $symmetricManifest
        $symmetricDiff = Invoke-LocalTool -Label "local-manifest symmetric $mutation diff" -Arguments @('diff', $symmetricManifest, $symmetricManifest, '--format', 'json', '--severity', 'all', '--no-telemetry')
        Assert-AnalysisErrorEnvelope -Result $symmetricDiff -Label "installed symmetric $mutation diff" -ForbiddenPath $freshCheckout
    }

    $comparisonFormManifest = Join-Path $freshCheckout 'comparison-derived-coordinated-none.json'
    Write-IdentityMutation -Mutation 'derived-coordinated-none' -Destination $comparisonFormManifest
    $comparisonFormCheck = Invoke-LocalTool -Label 'local-manifest comparison-form check' -Arguments @('check', $consumerProject, '--baseline', $comparisonFormManifest, '--format', 'json', '--severity', 'all', '--no-telemetry')
    Assert-AnalysisErrorEnvelope -Result $comparisonFormCheck -Label 'installed comparison-form check' -ForbiddenPath $freshCheckout
    Assert-That ($comparisonFormCheck.Output -match 'compared canonical method identity') 'installed comparison-form check must exercise the comparison-originated analysis error'
    $comparisonFormDiff = Invoke-LocalTool -Label 'local-manifest comparison-form diff' -Arguments @('diff', $baseline, $comparisonFormManifest, '--format', 'json', '--severity', 'all', '--no-telemetry')
    Assert-AnalysisErrorEnvelope -Result $comparisonFormDiff -Label 'installed comparison-form diff' -ForbiddenPath $freshCheckout
    Assert-That ($comparisonFormDiff.Output -match 'compared canonical method identity') 'installed comparison-form diff must exercise the comparison-originated analysis error'

    $reviewerBaseline = Join-Path $freshCheckout 'tampered-reviewer-exact.json'
    $reviewerText = Invoke-LocalTool -Label 'local-manifest reviewer-exact text check' -Arguments @('check', $consumerProject, '--baseline', $reviewerBaseline, '--severity', 'all', '--no-telemetry')
    Assert-That ($reviewerText.ExitCode -eq 3 -and $reviewerText.Output -match '(?m)^ANALYSIS ERROR ') 'the reviewer-exact text check must return an analysis error and exit 3'
    Assert-That (-not $reviewerText.Output.Contains($freshCheckout, [StringComparison]::OrdinalIgnoreCase)) 'the reviewer-exact text check must not expose an absolute path'
    Assert-That ($reviewerText.Output -notmatch '(?m)^\s*at KeelMatrix\.') 'the reviewer-exact text check must not expose a stack trace'

    $derivedEvent = @($capturedManifest.events | Where-Object method -eq 'DerivedException')
    Assert-That ($derivedEvent.Count -eq 1 -and (@($derivedEvent[0].parameterForms) -join ',') -eq 'ILogger,Exception' -and ([string]$derivedEvent[0].identity).Contains(':Exception:DerivedProblem', [StringComparison]::Ordinal)) 'installed capture must preserve the canonical non-suffix derived-exception form'
    $exactExceptionEvent = @($capturedManifest.events | Where-Object method -eq 'ExactException')
    Assert-That ($exactExceptionEvent.Count -eq 1 -and (@($exactExceptionEvent[0].parameterForms) -join ',') -eq 'ILogger,Exception' -and ([string]$exactExceptionEvent[0].identity).Contains(':Exception:System.Exception', [StringComparison]::Ordinal)) 'installed capture must preserve the canonical exact System.Exception form'
    $ordinaryCustomEvent = @($capturedManifest.events | Where-Object method -eq 'OrdinaryCustom')
    Assert-That ($ordinaryCustomEvent.Count -eq 1 -and (@($ordinaryCustomEvent[0].parameterForms) -join ',') -eq 'ILogger,None' -and ([string]$ordinaryCustomEvent[0].identity).Contains(':None:OrdinaryProblem', [StringComparison]::Ordinal)) 'installed capture must preserve the canonical ordinary custom-type form'

    $incompleteBaseline = Join-Path $freshCheckout 'incomplete.json'
    $incompleteManifest = Get-Content -Raw -LiteralPath $baseline | ConvertFrom-Json
    $incompleteManifest.unsupported = @([pscustomobject]@{
            projectKey = [string]$incompleteManifest.projects[0].key
            source = [pscustomobject]@{ file = 'ConsumerLogging.cs'; line = 1; kind = 'source' }
            declaration = 'unsupported declaration'
            declarationKey = 'PackageConsumerFixture.Unsupported'
            reason = 'unsupported form'
        })
    $incompleteManifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $incompleteBaseline -Encoding utf8NoBOM
    $unsupportedCheck = Invoke-LocalTool -Label 'local-manifest unsupported check' -Arguments @('check', $consumerProject, '--baseline', $incompleteBaseline, '--format', 'json', '--no-telemetry')
    Assert-That ($unsupportedCheck.ExitCode -eq 3 -and $unsupportedCheck.Output -match 'KMLOGP007' -and $unsupportedCheck.Output -match 'PackageConsumerFixture\.Unsupported' -and $unsupportedCheck.Output -match 'unsupported form') 'installed check must gate and explain unsupported baseline coverage'
    $unsupportedDiff = Invoke-LocalTool -Label 'local-manifest unsupported diff' -Arguments @('diff', $baseline, $incompleteBaseline, '--format', 'json', '--no-telemetry')
    Assert-That ($unsupportedDiff.ExitCode -eq 3 -and $unsupportedDiff.Output -match 'KMLOGP007' -and $unsupportedDiff.Output -match 'PackageConsumerFixture\.Unsupported') 'installed diff must gate and explain unsupported coverage'

    $sourcePath = Join-Path $freshCheckout 'ConsumerLogging.cs'
    $source = Get-Content -Raw -LiteralPath $sourcePath
    $source = $source.Replace('Processed {OrderId}', 'Processed {AccountId}').Replace('int orderId', 'int accountId')
    Set-Content -LiteralPath $sourcePath -Value $source -Encoding utf8NoBOM
    $mutated = Invoke-LocalTool -Label 'local-manifest mutated check' -Arguments @('check', $consumerProject, '--baseline', $baseline, '--no-telemetry')
    $expectedDiagnostic = 'BREAKING KMLOG102 ConsumerProcessed changed structured property "OrderId" to "AccountId".'
    Assert-That ($mutated.ExitCode -eq 1 -and $mutated.Output.Contains($expectedDiagnostic)) "mutated check must contain the exact diagnostic: $expectedDiagnostic"

    $mutatedManifest = Join-Path $freshCheckout 'mutated.json'
    $mutatedCapture = Invoke-LocalTool -Label 'local-manifest mutated capture' -Arguments @('capture', $consumerProject, '--output', $mutatedManifest, '--no-telemetry')
    Assert-That ($mutatedCapture.ExitCode -eq 0 -and (Test-Path -LiteralPath $mutatedManifest)) 'installed mutated capture must write a second manifest'
    $mutatedDiff = Invoke-LocalTool -Label 'local-manifest breaking diff' -Arguments @('diff', $baseline, $mutatedManifest, '--no-telemetry')
    Assert-That ($mutatedDiff.ExitCode -eq 1 -and $mutatedDiff.Output.Contains($expectedDiagnostic)) 'installed diff must report the same breaking structured rename'

    $source = Get-Content -Raw -LiteralPath $sourcePath
    $source = $source.Replace('Processed {AccountId}', 'Processed {OrderId}').Replace('int accountId', 'int orderId').Replace('[LoggerMessage(Message = "Dynamic level {Value}")]', '[LoggerMessage(Level = LogLevel.None, Message = "Dynamic level {Value}")]')
    Set-Content -LiteralPath $sourcePath -Value $source -Encoding utf8NoBOM
    $levelMutation = Invoke-LocalTool -Label 'local-manifest dynamic-to-fixed check' -Arguments @('check', $consumerProject, '--baseline', $baseline, '--severity', 'warning', '--no-telemetry')
    Assert-That ($levelMutation.ExitCode -eq 1 -and $levelMutation.Output -match 'WARNING\s+KMLOG201 DynamicLevel changed level Dynamic -> None\.') 'installed check must report a dynamic-to-fixed level transition'

    $invalid = Invoke-LocalTool -Label 'local-manifest invalid configuration' -Arguments @('check', $consumerProject, '--baseline', $baseline, '--severity', 'invalid', '--no-telemetry')
    Assert-That ($invalid.ExitCode -eq 2 -and $invalid.Output -match '--severity must be breaking, warning, or all\.') 'installed invalid configuration must return exit 2 with accepted values'
    $global:LASTEXITCODE = 0

    Write-Host "Local manifest: $manifestPath"
    Write-Host "Fresh package cache: $freshPackages"
    Write-Host 'Isolated local-tool onboarding and installed capture/check/diff smoke passed.'
}
finally {
    Set-Location $oldLocation
    foreach ($name in $savedEnvironment.Keys) {
        if ($null -eq $savedEnvironment[$name]) {
            Remove-Item -Path "Env:$name" -ErrorAction SilentlyContinue
        }
        else {
            Set-Item -Path "Env:$name" -Value $savedEnvironment[$name]
        }
    }
}
