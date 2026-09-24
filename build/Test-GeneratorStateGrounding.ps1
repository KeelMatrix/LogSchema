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

    Write-Host 'Pinned Microsoft.Extensions.Logging generator state grounding passed.'
}
finally {
    if (Test-Path -LiteralPath $outputRoot) {
        Remove-Item -LiteralPath $outputRoot -Recurse -Force
    }
}
