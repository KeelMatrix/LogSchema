param(
    [Parameter(Mandatory = $true)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Version,
    [string]$ExpectedReleaseDate = [DateTime]::UtcNow.ToString('yyyy-MM-dd'),
    [string]$ChangelogPath = (Join-Path $PSScriptRoot '../CHANGELOG.md'),
    [string]$ProjectPath = (Join-Path $PSScriptRoot '../src/KeelMatrix.LogSchema/KeelMatrix.LogSchema.csproj')
)

$ErrorActionPreference = 'Stop'

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Release contract failed: $Message"
    }
}

Assert-That (Test-Path -LiteralPath $ChangelogPath -PathType Leaf) "changelog not found: $ChangelogPath"
Assert-That (Test-Path -LiteralPath $ProjectPath -PathType Leaf) "package project not found: $ProjectPath"

$parsedDate = [DateTime]::MinValue
Assert-That ([DateTime]::TryParseExact(
        $ExpectedReleaseDate,
        'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None,
        [ref]$parsedDate)) "expected release date '$ExpectedReleaseDate' must use YYYY-MM-DD"

$propertyOutput = (& dotnet msbuild $ProjectPath -getProperty:PackageVersion -nologo | Out-String)
Assert-That ($LASTEXITCODE -eq 0) "could not read PackageVersion from $ProjectPath"
$packageVersion = $propertyOutput.Trim()
Assert-That (-not [string]::IsNullOrWhiteSpace($packageVersion)) "PackageVersion from $ProjectPath was empty"
Assert-That ($packageVersion -eq $Version) "package version '$packageVersion' does not match release version '$Version'"

$changelog = Get-Content -LiteralPath $ChangelogPath -Raw
$sectionPattern = '(?ms)^## \[(?<version>[^\]]+)\](?: - (?<date>[^\r\n]+))?\r?\n(?<body>.*?)(?=^## \[|\z)'
$sections = @([regex]::Matches($changelog, $sectionPattern))
$releaseSections = @($sections | Where-Object { $_.Groups['version'].Value -eq $Version })
Assert-That ($releaseSections.Count -eq 1) "CHANGELOG.md must contain exactly one finalized [$Version] section"

$releaseSection = $releaseSections[0]
$releaseDate = $releaseSection.Groups['date'].Value.Trim()
Assert-That ($releaseDate -eq $ExpectedReleaseDate) "release [$Version] date '$releaseDate' does not match '$ExpectedReleaseDate'"

$releaseBody = $releaseSection.Groups['body'].Value
Assert-That ($releaseBody -notmatch '(?i)\b(?:planned|unreleased|tbd)\b|not\s+yet\s+published') "release [$Version] is still described as planned or unpublished"
$headings = @([regex]::Matches($releaseBody, '(?m)^### (?<heading>[^\r\n]+)$') | ForEach-Object { $_.Groups['heading'].Value })
Assert-That ($headings.Count -eq 1 -and $headings[0] -eq 'Added') "the first release [$Version] must contain only an Added section"
Assert-That ($releaseBody -notmatch '(?i)\b(?:now|previously|formerly|fixed|fixes|corrected|resolved|addressed)\b|\bused\s+to\b|\bno\s+longer\b|\bthis\s+(?:removes|fixes)\b|\bchanged\s+from\b') "release [$Version] contains historical-change wording"

$unreleasedSections = @($sections | Where-Object { $_.Groups['version'].Value -eq 'Unreleased' })
Assert-That ($unreleasedSections.Count -eq 1) 'CHANGELOG.md must contain exactly one [Unreleased] section'
$unreleasedBody = $unreleasedSections[0].Groups['body'].Value
Assert-That ($unreleasedBody -notmatch '(?m)^\s*(?:### |[-*] )') 'release notes remain under [Unreleased]'

Write-Host "Release contract passed: version=$Version date=$ExpectedReleaseDate package=$packageVersion"
