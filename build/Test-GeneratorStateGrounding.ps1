param(
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
else {
    $RepositoryRoot = (Resolve-Path $RepositoryRoot).Path
}

$project = Join-Path $RepositoryRoot 'tests/PackageConsumerFixture/PackageConsumerFixture.csproj'
$outputRoot = Join-Path ([IO.Path]::GetTempPath()) ('logschema-generator-grounding-' + [Guid]::NewGuid().ToString('N'))

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

try {
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    $buildOutput = (& dotnet build $project -c Release --no-restore --no-incremental --nologo `
        '--property:EmitCompilerGeneratedFiles=true' `
        "--property:CompilerGeneratedFilesOutputPath=$outputRoot" 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "The generator grounding fixture did not build. $buildOutput"
    }

    $generatedFiles = @(Get-ChildItem -LiteralPath $outputRoot -Recurse -Filter '*.g.cs' -File)
    Assert-That ($generatedFiles.Count -gt 0) 'the pinned LoggerMessage generator must emit inspectable source'
    $generated = ($generatedFiles | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }) -join [Environment]::NewLine
    Assert-That ($generated.Contains('GeneratedCodeAttribute("Microsoft.Extensions.Logging.Generators", "10.0.13.7005")', [StringComparison]::Ordinal)) 'the fixture must be grounded against generator assembly version 10.0.13.7005'

    $absentStart = $generated.IndexOf('AbsentState', [StringComparison]::Ordinal)
    $absentEnd = $generated.IndexOf('MethodOrder', [StringComparison]::Ordinal)
    Assert-That ($absentStart -ge 0 -and $absentEnd -gt $absentStart) 'generated output must contain the absent-state and method-order methods'
    $absentSection = $generated.Substring($absentStart, $absentEnd - $absentStart)
    Assert-That ($absentSection.Contains('"customerId"', [StringComparison]::Ordinal)) 'the generator must emit the unreferenced ordinary customerId state property'

    $orderStart = $generated.IndexOf('MethodOrder', [StringComparison]::Ordinal)
    $orderEnd = $generated.IndexOf('RepeatedPlaceholder', [StringComparison]::Ordinal)
    Assert-That ($orderStart -ge 0 -and $orderEnd -gt $orderStart) 'generated output must contain the method-order and repeated-placeholder methods'
    $orderSection = $generated.Substring($orderStart, $orderEnd - $orderStart)
    Assert-That ($orderSection.IndexOf('"First"', [StringComparison]::Ordinal) -lt $orderSection.IndexOf('"Second"', [StringComparison]::Ordinal)) 'the generator must enumerate state in method-parameter order, not occurrence order'

    $repeatedStart = $generated.IndexOf('RepeatedPlaceholder', [StringComparison]::Ordinal)
    $repeatedEnd = $generated.IndexOf('PlaceholderCasing', [StringComparison]::Ordinal)
    $repeatedSection = $generated.Substring($repeatedStart, $repeatedEnd - $repeatedStart)
    Assert-That (([regex]::Matches($repeatedSection, '"Value"')).Count -eq 1) 'the generator must emit one state property for a repeated placeholder'

    Assert-That ($generated.Contains('"Case {DisplayValue}"', [StringComparison]::Ordinal)) 'the generator must preserve matched placeholder casing'
    Assert-That ($generated.Contains('"second"', [StringComparison]::Ordinal)) 'the generator must emit a later exception candidate as ordinary state'
    Assert-That ($generated.Contains('"Later logger {laterLogger}"', [StringComparison]::Ordinal)) 'the generator must emit a later logger candidate as ordinary state'
    Assert-That ($generated.Contains('"laterLevel"', [StringComparison]::Ordinal)) 'the generator must emit a later dynamic-level candidate as ordinary state'
    Assert-That ($generated.Contains('SpecialExceptionInTemplate', [StringComparison]::Ordinal)) 'the generator must emit the special-exception template case'

    $fixedAbsentStart = $generated.IndexOf('FixedLevelParameterAbsentFromTemplate', [StringComparison]::Ordinal)
    $fixedAbsentEnd = $generated.IndexOf('CustomLoggerFirst', $fixedAbsentStart, [StringComparison]::Ordinal)
    Assert-That ($fixedAbsentStart -ge 0 -and $fixedAbsentEnd -gt $fixedAbsentStart) 'generated output must contain the fixed-level absent-template and custom-logger methods'
    $fixedAbsentSection = $generated.Substring($fixedAbsentStart, $fixedAbsentEnd - $fixedAbsentStart)
    Assert-That ($fixedAbsentSection.Contains('new __FixedLevelParameterAbsentFromTemplateStruct(level)', [StringComparison]::Ordinal)) 'a fixed-level first LogLevel parameter must be emitted as ordinary state even when absent from the template'

    $fixedMultipleStart = $generated.IndexOf('MultipleFixedLevels', [StringComparison]::Ordinal)
    $fixedMultipleEnd = $generated.IndexOf('FixedLevelParameterAbsentFromTemplate', $fixedMultipleStart, [StringComparison]::Ordinal)
    Assert-That ($fixedMultipleStart -ge 0 -and $fixedMultipleEnd -gt $fixedMultipleStart) 'generated output must contain the fixed-level multiple-LogLevel method'
    $fixedMultipleSection = $generated.Substring($fixedMultipleStart, $fixedMultipleEnd - $fixedMultipleStart)
    Assert-That ($fixedMultipleSection.Contains('new __MultipleFixedLevelsStruct(firstLevel, laterLevel)', [StringComparison]::Ordinal)) 'a fixed-level first LogLevel and later LogLevel candidate must both be emitted as state'

    $customLoggerStart = $generated.IndexOf('CustomLoggerFirst', [StringComparison]::Ordinal)
    $customLoggerEnd = $generated.IndexOf('OverlappingSpecialRoles', $customLoggerStart, [StringComparison]::Ordinal)
    Assert-That ($customLoggerStart -ge 0 -and $customLoggerEnd -gt $customLoggerStart) 'generated output must contain the custom-logger and overlapping-role methods'
    $customLoggerSection = $generated.Substring($customLoggerStart, $customLoggerEnd - $customLoggerStart)
    Assert-That ($customLoggerSection.Contains('LoggerMessage.Define<global::Microsoft.Extensions.Logging.ILogger>', [StringComparison]::Ordinal) -and $customLoggerSection.Contains('__CustomLoggerFirstCallback(first, second, null)', [StringComparison]::Ordinal)) 'the first custom ILogger implementation must be the logger and the later ILogger must be ordinary state'

    $overlapStart = $generated.IndexOf('OverlappingSpecialRoles', [StringComparison]::Ordinal)
    $overlapEnd = $generated.IndexOf('RoleFlip', $overlapStart, [StringComparison]::Ordinal)
    Assert-That ($overlapStart -ge 0 -and $overlapEnd -gt $overlapStart) 'generated output must contain the overlapping-role method'
    $overlapSection = $generated.Substring($overlapStart, $overlapEnd - $overlapStart)
    Assert-That ($overlapSection.Contains('__OverlappingSpecialRolesCallback(value, value)', [StringComparison]::Ordinal)) 'a parameter with logger and exception roles must preserve both generator roles'

    Write-Host 'Pinned Microsoft.Extensions.Logging generator state grounding passed.'
}
finally {
    if (Test-Path -LiteralPath $outputRoot) {
        Remove-Item -LiteralPath $outputRoot -Recurse -Force
    }
}
