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

    $absentStateEvent = @($capturedManifest.events | Where-Object method -eq 'AbsentState')
    Assert-That ($absentStateEvent.Count -eq 1 -and @($absentStateEvent[0].placeholders).Count -eq 0 -and @($absentStateEvent[0].structuredState).Count -eq 1 -and $absentStateEvent[0].structuredState[0].emittedName -eq 'customerId' -and $absentStateEvent[0].parameters[1].role -eq 'State') 'installed capture must preserve an ordinary parameter absent from the template as structured state'
    $methodOrderEvent = @($capturedManifest.events | Where-Object method -eq 'MethodOrder')
    Assert-That ($methodOrderEvent.Count -eq 1 -and ((@($methodOrderEvent[0].placeholders) | ForEach-Object name) -join ',') -eq 'Second,First' -and ((@($methodOrderEvent[0].structuredState) | ForEach-Object emittedName) -join ',') -eq 'First,Second') 'installed capture must keep template occurrences separate from method-ordered structured state'
    $repeatedEvent = @($capturedManifest.events | Where-Object method -eq 'RepeatedPlaceholder')
    Assert-That ($repeatedEvent.Count -eq 1 -and @($repeatedEvent[0].placeholders).Count -eq 2 -and @($repeatedEvent[0].structuredState).Count -eq 1) 'installed capture must retain repeated template occurrences without duplicating state properties'
    $casingEvent = @($capturedManifest.events | Where-Object method -eq 'PlaceholderCasing')
    Assert-That ($casingEvent.Count -eq 1 -and $casingEvent[0].structuredState[0].emittedName -eq 'DisplayValue') 'installed capture must preserve matched placeholder casing as the emitted property name'
    $removedPlaceholderEvent = @($capturedManifest.events | Where-Object method -eq 'PlaceholderRemoved')
    Assert-That ($removedPlaceholderEvent.Count -eq 1 -and @($removedPlaceholderEvent[0].placeholders).Count -eq 0 -and $removedPlaceholderEvent[0].structuredState[0].emittedName -eq 'value') 'installed capture must retain the state property when its template occurrence is removed'
    $multipleExceptionEvent = @($capturedManifest.events | Where-Object method -eq 'MultipleExceptions')
    Assert-That ($multipleExceptionEvent.Count -eq 1 -and $multipleExceptionEvent[0].parameters[1].role -eq 'Exception' -and $multipleExceptionEvent[0].parameters[2].role -eq 'State' -and $multipleExceptionEvent[0].structuredState[0].emittedName -eq 'second') 'installed capture must apply first-exception semantics'
    $multipleLoggerEvent = @($capturedManifest.events | Where-Object method -eq 'MultipleLoggers')
    Assert-That ($multipleLoggerEvent.Count -eq 1 -and $multipleLoggerEvent[0].loggerParameter -eq 'firstLogger' -and $multipleLoggerEvent[0].parameters[1].role -eq 'State') 'installed capture must apply first-logger semantics'
    $multipleLevelEvent = @($capturedManifest.events | Where-Object method -eq 'MultipleDynamicLevels')
    Assert-That ($multipleLevelEvent.Count -eq 1 -and $multipleLevelEvent[0].levelSource -eq 'Dynamic' -and $multipleLevelEvent[0].levelParameter -eq 'firstLevel' -and $multipleLevelEvent[0].parameters[2].role -eq 'State') 'installed capture must apply first-dynamic-level semantics'
    $specialTemplateEvent = @($capturedManifest.events | Where-Object method -eq 'SpecialExceptionInTemplate')
    Assert-That ($specialTemplateEvent.Count -eq 1 -and $specialTemplateEvent[0].parameters[1].role -eq 'Exception' -and $specialTemplateEvent[0].structuredState[0].emittedName -eq 'exception') 'installed capture must retain the generator-specific exception state when the special parameter is referenced'

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
        $targetMethod = 'Event'
        $targetEvents = @($tamperedManifest.events | Where-Object method -eq $targetMethod)
        Assert-That ($targetEvents.Count -eq 1) "the installed capture must contain the canonical $targetMethod identity"
        $targetEvent = $targetEvents[0]
        switch ($Mutation) {
            'reviewer-exact' {
                $targetEvent.parameterForms = @('Exception', 'ILogger')
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

    $formRows = @(
        [pscustomobject]@{ Type = 'Microsoft.Extensions.Logging.ILogger'; Expected = 'ILogger' },
        [pscustomobject]@{ Type = 'Microsoft.Extensions.Logging.LogLevel'; Expected = 'LogLevel' },
        [pscustomobject]@{ Type = 'System.Exception'; Expected = 'Exception' },
        [pscustomobject]@{ Type = 'DerivedProblem'; Expected = 'None' },
        [pscustomobject]@{ Type = 'OrdinaryProblem'; Expected = 'None' },
        [pscustomobject]@{ Type = 'string'; Expected = 'None' }
    )
    $requiredForms = @($formRows | ForEach-Object Expected)
    $allForms = @('None', 'Exception', 'ILogger', 'LogLevel')

    function Write-FormMatrixManifest {
        param(
            [Parameter(Mandatory = $true)][string[]]$Forms,
            [Parameter(Mandatory = $true)][string]$Destination
        )

        $matrixManifest = Get-Content -Raw -LiteralPath $baseline | ConvertFrom-Json
        $parameters = for ($index = 0; $index -lt $formRows.Count; $index++) {
            "None:$($Forms[$index]):$($formRows[$index].Type)"
        }
        $parameterRecords = @(
            [pscustomobject]@{ name = 'logger'; type = 'Microsoft.Extensions.Logging.ILogger'; refKind = 'None'; role = 'Logger' },
            [pscustomobject]@{ name = 'level'; type = 'Microsoft.Extensions.Logging.LogLevel'; refKind = 'None'; role = 'DynamicLevel' },
            [pscustomobject]@{ name = 'exception'; type = 'System.Exception'; refKind = 'None'; role = 'Exception' },
            [pscustomobject]@{ name = 'derived'; type = 'DerivedProblem'; refKind = 'None'; role = 'State' },
            [pscustomobject]@{ name = 'ordinary'; type = 'OrdinaryProblem'; refKind = 'None'; role = 'State' },
            [pscustomobject]@{ name = 'value'; type = 'string'; refKind = 'None'; role = 'State' }
        )
        $matrixManifest.events = @([pscustomobject]@{
                projectKey = [string]$matrixManifest.projects[0].key
                identity = 'Logging.FormMatrix`0(' + ($parameters -join ',') + ')'
                containingType = 'Logging'
                method = 'FormMatrix'
                genericArity = 0
                parameterRefKinds = @($formRows | ForEach-Object { 'None' })
                eventId = 1200
                eventName = 'FormMatrix'
                level = 'Dynamic'
                message = 'Form matrix'
                placeholders = @()
                parameterForms = @($Forms)
                parameters = $parameterRecords
                structuredState = @(
                    [pscustomobject]@{ parameterName = 'derived'; emittedName = 'derived' },
                    [pscustomobject]@{ parameterName = 'ordinary'; emittedName = 'ordinary' },
                    [pscustomobject]@{ parameterName = 'value'; emittedName = 'value' }
                )
                loggerParameter = 'logger'
                exceptionParameter = 'exception'
                levelSource = 'Dynamic'
                levelParameter = 'level'
                source = [pscustomobject]@{ file = 'CanonicalIdentityLogging.cs'; line = 1; kind = 'source' }
            })
        $matrixManifest.unsupported = @()
        $matrixManifest.analysisIssues = @()
        $matrixManifest.compilationDiagnosticKinds = @()
        $matrixManifest.workspaceDiagnosticKinds = @()
        $matrixManifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Destination -Encoding utf8NoBOM
    }

    for ($position = 0; $position -lt $formRows.Count; $position++) {
        foreach ($candidateForm in $allForms) {
            $candidateForms = @($requiredForms)
            $candidateForms[$position] = $candidateForm
            $rowLabel = "position-$position-$($formRows[$position].Type)-$candidateForm"
            $candidateManifest = Join-Path $freshCheckout "form-matrix-$position-$candidateForm.json"
            Write-FormMatrixManifest -Forms $candidateForms -Destination $candidateManifest

            $matrixCheck = Invoke-LocalTool -Label "local-manifest form matrix $rowLabel check" -Arguments @('check', $consumerProject, '--baseline', $candidateManifest, '--format', 'json', '--severity', 'all', '--accept', 'KMLOG001', '--accept', 'KMLOG002', '--no-telemetry')
            $matrixDiff = Invoke-LocalTool -Label "local-manifest form matrix $rowLabel symmetric diff" -Arguments @('diff', $candidateManifest, $candidateManifest, '--format', 'json', '--severity', 'all', '--no-telemetry')
            if ($candidateForm -eq $formRows[$position].Expected) {
                Assert-That ($matrixCheck.ExitCode -eq 0) "installed form matrix $rowLabel check must accept the reader-computed form"
                Assert-That ($matrixDiff.ExitCode -eq 0) "installed form matrix $rowLabel diff must accept the reader-computed form"
                $checkEnvelope = $matrixCheck.Output | ConvertFrom-Json
                $diffEnvelope = $matrixDiff.Output | ConvertFrom-Json
                Assert-That ($checkEnvelope.coverageComplete -eq $true -and $diffEnvelope.coverageComplete -eq $true) "installed form matrix $rowLabel must report complete coverage"
            }
            else {
                Assert-AnalysisErrorEnvelope -Result $matrixCheck -Label "installed form matrix $rowLabel check" -ForbiddenPath $freshCheckout
                Assert-AnalysisErrorEnvelope -Result $matrixDiff -Label "installed form matrix $rowLabel symmetric diff" -ForbiddenPath $freshCheckout
            }
        }
    }

    $typeAuthenticityMutations = @(
        [pscustomobject]@{ Name = 'escaped-system-exception'; Old = 'None:None:string'; New = 'None:None:System.@Exception' },
        [pscustomobject]@{ Name = 'escaped-loglevel'; Old = 'None:None:string'; New = 'None:None:Microsoft.Extensions.Logging.@LogLevel' },
        [pscustomobject]@{ Name = 'escaped-ilogger'; Old = 'None:None:string'; New = 'None:None:Microsoft.Extensions.Logging.@ILogger<Probe.Category>' },
        [pscustomobject]@{ Name = 'trivia-whitespace'; Old = 'None:None:string'; New = 'None:None:Microsoft.Extensions.Logging.ILogger <Probe.Category>' },
        [pscustomobject]@{ Name = 'wrong-ilogger-arity'; Old = 'None:ILogger:Microsoft.Extensions.Logging.ILogger'; New = 'None:ILogger:Microsoft.Extensions.Logging.ILogger<Probe.Category,Probe.Other>' },
        [pscustomobject]@{ Name = 'nested-generic-member'; Old = 'None:ILogger:Microsoft.Extensions.Logging.ILogger'; New = 'None:ILogger:Microsoft.Extensions.Logging.ILogger<Probe.Category>.Nested<Probe.Other>' }
    )
    foreach ($mutation in $typeAuthenticityMutations) {
        $tamperedManifest = Get-Content -Raw -LiteralPath $baseline | ConvertFrom-Json
        $targetEvent = @($tamperedManifest.events | Where-Object method -eq 'Event')
        Assert-That ($targetEvent.Count -eq 1) "the installed capture must contain the canonical Event identity for $($mutation.Name)"
        $targetEvent[0].identity = $targetEvent[0].identity.Replace($mutation.Old, $mutation.New, [StringComparison]::Ordinal)
        $tamperedBaseline = Join-Path $freshCheckout "tampered-type-$($mutation.Name).json"
        $tamperedManifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tamperedBaseline -Encoding utf8NoBOM
        $selfDiff = Invoke-LocalTool -Label "local-manifest $($mutation.Name) self-diff" -Arguments @('diff', $tamperedBaseline, $tamperedBaseline, '--format', 'json', '--severity', 'all', '--no-telemetry')
        Assert-AnalysisErrorEnvelope -Result $selfDiff -Label "installed $($mutation.Name) self-diff" -ForbiddenPath $freshCheckout
    }

    $comparisonErrorManifest = Join-Path $freshCheckout 'comparison-analysis-error.json'
    $comparisonError = Get-Content -Raw -LiteralPath $baseline | ConvertFrom-Json
    $comparisonError.analysisIssues = @([pscustomobject]@{
            projectKey = [string]$comparisonError.projects[0].key
            code = 'KMLOGP001'
            severity = 'error'
            message = 'Synthetic comparison analysis error.'
            declarationKey = ''
            sources = @()
        })
    $comparisonError | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $comparisonErrorManifest -Encoding utf8NoBOM
    $comparisonErrorCheck = Invoke-LocalTool -Label 'local-manifest comparison analysis-error check' -Arguments @('check', $consumerProject, '--baseline', $comparisonErrorManifest, '--format', 'json', '--severity', 'all', '--no-telemetry')
    Assert-AnalysisErrorEnvelope -Result $comparisonErrorCheck -Label 'installed comparison analysis-error check' -ForbiddenPath $freshCheckout
    Assert-That ($comparisonErrorCheck.Output -match 'KMLOGP001: Synthetic comparison analysis error') 'installed check must exercise the comparison-originated analysis error'
    $comparisonErrorDiff = Invoke-LocalTool -Label 'local-manifest comparison analysis-error diff' -Arguments @('diff', $baseline, $comparisonErrorManifest, '--format', 'json', '--severity', 'all', '--no-telemetry')
    Assert-AnalysisErrorEnvelope -Result $comparisonErrorDiff -Label 'installed comparison analysis-error diff' -ForbiddenPath $freshCheckout
    Assert-That ($comparisonErrorDiff.Output -match 'KMLOGP001: Synthetic comparison analysis error') 'installed diff must exercise the comparison-originated analysis error'

    $reviewerBaseline = Join-Path $freshCheckout 'tampered-reviewer-exact.json'
    $reviewerText = Invoke-LocalTool -Label 'local-manifest reviewer-exact text check' -Arguments @('check', $consumerProject, '--baseline', $reviewerBaseline, '--severity', 'all', '--no-telemetry')
    Assert-That ($reviewerText.ExitCode -eq 3 -and $reviewerText.Output -match '(?m)^ANALYSIS ERROR ') 'the reviewer-exact text check must return an analysis error and exit 3'
    Assert-That (-not $reviewerText.Output.Contains($freshCheckout, [StringComparison]::OrdinalIgnoreCase)) 'the reviewer-exact text check must not expose an absolute path'
    Assert-That ($reviewerText.Output -notmatch '(?m)^\s*at KeelMatrix\.') 'the reviewer-exact text check must not expose a stack trace'

    $derivedEvent = @($capturedManifest.events | Where-Object method -eq 'DerivedException')
    Assert-That ($derivedEvent.Count -eq 1 -and (@($derivedEvent[0].parameterForms) -join ',') -eq 'ILogger,None' -and ([string]$derivedEvent[0].identity).Contains(':None:DerivedProblem', [StringComparison]::Ordinal)) 'installed capture must support a non-suffix derived exception while recording its declared type with form None'
    Assert-That (@($derivedEvent[0].placeholders).Count -eq 0) 'installed capture must keep a derived exception out of message-template placeholder occurrences'
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
    $expectedDiagnostic = 'BREAKING KMLOG102 ConsumerProcessed changed structured-state property "OrderId" to "AccountId".'
    Assert-That ($mutated.ExitCode -eq 1 -and $mutated.Output.Contains($expectedDiagnostic)) "mutated check must contain the exact diagnostic: $expectedDiagnostic"

    $mutatedManifest = Join-Path $freshCheckout 'mutated.json'
    $mutatedCapture = Invoke-LocalTool -Label 'local-manifest mutated capture' -Arguments @('capture', $consumerProject, '--output', $mutatedManifest, '--no-telemetry')
    Assert-That ($mutatedCapture.ExitCode -eq 0 -and (Test-Path -LiteralPath $mutatedManifest)) 'installed mutated capture must write a second manifest'
    $mutatedDiff = Invoke-LocalTool -Label 'local-manifest breaking diff' -Arguments @('diff', $baseline, $mutatedManifest, '--no-telemetry')
    Assert-That ($mutatedDiff.ExitCode -eq 1 -and $mutatedDiff.Output.Contains($expectedDiagnostic)) 'installed diff must report the same breaking structured rename'

    $stateSourcePath = Join-Path $freshCheckout 'GeneratorStateLogging.cs'
    $stateSource = Get-Content -Raw -LiteralPath $stateSourcePath
    $stateSource = $stateSource.Replace('int customerId', 'int accountId')
    Set-Content -LiteralPath $stateSourcePath -Value $stateSource -Encoding utf8NoBOM
    $absentRename = Invoke-LocalTool -Label 'local-manifest absent-state rename check' -Arguments @('check', $consumerProject, '--baseline', $baseline, '--no-telemetry')
    Assert-That ($absentRename.ExitCode -eq 1 -and $absentRename.Output -match 'BREAKING KMLOG102 AbsentState changed structured-state property "customerId" to "accountId".') 'installed check must gate a rename of an unreferenced ordinary state parameter'
    $absentRenameManifest = Join-Path $freshCheckout 'absent-state-renamed.json'
    $absentRenameCapture = Invoke-LocalTool -Label 'local-manifest absent-state rename capture' -Arguments @('capture', $consumerProject, '--output', $absentRenameManifest, '--no-telemetry')
    Assert-That ($absentRenameCapture.ExitCode -eq 0) 'installed capture must write the absent-state rename manifest'
    $absentRenameDiff = Invoke-LocalTool -Label 'local-manifest absent-state rename diff' -Arguments @('diff', $baseline, $absentRenameManifest, '--no-telemetry')
    Assert-That ($absentRenameDiff.ExitCode -eq 1 -and $absentRenameDiff.Output -match 'KMLOG102') 'installed diff must report the absent-state rename'

    $stateSource = $stateSource.Replace('int accountId', 'int customerId')
    $stateSource = $stateSource.Replace('int first, int second', 'int second, int first')
    Set-Content -LiteralPath $stateSourcePath -Value $stateSource -Encoding utf8NoBOM
    $orderMutation = Invoke-LocalTool -Label 'local-manifest method-order check' -Arguments @('check', $consumerProject, '--baseline', $baseline, '--no-telemetry')
    Assert-That ($orderMutation.ExitCode -eq 1 -and $orderMutation.Output -match 'KMLOG103 MethodOrder changed structured-state property order.') 'installed check must detect method-order state changes when template text is unchanged'

    $stateSource = $stateSource.Replace('int second, int first', 'int first, int second')
    $stateSource = $stateSource.Replace('RoleFlipProblem : Exception', 'RoleFlipProblem')
    Set-Content -LiteralPath $stateSourcePath -Value $stateSource -Encoding utf8NoBOM
    $roleMutation = Invoke-LocalTool -Label 'local-manifest parameter-role check' -Arguments @('check', $consumerProject, '--baseline', $baseline, '--severity', 'all', '--no-telemetry')
    Assert-That ($roleMutation.ExitCode -eq 1 -and $roleMutation.Output -match 'KMLOG105') 'installed check must detect a semantic exception-to-state role change'

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
