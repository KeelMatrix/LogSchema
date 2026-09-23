param(
    [Parameter(Mandatory = $true)][string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

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
    try {
        & $Action
        Assert-That ($LASTEXITCODE -eq 0) "$Label failed with exit code $LASTEXITCODE"
    }
    finally {
        $timer.Stop()
        Write-Host ("{0}: {1:N2}s" -f $Label, $timer.Elapsed.TotalSeconds)
    }
}

function Get-ProjectProperties {
    param([Parameter(Mandatory = $true)][string]$Project)

    $json = (& dotnet msbuild $Project -getProperty:PackageId -getProperty:PackageVersion -getProperty:IncludeSymbols -nologo | Out-String)
    Assert-That ($LASTEXITCODE -eq 0) "Could not read package properties from $Project."
    return $json | ConvertFrom-Json
}

function Get-ArtifactSet {
    param([Parameter(Mandatory = $true)][string]$OutputDirectory)

    return @(Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name)
}

function Get-ZipEntryBytes {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.GetEntry($EntryName)
        Assert-That ($null -ne $entry) "Archive entry '$EntryName' was not found in '$ArchivePath'."
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

function Get-BytesHash {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $hash = [System.Security.Cryptography.SHA256]::HashData($Bytes)
    return ([BitConverter]::ToString($hash)).Replace('-', '').ToUpperInvariant()
}

function Get-FileHashUpper {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-GitValue {
    param(
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Description,
        [switch]$Required
    )

    $global:LASTEXITCODE = 0
    $raw = (& git -C $WorkingDirectory @Arguments 2>$null | Out-String)
    $exitCode = $LASTEXITCODE
    $value = if ($null -eq $raw) { '' } else { ([string]$raw).Trim() }

    if ($Required) {
        Assert-That ($exitCode -eq 0) "Git could not provide $Description in '$WorkingDirectory' (exit code $exitCode). Ensure this is a valid Git checkout with the required repository metadata."
        Assert-That ($value.Length -gt 0) "Git returned no $Description in '$WorkingDirectory'. Ensure the checkout contains the required repository metadata."
    }

    return $value
}

function Get-ZipEntryNames {
    param([Parameter(Mandatory = $true)][string]$ArchivePath)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        return @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
    }
    finally {
        $archive.Dispose()
    }
}

function Get-ZipEntryMetadata {
    param([Parameter(Mandatory = $true)][string]$ArchivePath)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        return @($archive.Entries | ForEach-Object {
            "{0}|length={1}|compressed={2}|timestamp={3}" -f $_.FullName, $_.Length, $_.CompressedLength, $_.LastWriteTime.UtcDateTime.ToString('O')
        })
    }
    finally {
        $archive.Dispose()
    }
}

function Compare-ZipEntries {
    param(
        [Parameter(Mandatory = $true)][string]$LeftArchive,
        [Parameter(Mandatory = $true)][string]$RightArchive
    )

    $leftNames = @(Get-ZipEntryNames -ArchivePath $LeftArchive)
    $rightNames = @(Get-ZipEntryNames -ArchivePath $RightArchive)
    $names = @($leftNames + $rightNames | Sort-Object -Unique)
    $differences = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $names) {
        if ($name -notin $leftNames -or $name -notin $rightNames) {
            [void]$differences.Add($name)
            continue
        }

        $leftBytes = Get-ZipEntryBytes -ArchivePath $LeftArchive -EntryName $name
        $rightBytes = Get-ZipEntryBytes -ArchivePath $RightArchive -EntryName $name
        if (-not [System.Linq.Enumerable]::SequenceEqual($leftBytes, $rightBytes)) {
            [void]$differences.Add($name)
        }
    }

    return $differences.ToArray()
}

function Assert-ArtifactSetsEqual {
    param(
        [Parameter(Mandatory = $true)][string]$LeftLabel,
        [Parameter(Mandatory = $true)][string]$LeftDirectory,
        [Parameter(Mandatory = $true)][string]$RightLabel,
        [Parameter(Mandatory = $true)][string]$RightDirectory,
        [Parameter(Mandatory = $true)][string[]]$ExpectedNames
    )

    $leftArtifacts = @(Get-ArtifactSet -OutputDirectory $LeftDirectory)
    $rightArtifacts = @(Get-ArtifactSet -OutputDirectory $RightDirectory)
    $leftNames = @($leftArtifacts | Select-Object -ExpandProperty Name)
    $rightNames = @($rightArtifacts | Select-Object -ExpandProperty Name)
    Assert-That ((Compare-Object -ReferenceObject $ExpectedNames -DifferenceObject $leftNames).Count -eq 0) "$LeftLabel artifacts were not exactly: $($ExpectedNames -join ', ')"
    Assert-That ((Compare-Object -ReferenceObject $ExpectedNames -DifferenceObject $rightNames).Count -eq 0) "$RightLabel artifacts were not exactly: $($ExpectedNames -join ', ')"

    foreach ($name in $ExpectedNames) {
        $leftPath = Join-Path $LeftDirectory $name
        $rightPath = Join-Path $RightDirectory $name
        $leftBytes = (Get-Item -LiteralPath $leftPath).Length
        $rightBytes = (Get-Item -LiteralPath $rightPath).Length
        $leftHash = Get-FileHashUpper -Path $leftPath
        $rightHash = Get-FileHashUpper -Path $rightPath
        Write-Host ("Artifact {0}: {1} bytes={2} sha256={3}; {4} bytes={5} sha256={6}" -f $name, $LeftLabel, $leftBytes, $leftHash, $RightLabel, $rightBytes, $rightHash)
        $zipDifferences = @(Compare-ZipEntries -LeftArchive $leftPath -RightArchive $rightPath)
        Write-Host ("ZIP-entry diff {0} vs {1} for {2}: {3}" -f $LeftLabel, $RightLabel, $name, $zipDifferences.Count)
        foreach ($difference in $zipDifferences) {
            Write-Host ("  ZIP DIFF: {0}" -f $difference)
        }
        if ($leftBytes -ne $rightBytes -or $leftHash -ne $rightHash) {
            $leftMetadata = @(Get-ZipEntryMetadata -ArchivePath $leftPath)
            $rightMetadata = @(Get-ZipEntryMetadata -ArchivePath $rightPath)
            $metadataDifferences = @(Compare-Object -ReferenceObject $leftMetadata -DifferenceObject $rightMetadata)
            Write-Host ("ZIP metadata diff {0} vs {1} for {2}: {3}" -f $LeftLabel, $RightLabel, $name, $metadataDifferences.Count)
            foreach ($metadataDifference in $metadataDifferences) {
                Write-Host ("  ZIP METADATA DIFF [{0}]: {1}" -f $metadataDifference.SideIndicator, $metadataDifference.InputObject)
            }
        }
        Assert-That ($zipDifferences.Count -eq 0) "$name has differing ZIP entries between $LeftLabel and $RightLabel."
        Assert-That ($leftBytes -eq $rightBytes -and $leftHash -eq $rightHash) "$name differs between $LeftLabel and $RightLabel."
    }
}

function Build-And-Pack {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$CloneRoot,
        [Parameter(Mandatory = $true)][string]$OutputDirectory
    )

    $solution = Join-Path $CloneRoot 'LogSchema.slnx'
    $config = Join-Path $CloneRoot 'NuGet.config'
    $toolProject = Join-Path $CloneRoot 'src/KeelMatrix.LogSchema/KeelMatrix.LogSchema.csproj'
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    # The reproducibility clones share project references; serialize restore writes so
    # platform-specific filesystem timing cannot race on the generated assets files.
    Invoke-Timed "$Label restore" { dotnet restore $solution --configfile $config --disable-parallel --nologo }
    Invoke-Timed "$Label build" { dotnet build $solution -c Release --no-restore --nologo }
    Invoke-Timed "$Label pack" { dotnet pack $toolProject -c Release --no-build --no-restore --nologo -o $OutputDirectory }
}

$repositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$toolProject = Join-Path $repositoryRoot 'src/KeelMatrix.LogSchema/KeelMatrix.LogSchema.csproj'
$properties = Get-ProjectProperties -Project $toolProject
$packageId = [string]$properties.Properties.PackageId
$packageVersion = [string]$properties.Properties.PackageVersion
$includeSymbols = [string]$properties.Properties.IncludeSymbols -eq 'true'
$expectedNames = @("$packageId.$packageVersion.nupkg")
if ($includeSymbols) {
    $expectedNames += "$packageId.$packageVersion.snupkg"
}

$commit = Get-GitValue -WorkingDirectory $repositoryRoot -Arguments @('rev-parse', 'HEAD') -Description 'the current commit' -Required
$branch = Get-GitValue -WorkingDirectory $repositoryRoot -Arguments @('symbolic-ref', '--short', '-q', 'HEAD') -Description 'the current branch name'
$origin = Get-GitValue -WorkingDirectory $repositoryRoot -Arguments @('remote', 'get-url', 'origin') -Description 'the origin URL' -Required
Assert-That ($commit -match '^[0-9a-fA-F]{40}$') 'canonical checkout must provide a full commit SHA'
$attachedBranch = if ([string]::IsNullOrWhiteSpace($branch)) { 'repro-attached' } else { $branch }
if ([string]::IsNullOrWhiteSpace($branch)) {
    Write-Host "Canonical checkout is detached; using throwaway branch '$attachedBranch' for the attached pack phase."
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('logschema-package-repro-' + [System.Guid]::NewGuid().ToString('N'))
$attachedClone = Join-Path $tempRoot 'attached'
$originClone = Join-Path $tempRoot 'alternate-origin'
$attachedOutput = Join-Path $tempRoot 'attached-package'
$detachedOutput = Join-Path $tempRoot 'detached-package'
$alternateOutput = Join-Path $tempRoot 'alternate-package'

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    Invoke-Timed 'Clone attached fresh checkout' { git clone --no-tags --no-local $repositoryRoot $attachedClone }
    Invoke-Timed 'Attach attached fresh checkout' { git -C $attachedClone checkout -B $attachedBranch $commit }
    Assert-That ((Get-GitValue -WorkingDirectory $attachedClone -Arguments @('rev-parse', 'HEAD') -Description 'the attached clone commit' -Required) -eq $commit) 'attached clone must start at the canonical commit'
    Build-And-Pack -Label 'Attached clone' -CloneRoot $attachedClone -OutputDirectory $attachedOutput

    Invoke-Timed 'Detach attached checkout' { git -C $attachedClone checkout --detach $commit }
    Assert-That ((Get-GitValue -WorkingDirectory $attachedClone -Arguments @('rev-parse', 'HEAD') -Description 'the detached checkout commit' -Required) -eq $commit) 'detached checkout must remain at the canonical commit'
    Build-And-Pack -Label 'Detached checkout' -CloneRoot $attachedClone -OutputDirectory $detachedOutput

    Write-Host "Commit under test: $commit"
    Assert-ArtifactSetsEqual -LeftLabel 'attached' -LeftDirectory $attachedOutput -RightLabel 'detached' -RightDirectory $detachedOutput -ExpectedNames $expectedNames

    Invoke-Timed 'Clone alternate-origin checkout' { git clone --no-tags --no-local $repositoryRoot $originClone }
    Invoke-Timed 'Attach alternate-origin checkout' { git -C $originClone checkout -B $attachedBranch $commit }
    Invoke-Timed 'Set alternate-origin URL' { git -C $originClone remote set-url origin $origin }
    Assert-That ((Get-GitValue -WorkingDirectory $originClone -Arguments @('rev-parse', 'HEAD') -Description 'the alternate-origin clone commit' -Required) -eq $commit) 'alternate-origin clone must start at the canonical commit'
    Build-And-Pack -Label 'Alternate-origin clone' -CloneRoot $originClone -OutputDirectory $alternateOutput
    Assert-ArtifactSetsEqual -LeftLabel 'attached' -LeftDirectory $attachedOutput -RightLabel 'alternate-origin' -RightDirectory $alternateOutput -ExpectedNames $expectedNames
    Write-Host 'Package reproducibility regression passed.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
