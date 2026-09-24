param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$ReleaseVersion
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactRoot = Join-Path $repositoryRoot 'artifacts/validation'
$packageOutput = Join-Path $artifactRoot 'package'
$repeatPackageOutput = Join-Path $artifactRoot 'package-repeat'
$smokeRoot = Join-Path $artifactRoot 'consumer-smoke'
$toolProject = Join-Path $repositoryRoot 'src/KeelMatrix.LogSchema/KeelMatrix.LogSchema.csproj'
$coreProject = Join-Path $repositoryRoot 'src/KeelMatrix.LogSchema.Core/KeelMatrix.LogSchema.Core.csproj'
$consumerFixture = Join-Path $repositoryRoot 'tests/PackageConsumerFixture'
$nugetConfig = Join-Path $repositoryRoot 'NuGet.config'
$vulnerabilityReportValidator = Join-Path $repositoryRoot 'build/Test-VulnerabilityReport.ps1'
$msbuildVersionArgument = @()
if (-not [string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    $msbuildVersionArgument = @("-p:Version=$ReleaseVersion")
}

if (Test-Path -LiteralPath $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

$script:StageTimings = [System.Collections.Generic.List[object]]::new()

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Invoke-Timed {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $global:LASTEXITCODE = 0
    try {
        & $Action
        $actionExitCode = $LASTEXITCODE
    }
    finally {
        $timer.Stop()
        [void]$script:StageTimings.Add([pscustomobject]@{
                Label = $Label
                Seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
            })
        Write-Host ("{0}: {1:N2}s" -f $Label, $timer.Elapsed.TotalSeconds)
    }

    if ($actionExitCode -ne 0) {
        throw "$Label failed with exit code $actionExitCode"
    }
}

function Get-ProjectProperties {
    param([Parameter(Mandatory = $true)][string]$Project)

    $json = (& dotnet msbuild $Project -getProperty:PackageId -getProperty:PackageVersion -getProperty:IncludeSymbols -getProperty:AssemblyName @msbuildVersionArgument -nologo | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read package properties from $Project."
    }
    return $json | ConvertFrom-Json
}

function Assert-ExactSet {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string[]]$Actual,
        [Parameter(Mandatory = $true)][string[]]$Expected
    )

    $differences = @(Compare-Object -ReferenceObject @($Expected | Sort-Object -Unique) -DifferenceObject @($Actual | Sort-Object -Unique))
    if ($differences.Count -gt 0) {
        foreach ($difference in $differences) {
            Write-Host ("{0} entry difference [{1}]: {2}" -f $Label, $difference.SideIndicator, $difference.InputObject)
        }
    }
    Assert-That ($differences.Count -eq 0) "$Label contents must match the exact expected set"
}

function Get-PortablePdbInfo {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $sourceLinkKind = [Guid]'CC110556-A091-4D38-9FEC-25AB9A351A6A'
    $stream = [IO.MemoryStream]::new($Bytes, $false)
    $provider = [System.Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream(
        $stream,
        [System.Reflection.Metadata.MetadataStreamOptions]::LeaveOpen)
    try {
        $reader = $provider.GetMetadataReader()
        $documents = @($reader.Documents | ForEach-Object {
                $document = $reader.GetDocument($_)
                $reader.GetString($document.Name)
            })
        $sourceLinks = @($reader.CustomDebugInformation | ForEach-Object {
                $customDebug = $reader.GetCustomDebugInformation($_)
                if ($reader.GetGuid($customDebug.Kind) -eq $sourceLinkKind) {
                    [Text.Encoding]::UTF8.GetString($reader.GetBlobBytes($customDebug.Value))
                }
            } | Where-Object { $null -ne $_ })
        return [pscustomobject]@{
            Documents = $documents
            SourceLinks = $sourceLinks
        }
    }
    finally {
        $provider.Dispose()
        $stream.Dispose()
    }
}

function Assert-PortablePdbProvenance {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$EntryName,
        [Parameter(Mandatory = $true)][string]$ExpectedCommit,
        [Parameter(Mandatory = $true)][string]$ExpectedSourceDirectory
    )

    $pdb = Get-PortablePdbInfo -Bytes (Get-ZipEntryBytes -ArchivePath $ArchivePath -EntryName $EntryName)
    Assert-That ($pdb.Documents.Count -gt 0) "$EntryName must contain portable PDB documents"
    Assert-That ($pdb.SourceLinks.Count -eq 1) "$EntryName must contain exactly one SourceLink record"
    foreach ($document in $pdb.Documents) {
        Assert-That ($document -notmatch '(?i)^[A-Z]:[\\/]|\\\\|/Users/|/home/|/mnt/|/root/') "$EntryName contains a private machine path: $document"
    }
    Assert-That (@($pdb.Documents | Where-Object { $_ -like "/_/src/$ExpectedSourceDirectory/*.cs" }).Count -gt 0) "$EntryName must map repository source documents under /_/src/$ExpectedSourceDirectory"

    try {
        $sourceLink = $pdb.SourceLinks[0] | ConvertFrom-Json
    }
    catch {
        throw "$EntryName contains unusable SourceLink JSON. $($_.Exception.Message)"
    }
    $mappings = @($sourceLink.documents.PSObject.Properties)
    Assert-That ($mappings.Count -eq 1) "$EntryName must contain exactly one SourceLink mapping"
    Assert-That ($mappings[0].Name -eq '/_/*') "$EntryName SourceLink key must be /_/*"
    $expectedUrl = "https://raw.githubusercontent.com/KeelMatrix/LogSchema/$ExpectedCommit/*"
    Assert-That ([string]$mappings[0].Value -eq $expectedUrl) "$EntryName SourceLink URL must be $expectedUrl"
    Write-Host "SourceLink: $EntryName documents=$($pdb.Documents.Count) mapping=$expectedUrl"
}

function Invoke-VulnerabilityAudit {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$ReportPath
    )

    $global:LASTEXITCODE = 0
    $report = (& dotnet package list --project $Project --vulnerable --include-transitive --framework net8.0 --no-restore --config $nugetConfig --format json --output-version 1 2>&1 | Out-String)
    $commandExitCode = $LASTEXITCODE
    $report | Set-Content -LiteralPath $ReportPath -Encoding utf8NoBOM
    & pwsh -NoProfile -NonInteractive -File $vulnerabilityReportValidator `
        -ReportPath $ReportPath `
        -ExpectedProjectPath $Project `
        -CommandExitCode $commandExitCode
    Assert-That ($LASTEXITCODE -eq 0) "vulnerability report validation failed for $Project"
}

function Get-ZipEntryBytes {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.GetEntry($EntryName)
        if ($null -eq $entry) {
            throw "Archive entry '$EntryName' was not found in '$ArchivePath'."
        }
        $stream = $entry.Open()
        $memory = [System.IO.MemoryStream]::new()
        try {
            $stream.CopyTo($memory)
            return ,([byte[]]$memory.ToArray())
        }
        finally {
            $memory.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-ZipEntryText {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    $bytes = Get-ZipEntryBytes -ArchivePath $ArchivePath -EntryName $EntryName
    return ([System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)).TrimStart([char]0xFEFF)
}

function Get-ZipEntryHash {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.GetEntry($EntryName)
        if ($null -eq $entry) {
            throw "Archive entry '$EntryName' was not found in '$ArchivePath'."
        }
        $stream = $entry.Open()
        try {
            $hash = [System.Security.Cryptography.SHA256]::Create().ComputeHash($stream)
            return ([BitConverter]::ToString($hash)).Replace('-', '').ToUpperInvariant()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-ZipEntryNames {
    param([Parameter(Mandatory = $true)][string]$ArchivePath)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        return @($archive.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $archive.Dispose()
    }
}

function Get-BytesHash {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $hash = [System.Security.Cryptography.SHA256]::HashData($Bytes)
    return ([BitConverter]::ToString($hash)).Replace('-', '').ToUpperInvariant()
}

function Get-PngDimensions {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    Assert-That ($Bytes.Length -ge 24) 'PNG must contain an IHDR header.'
    Assert-That (($Bytes[0] -eq 137) -and ($Bytes[1] -eq 80) -and ($Bytes[2] -eq 78) -and ($Bytes[3] -eq 71)) 'icon.png must have a PNG signature.'
    $width = ([int]$Bytes[16] -shl 24) -bor ([int]$Bytes[17] -shl 16) -bor ([int]$Bytes[18] -shl 8) -bor [int]$Bytes[19]
    $height = ([int]$Bytes[20] -shl 24) -bor ([int]$Bytes[21] -shl 16) -bor ([int]$Bytes[22] -shl 8) -bor [int]$Bytes[23]
    return [pscustomobject]@{ Width = $width; Height = $height }
}

function Get-ResolvedPackages {
    param([Parameter(Mandatory = $true)][string]$Project)

    $assetsPath = Join-Path (Split-Path -Parent $Project) 'obj/project.assets.json'
    Assert-That (Test-Path -LiteralPath $assetsPath) "Restore did not produce $assetsPath."
    $assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
    return @($assets.libraries.PSObject.Properties | ForEach-Object {
            $parts = $_.Name -split '/', 2
            [pscustomobject]@{
                Id = $parts[0]
                Version = $parts[1]
                Type = $_.Value.type
            }
        } | Sort-Object Id, Version)
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:KEELMATRIX_NO_TELEMETRY = '1'

$solution = Join-Path $repositoryRoot 'LogSchema.slnx'
Invoke-Timed 'Restore solution' { dotnet restore $solution --configfile $nugetConfig @msbuildVersionArgument --nologo }
foreach ($fixture in @(
        'fixtures/Phase0.Net8/Phase0.Net8.csproj',
        'fixtures/Phase0.Stable/Phase0.Stable.csproj',
        'fixtures/Phase0.Multi/Phase0.Multi.csproj',
        'fixtures/Phase0.Pairing/Phase0.Pairing.csproj',
        'fixtures/Phase0.Sentinel/Phase0.Sentinel.csproj',
        'fixtures/Phase0.GeneratedDeclarationGenerator/Phase0.GeneratedDeclarationGenerator.csproj')) {
    Invoke-Timed "Restore $fixture" { dotnet restore (Join-Path $repositoryRoot $fixture) --configfile $nugetConfig --nologo }
}

Invoke-Timed 'Vulnerability audit' {
    Invoke-VulnerabilityAudit -Project $coreProject -ReportPath (Join-Path $artifactRoot 'core-vulnerability-report.json')
    Invoke-VulnerabilityAudit -Project $toolProject -ReportPath (Join-Path $artifactRoot 'tool-vulnerability-report.json')
}
Invoke-Timed 'Vulnerability audit regression tests' { & pwsh -NoProfile -NonInteractive -File (Join-Path $repositoryRoot 'build/Test-VulnerabilityReport.Tests.ps1') }
Invoke-Timed 'Release contract regression tests' { & pwsh -NoProfile -NonInteractive -File (Join-Path $repositoryRoot 'build/Test-ReleaseVersion.Tests.ps1') }
Invoke-Timed 'Release workflow contract' { & pwsh -NoProfile -NonInteractive -File (Join-Path $repositoryRoot 'build/Test-ReleaseWorkflow.ps1') }
Invoke-Timed 'Pack-safety regression tests' { & pwsh -NoProfile -NonInteractive -File (Join-Path $repositoryRoot 'build/Test-PackSafety.ps1') }
Invoke-Timed 'Documentation and repository consistency' { & pwsh -NoProfile -NonInteractive -File (Join-Path $repositoryRoot 'build/Test-Documentation.ps1') }
Invoke-Timed 'Line-ending contract' {
    $trackedFiles = @(git -C $repositoryRoot ls-files)
    Assert-That ($LASTEXITCODE -eq 0) 'git must enumerate tracked files for the line-ending check'
    foreach ($relativePath in $trackedFiles) {
        if ($relativePath -eq 'icon.png') {
            continue
        }

        $bytes = [IO.File]::ReadAllBytes((Join-Path $repositoryRoot $relativePath))
        Assert-That ([Array]::IndexOf($bytes, [byte]13) -lt 0) "tracked text file contains CRLF: $relativePath"
        if ($bytes.Length -gt 0) {
            Assert-That ($bytes[$bytes.Length - 1] -eq 10) "tracked text file must end with LF: $relativePath"
        }
    }

    $validateAttributesOutput = & git -C $repositoryRoot check-attr eol -- build/validate.ps1 | Out-String
    Assert-That ($LASTEXITCODE -eq 0) 'git must read repository attributes for the line-ending check'
    $validateAttributes = if ($null -eq $validateAttributesOutput) { '' } else { $validateAttributesOutput.Trim() }
    Assert-That ($validateAttributes -match 'build/validate\.ps1: eol: lf') 'repository attributes must require LF for PowerShell validation scripts'
}
Invoke-Timed 'Format and analyzer validation' { dotnet format $solution --no-restore --verify-no-changes --verbosity minimal }
Invoke-Timed 'Release build' { dotnet build $solution -c Release --no-restore @msbuildVersionArgument --nologo }
Invoke-Timed 'Pinned generator state grounding' { & pwsh -NoProfile -NonInteractive -File (Join-Path $repositoryRoot 'build/Test-GeneratorStateGrounding.ps1') -RepositoryRoot $repositoryRoot }
Invoke-Timed 'Unit and contract tests' { dotnet test (Join-Path $repositoryRoot 'tests/KeelMatrix.LogSchema.Tests/KeelMatrix.LogSchema.Tests.csproj') -c Release --no-build --no-restore --nologo }
Invoke-Timed 'Phase 0 regression matrix' { & pwsh -NoProfile -NonInteractive -File (Join-Path $repositoryRoot 'build/Test-Phase0Matrix.ps1') }
Invoke-Timed 'Shipping semantic fixture matrix' { & pwsh -NoProfile -NonInteractive -File (Join-Path $repositoryRoot 'build/Test-ShippingMatrix.ps1') -RepositoryRoot $repositoryRoot }

$tool = Join-Path $repositoryRoot 'src/KeelMatrix.LogSchema/bin/Release/net8.0/KeelMatrix.LogSchema.dll'
$fixtureProject = Join-Path $repositoryRoot 'fixtures/Phase0.Net8/Phase0.Net8.csproj'
$determinismA = Join-Path $artifactRoot 'determinism-a/logschema.json'
$determinismB = Join-Path $artifactRoot 'determinism-b/logschema.json'
Invoke-Timed 'Manifest determinism' {
    & dotnet $tool capture $fixtureProject --tfm net8.0 --output $determinismA --no-telemetry
    Assert-That ($LASTEXITCODE -eq 0) 'first deterministic capture must succeed'
    & dotnet $tool capture $fixtureProject --tfm net8.0 --output $determinismB --no-telemetry
    Assert-That ($LASTEXITCODE -eq 0) 'second deterministic capture must succeed'
    $hashA = (Get-FileHash $determinismA -Algorithm SHA256).Hash
    $hashB = (Get-FileHash $determinismB -Algorithm SHA256).Hash
    Write-Host "Windows manifest A SHA256: $hashA"
    Write-Host "Windows manifest B SHA256: $hashB"
    Assert-That ($hashA -eq $hashB) 'same source must produce byte-identical manifests'
}

Invoke-Timed 'CLI failure safety' {
    & dotnet $tool check (Join-Path $repositoryRoot 'does-not-exist.csproj') --baseline $determinismA --format json --no-telemetry | Out-Null
    Assert-That ($LASTEXITCODE -eq 3) 'project-load failure must return exit 3'
    $before = (Get-FileHash $determinismA -Algorithm SHA256).Hash
    & dotnet $tool diff (Join-Path $repositoryRoot 'does-not-exist.json') $determinismA --format json --no-telemetry | Out-Null
    Assert-That ($LASTEXITCODE -eq 3) 'missing manifest must return exit 3'
    $after = (Get-FileHash $determinismA -Algorithm SHA256).Hash
    Assert-That ($before -eq $after) 'failed comparison must not rewrite the baseline'
    $global:LASTEXITCODE = 0
}

Invoke-Timed 'Zero-event analysis safety' {
    $zeroRoot = Join-Path $artifactRoot 'zero-events'
    New-Item -ItemType Directory -Force -Path $zeroRoot | Out-Null
    $zeroProject = Join-Path $zeroRoot 'ZeroEvents.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
"@ | Set-Content -LiteralPath $zeroProject -Encoding utf8NoBOM
    'public sealed class Empty { }' | Set-Content -LiteralPath (Join-Path $zeroRoot 'Empty.cs') -Encoding utf8NoBOM
    $zeroBaseline = Join-Path $zeroRoot 'logschema.json'
    $zeroOutput = (& dotnet $tool capture $zeroProject --output $zeroBaseline --no-telemetry 2>&1 | Out-String)
    Assert-That ($LASTEXITCODE -eq 3) 'zero-event capture must return exit 3'
    Assert-That ($zeroOutput -match 'KMLOGP006') 'zero-event capture must report KMLOGP006'
    Assert-That (-not (Test-Path -LiteralPath $zeroBaseline)) 'zero-event capture must not write a baseline'

    @"
{
  "schemaVersion": 1,
  "projects": [],
  "events": [],
  "unsupported": [],
  "analysisIssues": [],
  "compilationDiagnosticKinds": [],
  "workspaceDiagnosticKinds": []
}
"@ | Set-Content -LiteralPath $zeroBaseline -Encoding utf8NoBOM
    $baselineOutput = (& dotnet $tool check $fixtureProject --tfm net8.0 --baseline $zeroBaseline --no-telemetry 2>&1 | Out-String)
    Assert-That ($LASTEXITCODE -eq 3) 'zero-event baseline check must return exit 3'
    Assert-That ($baselineOutput -match 'KMLOGP006') 'zero-event baseline check must report KMLOGP006'
    $diffOutput = (& dotnet $tool diff $zeroBaseline $zeroBaseline --no-telemetry 2>&1 | Out-String)
    Assert-That ($LASTEXITCODE -eq 0) 'diff of zero-event manifests must remain a pure manifest comparison'
    Assert-That ($diffOutput -notmatch 'KMLOGP006') 'diff must not apply the zero-event check gate'
    $global:LASTEXITCODE = 0
}

$properties = Get-ProjectProperties -Project $toolProject
$packageId = [string]$properties.Properties.PackageId
$packageVersion = [string]$properties.Properties.PackageVersion
$includeSymbols = [string]$properties.Properties.IncludeSymbols -eq 'true'
$assemblyName = [string]$properties.Properties.AssemblyName
$expectedPackageName = "$packageId.$packageVersion.nupkg"
$expectedSymbolsName = "$packageId.$packageVersion.snupkg"
if (-not [string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    Assert-That ($packageVersion -eq $ReleaseVersion) "package version $packageVersion must match requested release version $ReleaseVersion"
}

Invoke-Timed 'Pack and inspect' {
    New-Item -ItemType Directory -Force -Path $packageOutput, $repeatPackageOutput | Out-Null
    dotnet pack $toolProject -c Release --no-build --no-restore @msbuildVersionArgument --nologo -o $packageOutput
    Assert-That ($LASTEXITCODE -eq 0) 'the Release package must build'
    dotnet pack $toolProject -c Release --no-build --no-restore @msbuildVersionArgument --nologo -o $repeatPackageOutput
    Assert-That ($LASTEXITCODE -eq 0) 'the repeated Release package must build'

    $artifactNames = @(Get-ChildItem -LiteralPath $packageOutput -File | Select-Object -ExpandProperty Name | Sort-Object)
    $repeatArtifactNames = @(Get-ChildItem -LiteralPath $repeatPackageOutput -File | Select-Object -ExpandProperty Name | Sort-Object)
    $expectedNames = @($expectedPackageName)
    if ($includeSymbols) { $expectedNames += $expectedSymbolsName }
    Assert-That ((Compare-Object -ReferenceObject $expectedNames -DifferenceObject $artifactNames).Count -eq 0) "package artifacts must be exactly: $($expectedNames -join ', ')"
    Assert-That ((Compare-Object -ReferenceObject $expectedNames -DifferenceObject $repeatArtifactNames).Count -eq 0) "repeated package artifacts must be exactly: $($expectedNames -join ', ')"
    foreach ($artifactName in $artifactNames) {
        $artifactPath = Join-Path $packageOutput $artifactName
        $repeatArtifactPath = Join-Path $repeatPackageOutput $artifactName
        $artifactHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToUpperInvariant()
        $repeatArtifactHash = (Get-FileHash -LiteralPath $repeatArtifactPath -Algorithm SHA256).Hash.ToUpperInvariant()
        $artifactBytes = (Get-Item -LiteralPath $artifactPath).Length
        $repeatArtifactBytes = (Get-Item -LiteralPath $repeatArtifactPath).Length
        Write-Host "Artifact: $artifactName A_BYTES=$artifactBytes A_SHA256=$artifactHash B_BYTES=$repeatArtifactBytes B_SHA256=$repeatArtifactHash"
        Assert-That ($artifactBytes -eq $repeatArtifactBytes -and $artifactHash -eq $repeatArtifactHash) "$artifactName must be byte-identical across consecutive packs"
    }
    $packagePath = Join-Path $packageOutput $expectedPackageName
    $symbolsPath = Join-Path $packageOutput $expectedSymbolsName
    Assert-That (Test-Path -LiteralPath $packagePath) "missing $expectedPackageName"
    Assert-That (Test-Path -LiteralPath $symbolsPath) "missing $expectedSymbolsName"

    $entryNames = Get-ZipEntryNames -ArchivePath $packagePath
    $symbolEntryNames = Get-ZipEntryNames -ArchivePath $symbolsPath
    $publishDirectory = Join-Path $repositoryRoot 'src/KeelMatrix.LogSchema/bin/Release/net8.0/publish'
    Assert-That (Test-Path -LiteralPath $publishDirectory -PathType Container) "publish output was not found: $publishDirectory"
    $publishedToolEntries = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse | ForEach-Object {
            $relativePath = [IO.Path]::GetRelativePath($publishDirectory, $_.FullName).Replace('\', '/')
            if ($relativePath -notin @($assemblyName, "$assemblyName.exe")) {
                "tools/net8.0/any/$relativePath"
            }
        } | Where-Object { $null -ne $_ })
    $expectedPackageEntries = @(
        '_rels/.rels',
        '[Content_Types].xml',
        "$packageId.nuspec",
        'package/services/metadata/core-properties/nuget.psmdcp',
        'README.md',
        'icon.png',
        'tools/net8.0/any/DotnetToolSettings.xml'
    ) + $publishedToolEntries
    $expectedSymbolEntries = @(
        '_rels/.rels',
        '[Content_Types].xml',
        "$packageId.nuspec",
        'package/services/metadata/core-properties/nuget.psmdcp',
        'tools/net8.0/any/KeelMatrix.LogSchema.pdb',
        'tools/net8.0/any/KeelMatrix.LogSchema.Core.pdb'
    )
    Assert-ExactSet -Label $expectedPackageName -Actual $entryNames -Expected $expectedPackageEntries
    Assert-ExactSet -Label $expectedSymbolsName -Actual $symbolEntryNames -Expected $expectedSymbolEntries

    $nuspecEntry = @($entryNames | Where-Object { $_ -match '\.nuspec$' })
    Assert-That ($nuspecEntry.Count -eq 1) 'the package must contain exactly one nuspec'
    $nuspecText = Get-ZipEntryText -ArchivePath $packagePath -EntryName $nuspecEntry[0]
    $nuspec = [xml]$nuspecText
    $idNode = $nuspec.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='id']")
    $versionNode = $nuspec.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='version']")
    Assert-That ($null -ne $idNode -and $idNode.InnerText -eq $packageId) "nuspec id must be $packageId"
    Assert-That ($null -ne $versionNode -and $versionNode.InnerText -eq $packageVersion) "nuspec version must be $packageVersion"
    $iconNode = $nuspec.SelectSingleNode("//*[local-name()='icon']")
    Assert-That ($null -ne $iconNode -and $iconNode.InnerText -eq 'icon.png') 'nuspec icon must be icon.png'
    Write-Host "Nuspec icon: $($iconNode.InnerText)"
    $licenseNode = $nuspec.SelectSingleNode("//*[local-name()='license']")
    Assert-That ($null -ne $licenseNode -and $licenseNode.InnerText -eq 'MIT') 'nuspec license must be MIT'
    $repositoryNode = $nuspec.SelectSingleNode("//*[local-name()='repository']")
    $repositoryCommit = (& git -C $repositoryRoot rev-parse HEAD | Out-String).Trim()
    Assert-That ($LASTEXITCODE -eq 0 -and $repositoryCommit -match '^[0-9a-f]{40}$') 'git must provide the package repository commit'
    Assert-That ($null -ne $repositoryNode -and $repositoryNode.GetAttribute('type') -eq 'git' -and $repositoryNode.GetAttribute('url') -eq 'https://github.com/KeelMatrix/LogSchema' -and $repositoryNode.GetAttribute('branch') -eq 'refs/heads/main' -and $repositoryNode.GetAttribute('commit') -eq $repositoryCommit) 'nuspec repository metadata must contain the canonical LogSchema URL, branch, and commit'
    $packageTypes = @($nuspec.SelectNodes("//*[local-name()='packageTypes']/*[local-name()='packageType']"))
    Assert-That ($packageTypes.Count -eq 1 -and $packageTypes[0].GetAttribute('name') -eq 'DotnetTool') 'nuspec package type must be exactly DotnetTool'

    $symbolsNuspecEntry = @($symbolEntryNames | Where-Object { $_ -match '\.nuspec$' })
    Assert-That ($symbolsNuspecEntry.Count -eq 1) 'the symbols package must contain exactly one nuspec'
    $symbolsNuspec = [xml](Get-ZipEntryText -ArchivePath $symbolsPath -EntryName $symbolsNuspecEntry[0])
    $symbolsIdNode = $symbolsNuspec.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='id']")
    $symbolsVersionNode = $symbolsNuspec.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='version']")
    $symbolsRepositoryNode = $symbolsNuspec.SelectSingleNode("//*[local-name()='repository']")
    $symbolsPackageTypes = @($symbolsNuspec.SelectNodes("//*[local-name()='packageTypes']/*[local-name()='packageType']"))
    Assert-That ($null -ne $symbolsIdNode -and $symbolsIdNode.InnerText -eq $packageId) "symbols nuspec id must be $packageId"
    Assert-That ($null -ne $symbolsVersionNode -and $symbolsVersionNode.InnerText -eq $packageVersion) "symbols nuspec version must be $packageVersion"
    Assert-That ($symbolsPackageTypes.Count -eq 1 -and $symbolsPackageTypes[0].GetAttribute('name') -eq 'SymbolsPackage') 'symbols nuspec package type must be exactly SymbolsPackage'
    Assert-That ($null -ne $symbolsRepositoryNode -and $symbolsRepositoryNode.GetAttribute('type') -eq 'git' -and $symbolsRepositoryNode.GetAttribute('url') -eq 'https://github.com/KeelMatrix/LogSchema' -and $symbolsRepositoryNode.GetAttribute('commit') -eq $repositoryCommit) 'symbols nuspec repository metadata must contain the canonical URL, type, and commit'

    Assert-That ($entryNames -contains 'tools/net8.0/any/DotnetToolSettings.xml') 'tool settings must be under tools/net8.0/any'
    $toolSettings = [xml](Get-ZipEntryText -ArchivePath $packagePath -EntryName 'tools/net8.0/any/DotnetToolSettings.xml')
    $commandNode = $toolSettings.SelectSingleNode("//*[local-name()='Command']")
    Assert-That ($null -ne $commandNode -and $commandNode.GetAttribute('Name') -eq 'logschema') 'tool command identity must be logschema'
    Assert-That ($entryNames -contains 'README.md') 'package README must be at the package root'
    Assert-That ($entryNames -contains 'icon.png') 'package icon must be at the package root'
    Assert-That ($entryNames -contains 'tools/net8.0/any/KeelMatrix.LogSchema.Core.dll') 'compiled Core assembly must be inside the tool package'
    Assert-That ($entryNames -contains 'tools/net8.0/any/KeelMatrix.LogSchema.dll') 'compiled CLI assembly must be inside the tool package'
    foreach ($pdbName in @('KeelMatrix.LogSchema.pdb', 'KeelMatrix.LogSchema.Core.pdb')) {
        $pdbEntry = "tools/net8.0/any/$pdbName"
        $packagePdbHash = Get-ZipEntryHash -ArchivePath $packagePath -EntryName $pdbEntry
        $symbolsPdbHash = Get-ZipEntryHash -ArchivePath $symbolsPath -EntryName $pdbEntry
        Assert-That ($packagePdbHash -eq $symbolsPdbHash) "$pdbEntry must be byte-identical in the tool and symbols packages"
    }
    Assert-PortablePdbProvenance -ArchivePath $symbolsPath -EntryName 'tools/net8.0/any/KeelMatrix.LogSchema.pdb' -ExpectedCommit $repositoryCommit -ExpectedSourceDirectory 'KeelMatrix.LogSchema'
    Assert-PortablePdbProvenance -ArchivePath $symbolsPath -EntryName 'tools/net8.0/any/KeelMatrix.LogSchema.Core.pdb' -ExpectedCommit $repositoryCommit -ExpectedSourceDirectory 'KeelMatrix.LogSchema.Core'

    $readmeHash = Get-ZipEntryHash -ArchivePath $packagePath -EntryName 'README.md'
    $sourceReadmeHash = (Get-FileHash (Join-Path $repositoryRoot 'src/KeelMatrix.LogSchema/README.md') -Algorithm SHA256).Hash.ToUpperInvariant()
    Assert-That ($readmeHash -eq $sourceReadmeHash) "packed README hash $readmeHash does not match project README hash $sourceReadmeHash"
    Write-Host "README SHA256: $readmeHash"

    $iconPath = Join-Path $repositoryRoot 'icon.png'
    Assert-That (Test-Path -LiteralPath $iconPath) "required icon is missing: $iconPath"
    $repoIconBytes = [IO.File]::ReadAllBytes($iconPath)
    $repoIconHash = Get-BytesHash -Bytes $repoIconBytes
    $packageIconHash = Get-ZipEntryHash -ArchivePath $packagePath -EntryName 'icon.png'
    Assert-That ($packageIconHash -eq $repoIconHash) "package icon hash $packageIconHash does not match repository icon hash $repoIconHash"
    $dimensions = Get-PngDimensions -Bytes $repoIconBytes
    Assert-That ($dimensions.Width -eq 512 -and $dimensions.Height -eq 512) 'icon must be exactly 512x512 pixels'
    Assert-That ($repoIconBytes.Length -le 200KB) 'icon must be no larger than 200 KB'
    Write-Host "Icon SHA256: repository=$repoIconHash package=$packageIconHash dimensions=$($dimensions.Width)x$($dimensions.Height) bytes=$($repoIconBytes.Length)"

    $dependencies = @($nuspec.SelectNodes("//*[local-name()='dependencies']//*[local-name()='dependency']"))
    Assert-That ($dependencies.Count -eq 0) 'the bundled tool package must not expose external nuspec dependencies'
    Write-Host 'Nuspec dependency graph: none.'
    $resolvedCorePackages = Get-ResolvedPackages -Project $coreProject
    foreach ($resolved in $resolvedCorePackages) {
        Write-Host ("Resolved Core graph: {0} {1} ({2})" -f $resolved.Id, $resolved.Version, $resolved.Type)
    }
    $resolvedToolPackages = Get-ResolvedPackages -Project $toolProject
    foreach ($resolved in $resolvedToolPackages) {
        Write-Host ("Resolved tool graph: {0} {1} ({2})" -f $resolved.Id, $resolved.Version, $resolved.Type)
    }
    $asn1 = @($resolvedCorePackages | Where-Object { $_.Id -eq 'System.Formats.Asn1' -and $_.Version -eq '9.0.1' })
    Assert-That ($asn1.Count -eq 1) 'System.Formats.Asn1 must resolve at pinned version 9.0.1'

    $forbiddenEntryPattern = '(?i)(^|/)(tests?|fixtures?|phase0|bench(?:marks)?|credentials?|secrets?|private-keys?|internal|research|review-evidence)(/|$)|(^|/)\.env(?:\.|$)|(^|/)(?:AGENTS|CONTEXT)\.md$|(?:credential|secret|api[._-]?key|access[._-]?token|client[._-]?secret)|\.(?:cs|csproj|sln|slnx|props|targets|runsettings|pfx|p12|pem|key|snk)$'
    $internalTerms = @(
        ((112, 97, 112, 101, 114, 99, 108, 105, 112) | ForEach-Object { [char]$_ }) -join '',
        ((112, 97, 99, 107, 97, 103, 101, 32, 101, 110, 103, 105, 110, 101, 101, 114) | ForEach-Object { [char]$_ }) -join '',
        ((113, 117, 97, 108, 105, 116, 121, 32, 38, 32, 97, 100, 118, 101, 114, 115, 97, 114, 105, 97, 108) | ForEach-Object { [char]$_ }) -join '',
        ((116, 97, 115, 107, 32, 100, 101, 108, 101, 103, 97, 116, 111, 114) | ForEach-Object { [char]$_ }) -join '',
        ((99, 111, 100, 101, 120) | ForEach-Object { [char]$_ }) -join '',
        ((103, 112, 116) | ForEach-Object { [char]$_ }) -join '',
        ((99, 108, 97, 117, 100, 101) | ForEach-Object { [char]$_ }) -join '',
        ((111, 114, 99, 104, 101, 115, 116, 114, 97, 116) | ForEach-Object { [char]$_ }) -join ''
    )
    $internalLanguagePattern = '(?i)' + (($internalTerms | ForEach-Object { [regex]::Escape($_) }) -join '|') + '|KEE-[0-9]+'
    $textEntryExtensions = @('.config', '.json', '.md', '.nuspec', '.psmdcp', '.rels', '.runtimeconfig', '.xml')
    foreach ($archiveAndEntry in @(
            @($entryNames | ForEach-Object { [pscustomobject]@{ Archive = $packagePath; Entry = $_ } }) +
            @($symbolEntryNames | ForEach-Object { [pscustomobject]@{ Archive = $symbolsPath; Entry = $_ } })
        )) {
        $entryName = $archiveAndEntry.Entry
        Assert-That ($entryName -notmatch $forbiddenEntryPattern) "forbidden package entry: $entryName"
        if ([IO.Path]::GetExtension($entryName) -notin $textEntryExtensions) {
            continue
        }
        $entryBytes = Get-ZipEntryBytes -ArchivePath $archiveAndEntry.Archive -EntryName $entryName
        $entryText = [Text.Encoding]::UTF8.GetString($entryBytes)
        Assert-That ($entryText -notmatch $internalLanguagePattern) "internal wording found in package entry: $entryName"
        Assert-That ($entryText -notmatch '(?i)(?<![A-Za-z])[A-Z]:[\\/]|/home/|/Users/|/mnt/|/root/|\\\\') "absolute machine path found in package entry: $entryName"
    }
    Write-Host 'Package content safety: exact archive sets enforced; no test, fixture, environment, credential, source-only, internal-wording, or private-path content found.'
}

Invoke-Timed 'Attached/detached package reproducibility' {
    & pwsh -NoProfile -NonInteractive -File (Join-Path $repositoryRoot 'build/Test-PackageReproducibility.ps1') -RepositoryRoot $repositoryRoot -ReleaseVersion $packageVersion
    Assert-That ($LASTEXITCODE -eq 0) 'attached/detached package reproducibility regression must pass'
}

Invoke-Timed 'Isolated local-manifest onboarding and installed-tool smoke' {
    & pwsh -NoProfile -NonInteractive -File (Join-Path $repositoryRoot 'build/Test-LocalToolSmoke.ps1') `
        -PackagePath (Join-Path $packageOutput $expectedPackageName) `
        -PackageId $packageId `
        -PackageVersion $packageVersion `
        -ConsumerFixture $consumerFixture `
        -WorkRoot $smokeRoot
    Assert-That ($LASTEXITCODE -eq 0) 'the local-manifest installed-tool smoke must pass'
}

Write-Host 'Validation passed.'
Write-Host 'Stage timings:'
foreach ($stage in $script:StageTimings) {
    Write-Host ("  {0}: {1:N2}s" -f $stage.Label, $stage.Seconds)
}
