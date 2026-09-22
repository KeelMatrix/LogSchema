$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactRoot = Join-Path $repositoryRoot 'artifacts/validation'
$packageOutput = Join-Path $artifactRoot 'package'
$repeatPackageOutput = Join-Path $artifactRoot 'package-repeat'
$smokeRoot = Join-Path $artifactRoot 'consumer-smoke'
$toolProject = Join-Path $repositoryRoot 'src/KeelMatrix.LogSchema/KeelMatrix.LogSchema.csproj'
$coreProject = Join-Path $repositoryRoot 'src/KeelMatrix.LogSchema.Core/KeelMatrix.LogSchema.Core.csproj'
$consumerFixture = Join-Path $repositoryRoot 'tests/PackageConsumerFixture'

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

    $json = (& dotnet msbuild $Project -getProperty:PackageId -getProperty:PackageVersion -getProperty:IncludeSymbols -nologo | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read package properties from $Project."
    }
    return $json | ConvertFrom-Json
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

function Invoke-SmokeStep {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$ToolCommand,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $global:LASTEXITCODE = 0
    $output = (& $ToolCommand @Arguments 2>&1 | Out-String).TrimEnd()
    $exitCode = $LASTEXITCODE
    Write-Host "--- $Label ---"
    Write-Host ("Input: logschema {0}" -f ($Arguments -join ' '))
    Write-Host "Exit code: $exitCode"
    if ($output.Length -gt 0) {
        Write-Host $output
    }
    return [pscustomobject]@{ Label = $Label; ExitCode = $exitCode; Output = $output }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:KEELMATRIX_NO_TELEMETRY = '1'

$solution = Join-Path $repositoryRoot 'LogSchema.slnx'
Invoke-Timed 'Restore solution' { dotnet restore $solution --configfile (Join-Path $repositoryRoot 'NuGet.config') --nologo }
foreach ($fixture in @(
        'fixtures/Phase0.Net8/Phase0.Net8.csproj',
        'fixtures/Phase0.Stable/Phase0.Stable.csproj',
        'fixtures/Phase0.Multi/Phase0.Multi.csproj',
        'fixtures/Phase0.Pairing/Phase0.Pairing.csproj',
        'fixtures/Phase0.Sentinel/Phase0.Sentinel.csproj',
        'fixtures/Phase0.GeneratedDeclarationGenerator/Phase0.GeneratedDeclarationGenerator.csproj')) {
    Invoke-Timed "Restore $fixture" { dotnet restore (Join-Path $repositoryRoot $fixture) --configfile (Join-Path $repositoryRoot 'NuGet.config') --nologo }
}

Invoke-Timed 'Vulnerability audit' {
    dotnet list $coreProject package --vulnerable --include-transitive --framework net8.0 --no-restore
    Assert-That ($LASTEXITCODE -eq 0) 'core dependency vulnerability audit must pass'
    dotnet list $toolProject package --vulnerable --include-transitive --framework net8.0 --no-restore
    Assert-That ($LASTEXITCODE -eq 0) 'tool dependency vulnerability audit must pass'
}
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
Invoke-Timed 'Release build' { dotnet build $solution -c Release --no-restore --nologo }
Invoke-Timed 'Unit and contract tests' { dotnet test (Join-Path $repositoryRoot 'tests/KeelMatrix.LogSchema.Tests/KeelMatrix.LogSchema.Tests.csproj') -c Release --no-build --no-restore --nologo }
Invoke-Timed 'Phase 0 regression matrix' { & pwsh -NoProfile -NonInteractive -File (Join-Path $repositoryRoot 'build/Test-Phase0Matrix.ps1') }

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
    $expectedPackageName = "$packageId.$packageVersion.nupkg"
$expectedSymbolsName = "$packageId.$packageVersion.snupkg"

Invoke-Timed 'Pack and inspect' {
    New-Item -ItemType Directory -Force -Path $packageOutput, $repeatPackageOutput | Out-Null
    dotnet pack $toolProject -c Release --no-build --no-restore --nologo -o $packageOutput
    Assert-That ($LASTEXITCODE -eq 0) 'the Release package must build'
    dotnet pack $toolProject -c Release --no-build --no-restore --nologo -o $repeatPackageOutput
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
    Assert-That (Test-Path -LiteralPath $packagePath) "missing $expectedPackageName"

    $entryNames = Get-ZipEntryNames -ArchivePath $packagePath
    $nuspecEntry = @($entryNames | Where-Object { $_ -match '\.nuspec$' })
    Assert-That ($nuspecEntry.Count -eq 1) 'the package must contain exactly one nuspec'
    $nuspecText = Get-ZipEntryText -ArchivePath $packagePath -EntryName $nuspecEntry[0]
    $nuspec = [xml]$nuspecText
    $iconNode = $nuspec.SelectSingleNode("//*[local-name()='icon']")
    Assert-That ($null -ne $iconNode -and $iconNode.InnerText -eq 'icon.png') 'nuspec icon must be icon.png'
    Write-Host "Nuspec icon: $($iconNode.InnerText)"
    $licenseNode = $nuspec.SelectSingleNode("//*[local-name()='license']")
    Assert-That ($null -ne $licenseNode -and $licenseNode.InnerText -eq 'MIT') 'nuspec license must be MIT'
    $repositoryNode = $nuspec.SelectSingleNode("//*[local-name()='repository']")
    Assert-That ($null -ne $repositoryNode -and $repositoryNode.GetAttribute('type') -eq 'git' -and $repositoryNode.GetAttribute('url') -eq 'https://github.com/KeelMatrix/LogSchema') 'nuspec repository metadata must identify the LogSchema git repository'

    Assert-That ($entryNames -contains 'tools/net8.0/any/DotnetToolSettings.xml') 'tool settings must be under tools/net8.0/any'
    $toolSettings = [xml](Get-ZipEntryText -ArchivePath $packagePath -EntryName 'tools/net8.0/any/DotnetToolSettings.xml')
    $commandNode = $toolSettings.SelectSingleNode("//*[local-name()='Command']")
    Assert-That ($null -ne $commandNode -and $commandNode.GetAttribute('Name') -eq 'logschema') 'tool command identity must be logschema'
    Assert-That ($entryNames -contains 'README.md') 'package README must be at the package root'
    Assert-That ($entryNames -contains 'icon.png') 'package icon must be at the package root'
    Assert-That ($entryNames -contains 'tools/net8.0/any/KeelMatrix.LogSchema.Core.dll') 'compiled Core assembly must be inside the tool package'

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
    if ($dependencies.Count -eq 0) {
        Write-Host 'Nuspec dependency graph: none.'
    }
    else {
        foreach ($dependency in $dependencies) {
            Write-Host ("Nuspec dependency: {0} {1}" -f $dependency.GetAttribute('id'), $dependency.GetAttribute('version'))
        }
    }
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

    $forbiddenEntryPattern = '(^|/)(tests?|fixtures?|phase0|bench(?:marks)?)(/|$)|(^|/)\.env(?:\.|$)|(^|/)AGENTS\.md$|\.(cs|csproj|sln|slnx|props|targets|runsettings)$'
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
    $textEntryExtensions = @('.config', '.json', '.md', '.nuspec', '.rels', '.runtimeconfig', '.xml')
    foreach ($entryName in $entryNames) {
        Assert-That ($entryName -notmatch $forbiddenEntryPattern) "forbidden package entry: $entryName"
        if ([IO.Path]::GetExtension($entryName) -notin $textEntryExtensions) {
            continue
        }
        $entryBytes = Get-ZipEntryBytes -ArchivePath $packagePath -EntryName $entryName
        $entryText = [Text.Encoding]::UTF8.GetString($entryBytes)
        Assert-That ($entryText -notmatch $internalLanguagePattern) "internal wording found in package entry: $entryName"
        Assert-That ($entryText -notmatch '(?i)(?<![A-Za-z])[A-Z]:[\\/]|/home/|/Users/|/mnt/|/root/|\\\\') "absolute machine path found in package entry: $entryName"
    }
    Write-Host 'Package content safety: no tests, fixtures, Phase 0, benchmark, environment, source-only, internal-wording, or absolute-path entries found.'
}

Invoke-Timed 'Attached/detached package reproducibility' {
    & pwsh -NoProfile -NonInteractive -File (Join-Path $repositoryRoot 'build/Test-PackageReproducibility.ps1') -RepositoryRoot $repositoryRoot
    Assert-That ($LASTEXITCODE -eq 0) 'attached/detached package reproducibility regression must pass'
}

Invoke-Timed 'Isolated consumer restore and tool install' {
    $feed = Join-Path $smokeRoot 'feed'
    $consumerRoot = Join-Path $smokeRoot 'consumer'
    $toolPath = Join-Path $smokeRoot 'tool'
    $cachePath = Join-Path $smokeRoot 'nuget-packages'
    $httpCachePath = Join-Path $smokeRoot 'nuget-http-cache'
    foreach ($path in @($feed, $consumerRoot, $toolPath, $cachePath, $httpCachePath)) {
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }
    Copy-Item -LiteralPath (Join-Path $packageOutput $expectedPackageName) -Destination $feed
    Copy-Item -Path (Join-Path $consumerFixture '*') -Destination $consumerRoot -Recurse

    $escapedFeed = [System.Security.SecurityElement]::Escape($feed)
    $config = Join-Path $smokeRoot 'NuGet.config'
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
      <package pattern="$packageId" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $config -Encoding utf8

    $oldPackages = $env:NUGET_PACKAGES
    $oldHttpCache = $env:NUGET_HTTP_CACHE_PATH
    try {
        $env:NUGET_PACKAGES = $cachePath
        $env:NUGET_HTTP_CACHE_PATH = $httpCachePath
        $consumerProject = Join-Path $consumerRoot 'PackageConsumerFixture.csproj'
        dotnet restore $consumerProject --configfile $config --no-cache --nologo
        Assert-That ($LASTEXITCODE -eq 0) 'consumer fixture restore must succeed from controlled sources'
        dotnet tool install $packageId --version $packageVersion --tool-path $toolPath --configfile $config --no-cache --ignore-failed-sources
        Assert-That ($LASTEXITCODE -eq 0) 'packed tool installation must succeed from the isolated local feed'
    }
    finally {
        $env:NUGET_PACKAGES = $oldPackages
        $env:NUGET_HTTP_CACHE_PATH = $oldHttpCache
    }
}

Invoke-Timed 'Isolated packed-tool consumer smoke' {
    $consumerRoot = Join-Path $smokeRoot 'consumer'
    $toolPath = Join-Path $smokeRoot 'tool'
    $consumerProject = Join-Path $consumerRoot 'PackageConsumerFixture.csproj'
    $baseline = Join-Path $smokeRoot 'consumer-manifest.json'
    $toolCommandPath = Join-Path $toolPath ($(if ($IsWindows) { 'logschema.exe' } else { 'logschema' }))
    Assert-That (Test-Path -LiteralPath $toolCommandPath) "installed logschema command not found at $toolCommandPath"

    $oldLocation = Get-Location
    try {
        Set-Location $smokeRoot
        $help = Invoke-SmokeStep -Label 'help' -ToolCommand $toolCommandPath -Arguments @('--help')
        Assert-That ($help.ExitCode -eq 0) 'logschema --help must return exit 0'
        Assert-That ($help.Output -match 'logschema capture' -and $help.Output -match 'Usage:') 'help must identify the logschema command'

        $capture = Invoke-SmokeStep -Label 'capture' -ToolCommand $toolCommandPath -Arguments @('capture', $consumerProject, '--output', $baseline, '--no-telemetry')
        Assert-That ($capture.ExitCode -eq 0 -and (Test-Path -LiteralPath $baseline)) 'consumer capture must return exit 0 and write a manifest'

        $clean = Invoke-SmokeStep -Label 'clean check' -ToolCommand $toolCommandPath -Arguments @('check', $consumerProject, '--baseline', $baseline, '--no-telemetry')
        Assert-That ($clean.ExitCode -eq 0) 'clean consumer check must return exit 0'
        Assert-That ($clean.Output -match 'LogSchema: no gated incompatibilities found\.') 'clean check must report no gated incompatibilities'

        $sourcePath = Join-Path $consumerRoot 'ConsumerLogging.cs'
        $source = Get-Content -Raw -LiteralPath $sourcePath
        $source = $source.Replace('Processed {OrderId}', 'Processed {AccountId}').Replace('int orderId', 'int accountId')
        Set-Content -LiteralPath $sourcePath -Value $source -Encoding utf8
        $mutated = Invoke-SmokeStep -Label 'mutated structured field check' -ToolCommand $toolCommandPath -Arguments @('check', $consumerProject, '--baseline', $baseline, '--no-telemetry')
        $expectedDiagnostic = 'BREAKING KMLOG102 ConsumerProcessed changed structured property "OrderId" to "AccountId".'
        Assert-That ($mutated.ExitCode -eq 1) 'mutated consumer check must return exit 1'
        Assert-That ($mutated.Output.Contains($expectedDiagnostic)) "mutated check must contain the exact diagnostic: $expectedDiagnostic"

        $invalid = Invoke-SmokeStep -Label 'invalid configuration' -ToolCommand $toolCommandPath -Arguments @('check', $consumerProject, '--baseline', $baseline, '--severity', 'invalid', '--no-telemetry')
        Assert-That ($invalid.ExitCode -eq 2) 'invalid severity configuration must return documented exit 2'
        Assert-That ($invalid.Output -match '--severity must be breaking, warning, or all\.') 'invalid configuration must explain the accepted severity values'
        $global:LASTEXITCODE = 0
    }
    finally {
        Set-Location $oldLocation
    }
}

Write-Host 'Validation passed.'
Write-Host 'Stage timings:'
foreach ($stage in $script:StageTimings) {
    Write-Host ("  {0}: {1:N2}s" -f $stage.Label, $stage.Seconds)
}
