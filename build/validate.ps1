$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repositoryRoot 'artifacts/validation'
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

function Invoke-Timed {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    & $Action
    $timer.Stop()
    Write-Host ("{0}: {1:N2}s" -f $Label, $timer.Elapsed.TotalSeconds)
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE"
    }
}

$env:KEELMATRIX_NO_TELEMETRY = '1'

Invoke-Timed 'Restore probe' { dotnet restore (Join-Path $repositoryRoot 'phase0/Phase0.LogSchemaProbe.csproj') --configfile (Join-Path $repositoryRoot 'NuGet.config') }
Invoke-Timed 'Restore net8 fixture' { dotnet restore (Join-Path $repositoryRoot 'fixtures/Phase0.Net8/Phase0.Net8.csproj') --configfile (Join-Path $repositoryRoot 'NuGet.config') }
Invoke-Timed 'Restore stable fixture' { dotnet restore (Join-Path $repositoryRoot 'fixtures/Phase0.Stable/Phase0.Stable.csproj') --configfile (Join-Path $repositoryRoot 'NuGet.config') }
Invoke-Timed 'Restore multi-target fixture' { dotnet restore (Join-Path $repositoryRoot 'fixtures/Phase0.Multi/Phase0.Multi.csproj') --configfile (Join-Path $repositoryRoot 'NuGet.config') }
Invoke-Timed 'Build probe' { dotnet build (Join-Path $repositoryRoot 'phase0/Phase0.LogSchemaProbe.csproj') -c Release --no-restore --nologo }

$probe = Join-Path $repositoryRoot 'phase0/bin/Release/net10.0/Phase0.LogSchemaProbe.dll'
$net8Project = Join-Path $repositoryRoot 'fixtures/Phase0.Net8/Phase0.Net8.csproj'
$stableProject = Join-Path $repositoryRoot 'fixtures/Phase0.Stable/Phase0.Stable.csproj'
$multiProject = Join-Path $repositoryRoot 'fixtures/Phase0.Multi/Phase0.Multi.csproj'

Invoke-Timed 'Probe net8' { dotnet $probe $net8Project --output (Join-Path $artifactRoot 'net8.json') --tfm net8.0 }
Invoke-Timed 'Probe stable SDK fixture' { dotnet $probe $stableProject --output (Join-Path $artifactRoot 'stable.json') --tfm net10.0 }
Invoke-Timed 'Probe multi-target net8' { dotnet $probe $multiProject --output (Join-Path $artifactRoot 'multi-net8.json') --tfm net8.0 }
Invoke-Timed 'Probe multi-target net10' { dotnet $probe $multiProject --output (Join-Path $artifactRoot 'multi-net10.json') --tfm net10.0 }

Get-FileHash (Join-Path $artifactRoot 'net8.json') -Algorithm SHA256
Get-FileHash (Join-Path $artifactRoot 'stable.json') -Algorithm SHA256
Get-FileHash (Join-Path $artifactRoot 'multi-net8.json') -Algorithm SHA256
Get-FileHash (Join-Path $artifactRoot 'multi-net10.json') -Algorithm SHA256
