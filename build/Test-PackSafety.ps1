$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$testRoot = Join-Path $repositoryRoot 'artifacts/validation/pack-safety-tests'
$projectPath = Join-Path $testRoot 'PackSafety.csproj'
$outputPath = Join-Path $testRoot 'package'

if (Test-Path -LiteralPath $testRoot) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $testRoot, $outputPath | Out-Null

function Write-FixtureProject {
    param([Parameter(Mandatory = $true)][string]$ItemPath)

    $escapedPath = [Security.SecurityElement]::Escape($ItemPath)
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>true</IsPackable>
    <EnableDefaultItems>false</EnableDefaultItems>
    <PackageId>PackSafetyFixture</PackageId>
    <Version>1.0.0</Version>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Empty.cs" />
    <None Include="$escapedPath" Pack="true" PackagePath="" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $projectPath -Encoding utf8NoBOM
}

'internal sealed class Empty { }' | Set-Content -LiteralPath (Join-Path $testRoot 'Empty.cs') -Encoding utf8NoBOM
'safe package content' | Set-Content -LiteralPath (Join-Path $testRoot 'README.md') -Encoding utf8NoBOM
Write-FixtureProject -ItemPath 'README.md'
dotnet restore $projectPath --configfile (Join-Path $repositoryRoot 'NuGet.config') --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Pack-safety fixture restore failed with exit code $LASTEXITCODE."
}
dotnet pack $projectPath -c Release --no-restore --nologo -o $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "A safe explicit Pack=true item should be accepted."
}

$cases = @(
    [pscustomobject]@{ Payload = '.env.local'; Include = '.env.local' },
    [pscustomobject]@{ Payload = 'configuration/telemetry.local.json'; Include = 'configuration/**/*.json' },
    [pscustomobject]@{ Payload = 'client-secret.json'; Include = 'client-secret.json' },
    [pscustomobject]@{ Payload = 'review-evidence/internal-notes.md'; Include = 'review-evidence/internal-notes.md' }
)
foreach ($case in $cases) {
    $relativePath = $case.Payload
    $payloadPath = Join-Path $testRoot $relativePath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $payloadPath) | Out-Null
    'synthetic sensitive content' | Set-Content -LiteralPath $payloadPath -Encoding utf8NoBOM
    Write-FixtureProject -ItemPath $case.Include

    $global:LASTEXITCODE = 0
    $output = (& dotnet pack $projectPath -c Release --no-restore --nologo -o $outputPath 2>&1 | Out-String)
    $exitCode = $LASTEXITCODE
    Write-Host "Pack-safety case '$relativePath' via '$($case.Include)': exit=$exitCode"
    if ($exitCode -eq 0) {
        throw "Sensitive Pack=true item '$relativePath' should be rejected."
    }
    if ($output -notmatch 'Sensitive package input is not allowed') {
        throw "Sensitive Pack=true item '$relativePath' failed for the wrong reason. Output: $output"
    }
}

Write-Host 'Pack-safety regression tests passed.'
