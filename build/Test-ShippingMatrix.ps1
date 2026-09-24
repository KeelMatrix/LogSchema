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
    Assert-That (@($manifest.events).Count -eq 15) "$Name must capture all 15 supported declarations"
    Assert-That (@($manifest.unsupported).Count -eq 3) "$Name must retain all three unsupported declarations"
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

    Write-Host "$Name shipping manifest: events=$(@($manifest.events).Count) unsupported=$(@($manifest.unsupported).Count) sha256=$firstHash"
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
Assert-RootIndependentManifest

$pairingOutput = Join-Path $outputRoot 'pairing.json'
$pairing = Invoke-ShippingTool -Arguments @('capture', (Join-Path $RepositoryRoot 'fixtures/Phase0.Pairing/Phase0.Pairing.csproj'), '--tfm', 'net8.0', '--output', $pairingOutput, '--no-telemetry')
Assert-That ($pairing.ExitCode -eq 3) 'ambiguous shipping pairing must fail closed with exit 3'
Assert-That ($pairing.Output -match 'KMLOGP001') 'ambiguous shipping pairing must report KMLOGP001'
Assert-That ($pairing.Output -match 'KMLOGP005') 'untrustworthy pairing compilation must report KMLOGP005'
Assert-That ($pairing.Output -match 'generated LoggerMessage declaration has no project-source counterpart') 'unpaired generated declaration must remain explicit'
Assert-That (-not (Test-Path -LiteralPath $pairingOutput)) 'failed ambiguous shipping capture must not write a baseline'

Write-Host 'Shipping semantic fixture matrix passed.'
