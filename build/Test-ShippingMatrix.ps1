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

if (-not (Test-Path -LiteralPath $ToolAssembly -PathType Leaf)) {
    throw "Shipping tool assembly was not found: $ToolAssembly"
}

$outputRoot = Join-Path $RepositoryRoot 'artifacts/shipping-matrix'
if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Invoke-ShippingTool {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $global:LASTEXITCODE = 0
    $output = (& dotnet $ToolAssembly @Arguments 2>&1 | Out-String).TrimEnd()
    $exitCode = $LASTEXITCODE
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Assert-OrdinaryFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$TargetFramework,
        [Parameter(Mandatory = $true)][string]$Namespace
    )

    $fixtureDirectory = Join-Path $outputRoot $Name
    New-Item -ItemType Directory -Force -Path $fixtureDirectory | Out-Null
    $firstPath = Join-Path $fixtureDirectory 'first.json'
    $secondPath = Join-Path $fixtureDirectory 'second.json'
    $projectPath = Join-Path $RepositoryRoot $Project

    $first = Invoke-ShippingTool -Arguments @('capture', $projectPath, '--tfm', $TargetFramework, '--output', $firstPath, '--no-telemetry')
    Assert-That ($first.ExitCode -eq 0) "$Name first shipping capture must return exit 0. $($first.Output)"
    Assert-That (Test-Path -LiteralPath $firstPath) "$Name first shipping capture must write a manifest"

    $second = Invoke-ShippingTool -Arguments @('capture', $projectPath, '--tfm', $TargetFramework, '--output', $secondPath, '--no-telemetry')
    Assert-That ($second.ExitCode -eq 0) "$Name second shipping capture must return exit 0. $($second.Output)"
    Assert-That (Test-Path -LiteralPath $secondPath) "$Name second shipping capture must write a manifest"

    $firstHash = (Get-FileHash -LiteralPath $firstPath -Algorithm SHA256).Hash.ToUpperInvariant()
    $secondHash = (Get-FileHash -LiteralPath $secondPath -Algorithm SHA256).Hash.ToUpperInvariant()
    Assert-That ($firstHash -eq $secondHash) "$Name repeated shipping captures must be byte-identical"

    $manifest = Get-Content -Raw -LiteralPath $firstPath | ConvertFrom-Json
    Assert-That (@($manifest.projects).Count -eq 1) "$Name must contain exactly one project"
    Assert-That ($manifest.projects[0].key -eq "$Namespace|$TargetFramework") "$Name project key must include the selected target framework"
    Assert-That (@($manifest.events).Count -eq 14) "$Name must capture all 14 supported declarations"
    Assert-That (@($manifest.unsupported).Count -eq 4) "$Name must retain all four unsupported declarations, including generator-diagnostic declarations"
    Assert-That (@($manifest.analysisIssues | Where-Object severity -eq 'error').Count -eq 0) "$Name must not contain analysis errors"
    Assert-That ($first.Output -match 'Coverage: incomplete') "$Name output must identify incomplete unsupported coverage"

    $events = @{}
    foreach ($event in $manifest.events) {
        $events[[string]$event.method] = $event
        Assert-That (-not ([string]$event.source.file).Contains('\')) "$Name source paths must use forward slashes"
        Assert-That (-not [IO.Path]::IsPathRooted([string]$event.source.file)) "$Name source paths must remain project-relative"
    }

    Assert-That ($events.ConstantArguments.eventId -eq 1001 -and $events.ConstantArguments.eventName -eq 'OrderCreated' -and $events.ConstantArguments.level -eq 'Warning') "$Name constant attribute values must be effective"
    Assert-That ((@($events.ConstantArguments.placeholders.name) -join ',') -eq 'OrderId,CustomerName') "$Name message-template occurrence order must be preserved"
    Assert-That ($events.ConstructorArguments.eventId -eq 1002 -and @($events.ConstructorArguments.parameterForms) -contains 'Exception') "$Name constructor arguments and exception form must be represented"
    Assert-That ($events.DefaultEventName.eventName -eq 'DefaultEventName') "$Name omitted EventName must use the method name"
    Assert-That ($events.ExplicitEventName.eventName -eq 'ExplicitEventName') "$Name explicit EventName must be preserved"
    Assert-That ($events.FormatSpecifier.message -eq 'Escaped {{literal}} and {Value:000}') "$Name escaped placeholders and format specifiers must be preserved"
    Assert-That ((@($events.UnicodeTemplate.placeholders.name) -join ',') -eq 'Идентификатор') "$Name Unicode placeholders must be preserved"
    Assert-That ($events.NestedEvent.containingType -eq "$Namespace.PartialOuter.NestedLogging") "$Name nested partial type identity must be preserved"
    Assert-That ($events.GeneratedPartial.source.kind -eq 'source') "$Name project-source generated-style declaration must retain source provenance"
    Assert-That (@($manifest.unsupported | Where-Object { $_.declaration -match 'InstanceEvent' -and $_.reason -match 'SYSLIB1009' }).Count -eq 1) "$Name generator-diagnostic declaration must remain unsupported with its run-result diagnostic"
    Assert-That (@($manifest.compilationDiagnosticKinds | Where-Object { $_ -eq 'SYSLIB1015:Warning' }).Count -eq 1) "$Name benign SYSLIB1015 must be classified and retained without making the declaration unsupported"
    Write-Host "$Name shipping manifest: events=$(@($manifest.events).Count) unsupported=$(@($manifest.unsupported).Count) sha256=$firstHash"
}

function Assert-GeneratorStateFixture {
    $fixtureDirectory = Join-Path $outputRoot 'generator-state'
    New-Item -ItemType Directory -Force -Path $fixtureDirectory | Out-Null
    $manifestPath = Join-Path $fixtureDirectory 'manifest.json'
    $projectPath = Join-Path $RepositoryRoot 'tests/PackageConsumerFixture/PackageConsumerFixture.csproj'
    $capture = Invoke-ShippingTool -Arguments @('capture', $projectPath, '--tfm', 'net8.0', '--output', $manifestPath, '--no-telemetry')
    Assert-That ($capture.ExitCode -eq 0) "generator-state shipping capture must return exit 0. $($capture.Output)"
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    Assert-That (@($manifest.events).Count -eq 23) 'generator-state shipping capture must contain all 23 supported declarations'
    Assert-That (@($manifest.unsupported).Count -eq 0) 'generator-state shipping capture must not omit supported generator-state declarations'
    $events = @{}
    foreach ($event in @($manifest.events)) { $events[[string]$event.method] = $event }
    Assert-That ($events.FixedLevelParameterAbsentFromTemplate.levelSource -eq 'Fixed' -and $events.FixedLevelParameterAbsentFromTemplate.levelParameter -eq 'level' -and $events.FixedLevelParameterAbsentFromTemplate.parameters[1].role -eq 'LogLevel' -and @($events.FixedLevelParameterAbsentFromTemplate.structuredState).Count -eq 1 -and $events.FixedLevelParameterAbsentFromTemplate.structuredState[0].emittedName -eq 'level') 'shipping extractor must mirror fixed-level first LogLevel state when the placeholder is absent'
    Assert-That ($events.FixedLevelParameter.levelSource -eq 'Fixed' -and $events.FixedLevelParameter.parameters[1].role -eq 'LogLevel' -and $events.FixedLevelParameter.structuredState[0].emittedName -eq 'level') 'shipping extractor must mirror fixed-level first LogLevel state when the placeholder is referenced'
    Assert-That ($events.MultipleFixedLevels.levelSource -eq 'Fixed' -and $events.MultipleFixedLevels.parameters[1].role -eq 'LogLevel' -and $events.MultipleFixedLevels.parameters[2].role -eq 'State' -and ((@($events.MultipleFixedLevels.structuredState) | ForEach-Object emittedName) -join ',') -eq 'firstLevel,laterLevel') 'shipping extractor must mirror fixed-level first and later LogLevel state semantics'
    Assert-That ($events.MultipleDynamicLevels.levelSource -eq 'Dynamic' -and $events.MultipleDynamicLevels.levelParameter -eq 'firstLevel' -and $events.MultipleDynamicLevels.parameters[1].role -eq 'LogLevel' -and $events.MultipleDynamicLevels.parameters[2].role -eq 'State' -and $events.MultipleDynamicLevels.structuredState[0].emittedName -eq 'laterLevel') 'shipping extractor must mirror first-dynamic-level and later-level state semantics'
    Assert-That ($events.CustomLoggerFirst.loggerParameter -eq 'first' -and $events.CustomLoggerFirst.parameters[0].role -eq 'Logger' -and $events.CustomLoggerFirst.parameters[1].role -eq 'State' -and $events.CustomLoggerFirst.structuredState[0].emittedName -eq 'second') 'shipping extractor must mirror implicit-reference first-logger semantics'
    Assert-That ($events.OverlappingSpecialRoles.parameters[0].role -eq 'Logger|Exception' -and $events.OverlappingSpecialRoles.loggerParameter -eq 'value' -and $events.OverlappingSpecialRoles.exceptionParameter -eq 'value' -and @($events.OverlappingSpecialRoles.structuredState).Count -eq 0) 'shipping extractor must preserve overlapping logger and exception roles without silently selecting one'
    Assert-That ($events.SpecialExceptionInTemplate.parameters[1].role -eq 'Exception' -and $events.SpecialExceptionInTemplate.structuredState[0].emittedName -eq 'exception') 'shipping extractor must mirror referenced first-exception state behavior'
    Write-Host "Generator-state shipping manifest: events=$(@($manifest.events).Count)"
}

function Assert-RootIndependentManifest {
    $sourceFixture = Join-Path $RepositoryRoot 'tests/PackageConsumerFixture'
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('logschema-manifest-roots-' + [guid]::NewGuid().ToString('N'))
    $firstRoot = Join-Path $tempRoot 'first'
    $secondRoot = Join-Path $tempRoot 'second'
    try {
        foreach ($root in @($firstRoot, $secondRoot)) {
            New-Item -ItemType Directory -Force -Path $root | Out-Null
            Copy-Item -LiteralPath (Join-Path $sourceFixture 'PackageConsumerFixture.csproj') -Destination $root
            Get-ChildItem -LiteralPath $sourceFixture -Filter '*.cs' -File | Copy-Item -Destination $root

            & dotnet restore (Join-Path $root 'PackageConsumerFixture.csproj') --configfile (Join-Path $RepositoryRoot 'NuGet.config') --nologo | Out-Host
            Assert-That ($LASTEXITCODE -eq 0) "root-independent fixture restore failed for $root"
        }

        $firstManifest = Join-Path $firstRoot 'logschema.json'
        $secondManifest = Join-Path $secondRoot 'logschema.json'
        foreach ($pair in @(
                @($firstRoot, $firstManifest),
                @($secondRoot, $secondManifest))) {
            $capture = Invoke-ShippingTool -Arguments @('capture', (Join-Path $pair[0] 'PackageConsumerFixture.csproj'), '--output', $pair[1], '--no-telemetry')
            Assert-That ($capture.ExitCode -eq 0) "root-independent fixture capture failed for $($pair[0]): $($capture.Output)"
        }

        $firstHash = (Get-FileHash -LiteralPath $firstManifest -Algorithm SHA256).Hash.ToUpperInvariant()
        $secondHash = (Get-FileHash -LiteralPath $secondManifest -Algorithm SHA256).Hash.ToUpperInvariant()
        Assert-That ($firstHash -eq $secondHash) 'equivalent checkouts must produce byte-identical manifests'

        $manifest = Get-Content -Raw -LiteralPath $firstManifest | ConvertFrom-Json
        foreach ($event in @($manifest.events)) {
            Assert-That ([string]$event.source.file -match '^project/') 'project source provenance must use the project namespace'
            Assert-That (-not [IO.Path]::IsPathRooted([string]$event.source.file)) 'root-independent provenance must not be absolute'
            Assert-That (-not ([string]$event.source.file).Contains($firstRoot, [StringComparison]::OrdinalIgnoreCase)) 'root-independent provenance must not contain the first checkout root'
            Assert-That (-not ([string]$event.source.file).Contains($secondRoot, [StringComparison]::OrdinalIgnoreCase)) 'root-independent provenance must not contain the second checkout root'
        }

        Write-Host "Root-independent manifest: sha256=$firstHash"
    }
    finally {
        if (Test-Path -LiteralPath $tempRoot) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
    }
}

$generatorProject = Join-Path $RepositoryRoot 'fixtures/Phase0.GeneratedDeclarationGenerator/Phase0.GeneratedDeclarationGenerator.csproj'
& dotnet build $generatorProject -c Debug --no-restore --nologo | Out-Host
Assert-That ($LASTEXITCODE -eq 0) 'the generated-declaration fixture must build before shipping pairing validation'

Assert-OrdinaryFixture -Name 'net8' -Project 'fixtures/Phase0.Net8/Phase0.Net8.csproj' -TargetFramework 'net8.0' -Namespace 'Phase0.Net8'
Assert-OrdinaryFixture -Name 'stable' -Project 'fixtures/Phase0.Stable/Phase0.Stable.csproj' -TargetFramework 'net10.0' -Namespace 'Phase0.Stable'
Assert-OrdinaryFixture -Name 'multi-net8' -Project 'fixtures/Phase0.Multi/Phase0.Multi.csproj' -TargetFramework 'net8.0' -Namespace 'Phase0.Multi'
Assert-OrdinaryFixture -Name 'multi-net10' -Project 'fixtures/Phase0.Multi/Phase0.Multi.csproj' -TargetFramework 'net10.0' -Namespace 'Phase0.Multi'
Assert-GeneratorStateFixture
Assert-RootIndependentManifest

$currentStableOutput = Join-Path $outputRoot 'current-stable.json'
$currentStable = Invoke-ShippingTool -Arguments @('capture', (Join-Path $RepositoryRoot 'fixtures/Phase0.CurrentStable/Phase0.CurrentStable.csproj'), '--tfm', 'net10.0', '--output', $currentStableOutput, '--no-telemetry')
Assert-That ($currentStable.ExitCode -eq 0) "current stable generator capture must return exit 0. $($currentStable.Output)"
$currentStableManifest = Get-Content -Raw -LiteralPath $currentStableOutput | ConvertFrom-Json
Assert-That (@($currentStableManifest.events).Count -eq 1 -and @($currentStableManifest.unsupported).Count -eq 0) 'current stable generator line must produce one supported event without unsupported declarations'
Assert-That (@($currentStableManifest.analysisIssues | Where-Object severity -eq 'error').Count -eq 0) 'current stable generator line must not produce analysis errors'
Write-Host "Current stable generator manifest: generator=10.0.14.42308 events=$(@($currentStableManifest.events).Count)"

$rejected = Invoke-ShippingTool -Arguments @('capture', (Join-Path $RepositoryRoot 'fixtures/Phase0.Rejected/Phase0.Rejected.csproj'), '--tfm', 'net8.0', '--format', 'json', '--output', (Join-Path $outputRoot 'rejected.json'), '--no-telemetry')
Assert-That ($rejected.ExitCode -eq 3) "generator-rejected declaration shapes must fail closed with exit 3. $($rejected.Output)"
$rejectedEnvelope = $rejected.Output | ConvertFrom-Json
Assert-That (@($rejectedEnvelope.analysisErrors | Where-Object { $_ -match 'KMLOGP006' }).Count -gt 0) 'all generator-rejected declaration shapes must fail closed through the zero-supported-event analysis family'
Assert-That (@($rejectedEnvelope.unsupported).Count -eq 5 -and @($rejectedEnvelope.unsupported | Where-Object reason -match 'ref kind|params').Count -eq 5) 'out, ref, in, ref readonly, and params declarations must be explicit unsupported records'
Write-Host "Generator-rejected declaration matrix: unsupported=$(@($rejectedEnvelope.unsupported).Count)"

$pairingOutput = Join-Path $outputRoot 'pairing.json'
$pairing = Invoke-ShippingTool -Arguments @('capture', (Join-Path $RepositoryRoot 'fixtures/Phase0.Pairing/Phase0.Pairing.csproj'), '--tfm', 'net8.0', '--output', $pairingOutput, '--no-telemetry')
Assert-That ($pairing.ExitCode -eq 3) 'ambiguous shipping pairing must fail closed with exit 3'
Assert-That ($pairing.Output -match 'KMLOGP001') 'ambiguous shipping pairing must report KMLOGP001'
Assert-That ($pairing.Output -match 'KMLOGP005') 'untrustworthy pairing compilation must report KMLOGP005'
Assert-That ($pairing.Output -match 'generated LoggerMessage declaration was not produced by the resolved Microsoft.Extensions.Logging.Generators assembly') 'custom-generator declaration must remain explicit and untrusted'
Assert-That (-not (Test-Path -LiteralPath $pairingOutput)) 'failed ambiguous shipping capture must not write a baseline'

Write-Host 'Shipping semantic fixture matrix passed.'
