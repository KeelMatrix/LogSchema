param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$probe = Join-Path $repositoryRoot "phase0/bin/$Configuration/net10.0/Phase0.LogSchemaProbe.dll"
$artifactRoot = Join-Path $repositoryRoot 'artifacts/matrix-test'

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Invoke-Probe {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$TargetFramework
    )

    $output = Join-Path $artifactRoot "$Name.json"
    & dotnet $probe $Project --output $output --tfm $TargetFramework
    if ($LASTEXITCODE -ne 0) {
        throw "Probe failed for $Name with exit code $LASTEXITCODE"
    }

    return Get-Content -Raw $output | ConvertFrom-Json
}

function Get-RecordLocations {
    param([Parameter(Mandatory = $true)][object[]]$Records)

    return @($Records | ForEach-Object { "$($_.source.file):$($_.source.line)" })
}

Assert-That (Test-Path -LiteralPath $probe) "build the Phase 0 probe before running this matrix test"
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

$fixtures = @(
    @{ Name = 'net8'; Project = 'fixtures/Phase0.Net8/Phase0.Net8.csproj'; TargetFramework = 'net8.0'; Namespace = 'Phase0.Net8' },
    @{ Name = 'stable'; Project = 'fixtures/Phase0.Stable/Phase0.Stable.csproj'; TargetFramework = 'net10.0'; Namespace = 'Phase0.Stable' },
    @{ Name = 'multi-net8'; Project = 'fixtures/Phase0.Multi/Phase0.Multi.csproj'; TargetFramework = 'net8.0'; Namespace = 'Phase0.Multi' },
    @{ Name = 'multi-net10'; Project = 'fixtures/Phase0.Multi/Phase0.Multi.csproj'; TargetFramework = 'net10.0'; Namespace = 'Phase0.Multi' }
)

foreach ($fixture in $fixtures) {
    $manifest = Invoke-Probe -Name $fixture.Name -Project (Join-Path $repositoryRoot $fixture.Project) -TargetFramework $fixture.TargetFramework
    $projectDirectory = Join-Path $repositoryRoot (Split-Path -Parent $fixture.Project)
    $rawOccurrences = @(Get-ChildItem -LiteralPath $projectDirectory -Filter '*.cs' -File | Select-String -Pattern '\[LoggerMessage').Count
    $sourceRecords = @($manifest.events + $manifest.unsupported | Where-Object { $_.source.kind -eq 'source' })
    $eventLocations = Get-RecordLocations -Records @($manifest.events | Where-Object { $_.source.kind -eq 'source' })
    $sourceLocations = Get-RecordLocations -Records $sourceRecords
    $replicas = @($manifest.generatedImplementationReplicas)

    Assert-That ($rawOccurrences -eq 18) "$($fixture.Name): expected 18 committed LoggerMessage occurrences, found $rawOccurrences"
    Assert-That ($sourceRecords.Count -eq $rawOccurrences) "$($fixture.Name): every committed LoggerMessage occurrence must be extracted or reported unsupported"
    Assert-That ((@($sourceLocations | Select-Object -Unique).Count) -eq $sourceLocations.Count) "$($fixture.Name): a source occurrence was classified more than once"
    Assert-That ($manifest.events.Count -eq 15) "$($fixture.Name): expected 15 extracted events, found $($manifest.events.Count)"
    Assert-That ($manifest.unsupported.Count -eq 3) "$($fixture.Name): expected 3 unsupported declarations, found $($manifest.unsupported.Count)"

    $generatedPartial = @($manifest.events | Where-Object { $_.method -eq 'GeneratedPartial' })
    Assert-That ($generatedPartial.Count -eq 1) "$($fixture.Name): GeneratedPartial must be emitted exactly once"
    Assert-That ($generatedPartial[0].eventId -eq 1101) "$($fixture.Name): GeneratedPartial EventId changed"
    Assert-That ($generatedPartial[0].source.file -eq 'Generated.Logging.g.cs' -and $generatedPartial[0].source.line -eq 7 -and $generatedPartial[0].source.kind -eq 'source') "$($fixture.Name): project .g.cs declaration lost source provenance"
    Assert-That (@($replicas | Where-Object { $_.pairedSource.file -eq 'Generated.Logging.g.cs' }).Count -eq 1) "$($fixture.Name): compiler implementation was not paired to GeneratedPartial"
    Assert-That (@($replicas | Where-Object { $_.source.file -eq 'Generated.Logging.g.cs' }).Count -eq 0) "$($fixture.Name): project .g.cs declaration was misclassified as generated"

    $constantField = @($manifest.events | Where-Object { $_.method -eq 'ConstantArguments' })
    $constantExpression = @($manifest.events | Where-Object { $_.method -eq 'ConstantExpressionEventName' })
    $skipEnabledCheck = @($manifest.events | Where-Object { $_.method -eq 'SkipEnabledCheck' })
    $allNamed = @($manifest.events | Where-Object { $_.method -eq 'AllNamedArguments' })
    $nested = @($manifest.events | Where-Object { $_.method -eq 'NestedEvent' })
    $instance = @($manifest.events | Where-Object { $_.method -eq 'InstanceEvent' })
    $duplicateIds = @($manifest.events | Where-Object { $_.method -like 'DuplicateEventId*' })
    $generic = @($manifest.unsupported | Where-Object { $_.declaration -match 'GenericEvent' })
    $noParameters = @($manifest.unsupported | Where-Object { $_.declaration -match 'NoParameters' })

    Assert-That ($constantField.Count -eq 1 -and $constantField[0].eventName -eq 'OrderCreated') "$($fixture.Name): const field EventName was not read"
    Assert-That ($constantExpression.Count -eq 1 -and $constantExpression[0].eventName -eq 'ConstantExpression') "$($fixture.Name): constant-expression EventName was not read"
    Assert-That ($skipEnabledCheck.Count -eq 1 -and $skipEnabledCheck[0].eventId -eq 1009) "$($fixture.Name): SkipEnabledCheck declaration was omitted"
    Assert-That ($allNamed.Count -eq 1 -and $allNamed[0].eventName -eq 'AllNamedArguments') "$($fixture.Name): all-named attribute arguments were not read"
    Assert-That ($nested.Count -eq 1 -and $nested[0].containingType -eq "$($fixture.Namespace).PartialOuter.NestedLogging") "$($fixture.Name): nested partial declaration was omitted"
    Assert-That ($instance.Count -eq 1) "$($fixture.Name): non-static containing class declaration was omitted"
    Assert-That ($duplicateIds.Count -eq 2 -and (@($duplicateIds | Where-Object { $_.eventId -eq 1011 }).Count -eq 2)) "$($fixture.Name): duplicate EventId declarations were not both retained"
    Assert-That ($generic.Count -eq 1 -and $generic[0].reason -match 'generic') "$($fixture.Name): generic declaration was neither extracted nor reported"
    Assert-That ($noParameters.Count -eq 1 -and $noParameters[0].reason -match 'ILogger') "$($fixture.Name): no-parameter declaration was neither extracted nor reported"

    $replicaLocations = @($replicas | ForEach-Object { "$($_.pairedSource.file):$($_.pairedSource.line)" })
    Assert-That ($replicas.Count -eq $manifest.events.Count) "$($fixture.Name): each extracted declaration should have one compiler replica"
    Assert-That (((@($replicaLocations | Sort-Object) -join "`n") -eq (@($eventLocations | Sort-Object) -join "`n"))) "$($fixture.Name): compiler replicas are not a one-to-one pairing with extracted source declarations"
    Assert-That (@($replicas | Where-Object { $_.source.kind -ne 'generated' -or $_.source.line -ne 0 -or $_.pairedSource.kind -ne 'source' }).Count -eq 0) "$($fixture.Name): generated replica provenance is not canonical"
}

$program = Get-Content -Raw (Join-Path $repositoryRoot 'phase0/Program.cs')
Assert-That ($program -notmatch 'Assembly\.Load|Process\.Start|GetEntryAssembly|LoadFrom') 'the probe must not load or invoke target assemblies'
Assert-That ($program -match 'OpenProjectAsync' -and $program -match 'GetCompilationAsync') 'the probe must use semantic project loading'
Assert-That ($program -match '\["DesignTimeBuild"\]\s*=\s*"true"' -and $program -match '\["BuildingProject"\]\s*=\s*"false"') 'the probe must use non-building workspace options'
foreach ($fixture in $fixtures | Select-Object -Unique -Property Namespace) {
    $sentinel = Get-Content -Raw (Join-Path $repositoryRoot "fixtures/$($fixture.Namespace)/ExecutionSentinel.cs")
    Assert-That ($sentinel -match '\[ModuleInitializer\]') "$($fixture.Namespace): execution sentinel must be reachable through a module initializer"
}

Write-Output 'Phase 0 matrix regression test passed.'
