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

. (Join-Path $PSScriptRoot 'ShippingManifest.ps1')
$manifest = Read-ShippingManifest -Path (Join-Path $RepositoryRoot 'build/shipping-manifest.json')

function Assert-Rejected {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    try {
        & $Action
    }
    catch {
        Write-Host "Negative control passed: $Label"
        return
    }
    throw "Negative control failed: $Label was accepted."
}

$coreDependencies = @(Get-ShippingManifestDependencyKeys -Manifest $manifest -Project core)
$toolDependencies = @(Get-ShippingManifestDependencyKeys -Manifest $manifest -Project tool)
$publishEntries = @($manifest.publishEntries)
$packageEntries = @($manifest.packageEntries)

Assert-Rejected 'extra direct dependency' {
    Assert-ShippingExactSet -Label 'extra direct dependency' -Actual ($coreDependencies + 'Harmless.Direct/1.0.0/package') -Expected $coreDependencies
}
Assert-Rejected 'extra transitive dependency' {
    Assert-ShippingExactSet -Label 'extra transitive dependency' -Actual ($toolDependencies + 'Harmless.Transitive/1.0.0/package') -Expected $toolDependencies
}
Assert-Rejected 'ordinary content file' {
    Assert-ShippingExactSet -Label 'ordinary content file' -Actual ($packageEntries + 'content/readme.txt') -Expected $packageEntries
}
Assert-Rejected 'OS-specific runtime asset' {
    Assert-ShippingExactSet -Label 'OS-specific runtime asset' -Actual ($packageEntries + 'tools/net8.0/any/runtimes/linux-x64/lib/net8.0/Harmless.dll') -Expected $packageEntries
}
Assert-Rejected 'missing intended file' {
    Assert-ShippingExactSet -Label 'missing intended file' -Actual $packageEntries[1..($packageEntries.Count - 1)] -Expected $packageEntries
}
Assert-Rejected 'unexpected dependency version movement' {
    $moved = @($coreDependencies | ForEach-Object {
            if ($_ -eq 'System.Formats.Asn1/9.0.1/package') { 'System.Formats.Asn1/9.0.2/package' } else { $_ }
        })
    Assert-ShippingExactSet -Label 'unexpected dependency version movement' -Actual $moved -Expected $coreDependencies
}

Write-Host 'Shipping manifest negative controls passed.'
