$ErrorActionPreference = 'Stop'

function Read-ShippingManifest {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Shipping manifest was not found: $Path"
    }

    $manifest = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.packageId -ne 'KeelMatrix.LogSchema') {
        throw 'Shipping manifest schema or package identity is invalid.'
    }
    foreach ($property in @('publishEntries', 'packageEntries', 'symbolEntries')) {
        $values = @($manifest.$property)
        if ($values.Count -eq 0 -or $values.Count -ne @($values | Sort-Object -Unique).Count) {
            throw "Shipping manifest $property must be non-empty and duplicate-free."
        }
    }
    $publishAliases = @($manifest.publishAliases)
    if ($publishAliases.Count -eq 0) {
        throw 'Shipping manifest publishAliases must be non-empty.'
    }
    $aliasActuals = @($publishAliases | ForEach-Object { [string]$_.actual })
    $aliasCanonicals = @($publishAliases | ForEach-Object { [string]$_.canonical })
    if ($aliasActuals | Where-Object { [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1) {
        throw 'Shipping manifest publishAliases must contain actual entries.'
    }
    if ($aliasCanonicals | Where-Object { [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1) {
        throw 'Shipping manifest publishAliases must contain canonical entries.'
    }
    if ($aliasActuals.Count -ne @($aliasActuals | Sort-Object -Unique).Count -or
        $aliasCanonicals.Count -ne @($aliasCanonicals | Sort-Object -Unique).Count) {
        throw 'Shipping manifest publishAliases must be duplicate-free.'
    }
    foreach ($canonical in $aliasCanonicals) {
        if (@($manifest.publishEntries) -notcontains $canonical) {
            throw "Shipping manifest publish alias canonical entry is not in publishEntries: $canonical"
        }
    }
    foreach ($project in @('core', 'tool')) {
        $records = @($manifest.dependencies.$project)
        if ($records.Count -eq 0) {
            throw "Shipping manifest dependency graph '$project' must be non-empty."
        }
        $keys = @($records | ForEach-Object { "$(($_.id))/$($_.version)/$($_.type)" })
        if ($keys.Count -ne @($keys | Sort-Object -Unique).Count) {
            throw "Shipping manifest dependency graph '$project' contains duplicates."
        }
    }
    return $manifest
}

function Assert-ShippingExactSet {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Actual,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Expected
    )

    $actualUnique = @($Actual | Sort-Object -Unique)
    $expectedUnique = @($Expected | Sort-Object -Unique)
    $differences = @(Compare-Object -ReferenceObject $expectedUnique -DifferenceObject $actualUnique)
    if ($differences.Count -gt 0) {
        foreach ($difference in $differences) {
            Write-Host ("{0} entry difference [{1}]: {2}" -f $Label, $difference.SideIndicator, $difference.InputObject)
        }
    }
    if ($Actual.Count -ne $actualUnique.Count -or $Expected.Count -ne $expectedUnique.Count -or $differences.Count -ne 0) {
        throw "$Label must match the repository-owned shipping manifest exactly."
    }
}

function Get-ShippingArchiveEntries {
    param([Parameter(Mandatory = $true)][string]$ArchivePath)

    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        return @($archive.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $archive.Dispose()
    }
}

function ConvertTo-ShippingPublishEntries {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Entries
    )

    $aliases = @{}
    foreach ($alias in @($Manifest.publishAliases)) {
        $aliases[[string]$alias.actual] = [string]$alias.canonical
    }
    return @($Entries | ForEach-Object {
            $entry = [string]$_
            if ($aliases.ContainsKey($entry)) { $aliases[$entry] } else { $entry }
        })
}

function Get-ShippingDependencyKeys {
    param([Parameter(Mandatory = $true)][string]$AssetsPath)

    if (-not (Test-Path -LiteralPath $AssetsPath -PathType Leaf)) {
        throw "Restore graph was not found: $AssetsPath"
    }
    $assets = Get-Content -Raw -LiteralPath $AssetsPath | ConvertFrom-Json
    return @($assets.libraries.PSObject.Properties | ForEach-Object {
            $parts = $_.Name -split '/', 2
            "{0}/{1}/{2}" -f $parts[0], $parts[1], [string]$_.Value.type
        })
}

function Get-ShippingManifestDependencyKeys {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][ValidateSet('core', 'tool')][string]$Project
    )

    return @($Manifest.dependencies.$Project | ForEach-Object { "$(($_.id))/$(($_.version))/$(($_.type))" })
}

function Assert-ShippingManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$CoreAssetsPath,
        [Parameter(Mandatory = $true)][string]$ToolAssetsPath,
        [Parameter(Mandatory = $true)][string]$PublishDirectory,
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$SymbolsPath
    )

    $manifest = Read-ShippingManifest -Path $ManifestPath
    $publishEntries = @(Get-ChildItem -LiteralPath $PublishDirectory -File -Recurse | ForEach-Object {
            [IO.Path]::GetRelativePath($PublishDirectory, $_.FullName).Replace('\', '/')
        })
    $publishEntries = @(ConvertTo-ShippingPublishEntries -Manifest $manifest -Entries $publishEntries)
    Assert-ShippingExactSet -Label 'publish directory' -Actual $publishEntries -Expected @($manifest.publishEntries)
    Assert-ShippingExactSet -Label 'tool package' -Actual @(Get-ShippingArchiveEntries -ArchivePath $PackagePath) -Expected @($manifest.packageEntries)
    Assert-ShippingExactSet -Label 'symbols package' -Actual @(Get-ShippingArchiveEntries -ArchivePath $SymbolsPath) -Expected @($manifest.symbolEntries)

    Assert-ShippingExactSet -Label 'Core restore graph' `
        -Actual @(Get-ShippingDependencyKeys -AssetsPath $CoreAssetsPath) `
        -Expected @(Get-ShippingManifestDependencyKeys -Manifest $manifest -Project core)
    Assert-ShippingExactSet -Label 'tool restore graph' `
        -Actual @(Get-ShippingDependencyKeys -AssetsPath $ToolAssetsPath) `
        -Expected @(Get-ShippingManifestDependencyKeys -Manifest $manifest -Project tool)
    Write-Host ("Shipping manifest passed: publish={0}, package={1}, symbols={2}, coreDependencies={3}, toolDependencies={4}" -f `
        $publishEntries.Count, @($manifest.packageEntries).Count, @($manifest.symbolEntries).Count, `
        @($manifest.dependencies.core).Count, @($manifest.dependencies.tool).Count)
}
