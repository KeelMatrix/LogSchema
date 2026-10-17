param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$expectedRepositoryUrl = 'https://github.com/KeelMatrix/LogSchema'
$manifestPath = Join-Path $RepositoryRoot 'keelmatrix.website.json'

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Website metadata validation failed: $Message"
    }
}

function Get-UniqueJsonProperties {
    param(
        [Parameter(Mandatory = $true)][System.Text.Json.JsonElement]$Element,
        [Parameter(Mandatory = $true)][string]$Context
    )

    Assert-That ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) "$Context must be a JSON object"
    $properties = [System.Collections.Generic.Dictionary[string, System.Text.Json.JsonElement]]::new([System.StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        Assert-That (-not $properties.ContainsKey($property.Name)) "$Context contains duplicate property '$($property.Name)'"
        $properties.Add($property.Name, $property.Value)
    }
    return ,$properties
}

$origin = (& git -C $RepositoryRoot remote get-url origin 2>&1 | Out-String).Trim()
Assert-That ($LASTEXITCODE -eq 0) "could not read the origin remote for '$RepositoryRoot': $origin"
$normalizedOrigin = $origin -replace '^git@github\.com:', 'https://github.com/' -replace '^ssh://git@github\.com/', 'https://github.com/' -replace '\.git$', ''
Assert-That ([string]::Equals($normalizedOrigin, $expectedRepositoryUrl, [System.StringComparison]::OrdinalIgnoreCase)) "origin '$origin' must identify $expectedRepositoryUrl"

Assert-That (Test-Path -LiteralPath $manifestPath -PathType Leaf) 'the repository-root keelmatrix.website.json file is required'

$manifestDocument = [System.Text.Json.JsonDocument]::Parse([System.IO.File]::ReadAllText($manifestPath))
try {
    $rootProperties = Get-UniqueJsonProperties -Element $manifestDocument.RootElement -Context 'manifest'
    Assert-That ($rootProperties.Count -eq 2 -and $rootProperties.ContainsKey('schemaVersion') -and $rootProperties.ContainsKey('packages')) 'the manifest must contain only schemaVersion and packages'
    $schemaVersion = $rootProperties['schemaVersion']
    Assert-That ($schemaVersion.ValueKind -eq [System.Text.Json.JsonValueKind]::Number -and $schemaVersion.GetInt32() -eq 1) 'schemaVersion must be 1'

    $manifestPackagesElement = $rootProperties['packages']
    $manifestPackageProperties = Get-UniqueJsonProperties -Element $manifestPackagesElement -Context 'manifest packages'
    $manifestPackages = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
    foreach ($packageProperty in $manifestPackageProperties.GetEnumerator()) {
        $entryProperties = Get-UniqueJsonProperties -Element $packageProperty.Value -Context "manifest package '$($packageProperty.Key)'"
        Assert-That ($entryProperties.Count -eq 2 -and $entryProperties.ContainsKey('visibility') -and $entryProperties.ContainsKey('role')) "manifest package '$($packageProperty.Key)' must contain only visibility and role"
        $visibilityElement = $entryProperties['visibility']
        $roleElement = $entryProperties['role']
        Assert-That ($visibilityElement.ValueKind -eq [System.Text.Json.JsonValueKind]::String) "manifest package '$($packageProperty.Key)' visibility must be a string"
        Assert-That ($roleElement.ValueKind -eq [System.Text.Json.JsonValueKind]::String) "manifest package '$($packageProperty.Key)' role must be a string"
        $visibility = $visibilityElement.GetString()
        $role = $roleElement.GetString()
        Assert-That (@('public-product', 'internal-package') -ccontains $visibility) "manifest package '$($packageProperty.Key)' has an unsupported visibility '$visibility'"
        Assert-That (@('primary', 'component') -ccontains $role) "manifest package '$($packageProperty.Key)' has an unsupported role '$role'"
        $manifestPackages.Add($packageProperty.Key, [pscustomobject]@{ Visibility = $visibility; Role = $role })
    }
}
finally {
    $manifestDocument.Dispose()
}

$visibilitySentinels = @('keelmatrix-public-product', 'keelmatrix-internal-package')
$roleSentinels = @('keelmatrix-primary', 'keelmatrix-component')
$projectFiles = @(Get-ChildItem -LiteralPath $RepositoryRoot -Filter '*.csproj' -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
$packablePackages = [System.Collections.Generic.List[object]]::new()

foreach ($projectFile in $projectFiles) {
    $propertyArguments = @(
        '-getProperty:IsPackable',
        '-getProperty:PackageId',
        '-getProperty:PackageTags',
        '-getProperty:Authors',
        '-getProperty:Description',
        '-getProperty:PackageLicenseExpression',
        '-getProperty:PackageLicenseFile',
        '-getProperty:PackageReadmeFile',
        '-getProperty:PackageProjectUrl',
        '-getProperty:RepositoryUrl',
        '-getProperty:RepositoryType',
        '-getProperty:PublishRepositoryUrl',
        '-getProperty:PackAsTool'
    )
    $propertyOutput = (& dotnet msbuild $projectFile.FullName @propertyArguments -nologo 2>&1 | Out-String)
    Assert-That ($LASTEXITCODE -eq 0) "MSBuild could not evaluate '$($projectFile.FullName)': $propertyOutput"
    try {
        $evaluated = $propertyOutput | ConvertFrom-Json
    }
    catch {
        throw "Website metadata validation failed: MSBuild returned invalid property JSON for '$($projectFile.FullName)'. $($_.Exception.Message)"
    }

    if ([string]$evaluated.Properties.IsPackable -ne 'true') {
        continue
    }

    $properties = $evaluated.Properties
    $packageId = [string]$properties.PackageId
    Assert-That (-not [string]::IsNullOrWhiteSpace($packageId)) "packable project '$($projectFile.FullName)' must define PackageId"
    Assert-That (-not @($packablePackages | Where-Object { [string]::Equals($_.PackageId, $packageId, [System.StringComparison]::Ordinal) }).Count) "PackageId '$packageId' is defined by more than one packable project"
    Assert-That (-not [string]::IsNullOrWhiteSpace([string]$properties.Description)) "package '$packageId' must have a non-empty Description"
    Assert-That ([string]$properties.Authors -eq 'KeelMatrix') "package '$packageId' Authors must be KeelMatrix"
    Assert-That (-not [string]::IsNullOrWhiteSpace([string]$properties.PackageLicenseExpression) -or -not [string]::IsNullOrWhiteSpace([string]$properties.PackageLicenseFile)) "package '$packageId' must declare license metadata"
    Assert-That ([string]$properties.PackageProjectUrl -ceq $expectedRepositoryUrl) "package '$packageId' PackageProjectUrl must be $expectedRepositoryUrl"
    Assert-That ([string]$properties.RepositoryUrl -ceq $expectedRepositoryUrl) "package '$packageId' RepositoryUrl must be $expectedRepositoryUrl"
    Assert-That ([string]$properties.RepositoryType -ceq 'git') "package '$packageId' RepositoryType must be git"
    Assert-That ([string]$properties.PublishRepositoryUrl -eq 'true') "package '$packageId' must publish its repository metadata"
    Assert-That ([string]$properties.PackAsTool -eq 'true') "package '$packageId' must remain a .NET tool"

    $readmeName = [string]$properties.PackageReadmeFile
    Assert-That (-not [string]::IsNullOrWhiteSpace($readmeName)) "package '$packageId' must declare PackageReadmeFile"
    $projectDirectory = [System.IO.Path]::GetFullPath($projectFile.DirectoryName)
    $readmePath = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory $readmeName))
    Assert-That ($readmePath.StartsWith($projectDirectory + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) "package '$packageId' README must stay within its project directory"
    Assert-That (Test-Path -LiteralPath $readmePath -PathType Leaf) "package '$packageId' README does not exist at '$readmeName'"

    $tags = @(([string]$properties.PackageTags) -split '[;\s]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $caseInsensitiveVisibilityTags = @($tags | Where-Object { $visibilitySentinels -icontains $_ })
    $caseInsensitiveRoleTags = @($tags | Where-Object { $roleSentinels -icontains $_ })
    $packageVisibilityTags = @($tags | Where-Object { $visibilitySentinels -ccontains $_ })
    $packageRoleTags = @($tags | Where-Object { $roleSentinels -ccontains $_ })
    Assert-That ($caseInsensitiveVisibilityTags.Count -eq $packageVisibilityTags.Count) "package '$packageId' website visibility sentinels must use exact lowercase spelling"
    Assert-That ($caseInsensitiveRoleTags.Count -eq $packageRoleTags.Count) "package '$packageId' website role sentinels must use exact lowercase spelling"
    Assert-That ($packageVisibilityTags.Count -eq 1) "package '$packageId' must contain exactly one website visibility sentinel"
    Assert-That ($packageRoleTags.Count -eq 1) "package '$packageId' must contain exactly one website role sentinel"
    Assert-That ($manifestPackages.ContainsKey($packageId)) "packable package '$packageId' is missing from keelmatrix.website.json"

    $manifestEntry = $manifestPackages[$packageId]
    $sourceVisibility = if ($packageVisibilityTags[0] -eq 'keelmatrix-public-product') { 'public-product' } else { 'internal-package' }
    $sourceRole = if ($packageRoleTags[0] -eq 'keelmatrix-primary') { 'primary' } else { 'component' }
    Assert-That ($manifestEntry.Visibility -ceq $sourceVisibility) "package '$packageId' source visibility disagrees with keelmatrix.website.json"
    Assert-That ($manifestEntry.Role -ceq $sourceRole) "package '$packageId' source role disagrees with keelmatrix.website.json"

    $packablePackages.Add([pscustomobject]@{
        PackageId = $packageId
        ProjectUrl = [string]$properties.PackageProjectUrl
        Visibility = $sourceVisibility
        Role = $sourceRole
    })
}

Assert-That ($packablePackages.Count -gt 0) 'the repository must contain at least one packable package'
foreach ($manifestPackageId in $manifestPackages.Keys) {
    Assert-That (@($packablePackages | Where-Object { [string]::Equals($_.PackageId, $manifestPackageId, [System.StringComparison]::Ordinal) }).Count -eq 1) "manifest package '$manifestPackageId' does not belong to a packable project in this repository"
}

$families = @($packablePackages | Group-Object -Property ProjectUrl)
foreach ($family in $families) {
    $primaryCount = @($family.Group | Where-Object { $_.Role -eq 'primary' }).Count
    Assert-That ($primaryCount -le 1) "product family '$($family.Name)' has more than one primary package"
    if (@($family.Group | Where-Object { $_.Visibility -eq 'public-product' }).Count -gt 0) {
        Assert-That ($primaryCount -eq 1) "public product family '$($family.Name)' must have exactly one primary package"
    }
}

Write-Host "Website package metadata passed for $($packablePackages.Count) packable package(s): $($packablePackages.PackageId -join ', ')."
