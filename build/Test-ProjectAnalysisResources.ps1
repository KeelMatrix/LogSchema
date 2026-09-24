param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [int]$MaxElapsedSeconds = 120,
    [int]$MaxWorkingSetMiB = 1536,
    [int]$MaxPrivateMemoryMiB = 1024
)

$ErrorActionPreference = 'Stop'

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function New-SolutionFile {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$ProjectPaths
    )

    & dotnet new sln --format sln --name $Name --output $Root --force --no-update-check | Out-Null
    Assert-That ($LASTEXITCODE -eq 0) "could not create $Name.sln"
    $solutionPath = Join-Path $Root "$Name.sln"
    Push-Location $Root
    try {
        & dotnet sln $solutionPath add $ProjectPaths | Out-Null
        Assert-That ($LASTEXITCODE -eq 0) "could not add projects to $solutionPath"
    }
    finally {
        Pop-Location
    }
    return $solutionPath
}

function New-LargeFixture {
    param([Parameter(Mandatory = $true)][string]$Root)

    $projectCount = 24
    $documentsPerProject = 16
    $declarationsPerDocument = 4
    $projectPaths = [Collections.Generic.List[string]]::new()
    $declarationCount = 0

    for ($projectIndex = 1; $projectIndex -le $projectCount; $projectIndex++) {
        $projectName = 'LargeProject{0:D2}' -f $projectIndex
        $projectDirectory = Join-Path $Root $projectName
        $projectPath = Join-Path $projectDirectory "$projectName.csproj"
        $projectPaths.Add("$projectName/$projectName.csproj")

        $references = [Collections.Generic.List[string]]::new()
        if ($projectIndex -gt 1) {
            $referenceProject = 'LargeProject{0:D2}' -f ($projectIndex - 1)
            $references.Add("    <ProjectReference Include=`"../$referenceProject/$referenceProject.csproj`" />")
        }
        if ($projectIndex -gt 2) {
            $referenceProject = 'LargeProject{0:D2}' -f ($projectIndex - 2)
            $references.Add("    <ProjectReference Include=`"../$referenceProject/$referenceProject.csproj`" />")
        }
        $referenceXml = if ($references.Count -gt 0) { "`n  <ItemGroup>`n$($references -join "`n")`n  </ItemGroup>" } else { '' }
        $projectXml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>$projectName</AssemblyName>
    <RootNamespace>LogSchema.ResourceFixture.$projectName</RootNamespace>
    <IsPackable>false</IsPackable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.1" />
  </ItemGroup>$referenceXml
</Project>
"@
        Write-Utf8File -Path $projectPath -Content $projectXml

        for ($documentIndex = 1; $documentIndex -le $documentsPerProject; $documentIndex++) {
            $source = [Collections.Generic.List[string]]::new()
            $source.Add('using Microsoft.Extensions.Logging;')
            $source.Add('')
            $source.Add("namespace LogSchema.ResourceFixture.$projectName;")
            $source.Add('')
            $source.Add(("public static partial class Logging{0:D2}_{1:D2}" -f $projectIndex, $documentIndex))
            $source.Add('{')
            for ($declarationIndex = 1; $declarationIndex -le $declarationsPerDocument; $declarationIndex++) {
                $eventId = (($projectIndex - 1) * $documentsPerProject * $declarationsPerDocument) + (($documentIndex - 1) * $declarationsPerDocument) + $declarationIndex
                $methodName = 'Event{0:D4}' -f $eventId
                $source.Add("    [LoggerMessage(EventId = $eventId, Level = LogLevel.Information, Message = `"Project $projectIndex document $documentIndex event $declarationIndex {Value}`")]")
                $source.Add("    public static partial void $methodName(ILogger logger, int value);")
                $source.Add('')
                $declarationCount++
            }
            $source.Add('}')
            Write-Utf8File -Path (Join-Path $projectDirectory ("Logging{0:D2}_{1:D2}.cs" -f $projectIndex, $documentIndex)) -Content (($source -join "`n") + "`n")
        }
    }

    $solutionPath = New-SolutionFile -Root $Root -Name 'ResourceFixture' -ProjectPaths $projectPaths
    return [pscustomobject]@{
        Solution = $solutionPath
        ProjectCount = $projectCount
        DocumentCount = $projectCount * $documentsPerProject
        DeclarationCount = $declarationCount
    }
}

function New-ProjectCountFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][int]$ProjectCount
    )

    $projectPaths = [Collections.Generic.List[string]]::new()
    for ($projectIndex = 1; $projectIndex -le $ProjectCount; $projectIndex++) {
        $projectName = 'TooManyProjects{0:D3}' -f $projectIndex
        $projectDirectory = Join-Path $Root $projectName
        $projectPath = Join-Path $projectDirectory "$projectName.csproj"
        $projectPaths.Add("$projectName/$projectName.csproj")
        Write-Utf8File -Path $projectPath -Content @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
"@
        Write-Utf8File -Path (Join-Path $projectDirectory 'Empty.cs') -Content "public sealed class Empty$projectIndex { }`n"
    }

    $solutionPath = New-SolutionFile -Root $Root -Name 'TooManyProjects' -ProjectPaths $projectPaths
    return $solutionPath
}

function New-DocumentCountFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][int]$DocumentCount
    )

    $projectDirectory = Join-Path $Root 'TooManyDocuments'
    $projectPath = Join-Path $projectDirectory 'TooManyDocuments.csproj'
    Write-Utf8File -Path $projectPath -Content @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
"@
    for ($documentIndex = 1; $documentIndex -le $DocumentCount; $documentIndex++) {
        Write-Utf8File -Path (Join-Path $projectDirectory ("Document{0:D4}.cs" -f $documentIndex)) -Content "public sealed class Document$documentIndex { }`n"
    }

    return $projectPath
}

function Restore-Fixture {
    param(
        [Parameter(Mandatory = $true)][string]$SolutionOrProject,
        [Parameter(Mandatory = $true)][string]$NuGetConfig
    )

    & dotnet restore $SolutionOrProject --configfile $NuGetConfig --disable-parallel --nologo
    Assert-That ($LASTEXITCODE -eq 0) "controlled restore failed for $SolutionOrProject"
}

function Invoke-TrackedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $process = [Diagnostics.Process]::new()
    $process.StartInfo.FileName = $FileName
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$process.StartInfo.ArgumentList.Add($argument) }

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    Assert-That $process.Start() "could not start $FileName"
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $peakWorkingSet = 0L
    $peakPrivateMemory = 0L
    $timedOut = $false
    while (-not $process.HasExited) {
        $process.Refresh()
        $peakWorkingSet = [Math]::Max($peakWorkingSet, $process.WorkingSet64)
        $peakPrivateMemory = [Math]::Max($peakPrivateMemory, $process.PrivateMemorySize64)
        if ($stopwatch.Elapsed.TotalSeconds -gt $TimeoutSeconds) {
            $timedOut = $true
            try { $process.Kill($true) } catch { $process.Kill() }
            break
        }
        Start-Sleep -Milliseconds 100
    }
    $process.WaitForExit()
    $process.Refresh()
    $peakWorkingSet = [Math]::Max($peakWorkingSet, $process.WorkingSet64)
    $peakPrivateMemory = [Math]::Max($peakPrivateMemory, $process.PrivateMemorySize64)
    $stopwatch.Stop()

    return [pscustomobject]@{
        ExitCode = if ($timedOut) { 124 } else { $process.ExitCode }
        TimedOut = $timedOut
        ElapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
        PeakWorkingSetMiB = [Math]::Round($peakWorkingSet / 1MB, 1)
        PeakPrivateMemoryMiB = [Math]::Round($peakPrivateMemory / 1MB, 1)
        Stdout = $stdoutTask.GetAwaiter().GetResult()
        Stderr = $stderrTask.GetAwaiter().GetResult()
    }
}

function Assert-ResourceBudget {
    param([Parameter(Mandatory = $true)]$Result)

    Assert-That (-not $Result.TimedOut) "large fixture exceeded the $MaxElapsedSeconds second process limit"
    Assert-That ($Result.ExitCode -eq 0) "large fixture capture failed: $($Result.Stderr) $($Result.Stdout)"
    if ($MaxElapsedSeconds -gt 0) { Assert-That ($Result.ElapsedSeconds -le $MaxElapsedSeconds) "large fixture elapsed time exceeded $MaxElapsedSeconds seconds" }
    if ($MaxWorkingSetMiB -gt 0) { Assert-That ($Result.PeakWorkingSetMiB -le $MaxWorkingSetMiB) "large fixture working set exceeded $MaxWorkingSetMiB MiB" }
    if ($MaxPrivateMemoryMiB -gt 0) { Assert-That ($Result.PeakPrivateMemoryMiB -le $MaxPrivateMemoryMiB) "large fixture private memory exceeded $MaxPrivateMemoryMiB MiB" }
}

$repositoryRoot = (Resolve-Path $RepositoryRoot).Path
$artifactRoot = Join-Path $repositoryRoot 'artifacts/validation/project-analysis-resources'
if (Test-Path -LiteralPath $artifactRoot) { Remove-Item -LiteralPath $artifactRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
$nugetConfig = Join-Path $repositoryRoot 'NuGet.config'
$tool = Join-Path $repositoryRoot 'src/KeelMatrix.LogSchema/bin/Release/net8.0/KeelMatrix.LogSchema.dll'
Assert-That (Test-Path -LiteralPath $tool) "Release shipping tool was not found: $tool"

$largeRoot = Join-Path $artifactRoot 'large-solution'
$largeFixture = New-LargeFixture -Root $largeRoot
Restore-Fixture -SolutionOrProject $largeFixture.Solution -NuGetConfig $nugetConfig
$largeManifest = Join-Path $largeRoot 'logschema.json'
$largeResult = Invoke-TrackedProcess -FileName 'dotnet' -Arguments @($tool, 'capture', $largeFixture.Solution, '--tfm', 'net8.0', '--output', $largeManifest, '--format', 'json', '--no-telemetry') -TimeoutSeconds $(if ($MaxElapsedSeconds -gt 0) { $MaxElapsedSeconds } else { 600 })
Assert-ResourceBudget -Result $largeResult
Assert-That (Test-Path -LiteralPath $largeManifest) 'large fixture capture must write a manifest'
$largeEnvelope = $largeResult.Stdout | ConvertFrom-Json
Assert-That ([int]$largeEnvelope.eventCount -eq $largeFixture.DeclarationCount) "large fixture must capture $($largeFixture.DeclarationCount) declarations"
Write-Host ("Large project-analysis fixture: projects={0} documents={1} declarations={2} elapsed={3:N2}s peakWorkingSet={4:N1}MiB peakPrivateMemory={5:N1}MiB" -f $largeFixture.ProjectCount, $largeFixture.DocumentCount, $largeFixture.DeclarationCount, $largeResult.ElapsedSeconds, $largeResult.PeakWorkingSetMiB, $largeResult.PeakPrivateMemoryMiB)

$tooManyProjectRoot = Join-Path $artifactRoot 'too-many-projects'
$tooManyProjects = New-ProjectCountFixture -Root $tooManyProjectRoot -ProjectCount 65
Restore-Fixture -SolutionOrProject $tooManyProjects -NuGetConfig $nugetConfig
$tooManyProjectResult = Invoke-TrackedProcess -FileName 'dotnet' -Arguments @($tool, 'capture', $tooManyProjects, '--format', 'json', '--no-telemetry') -TimeoutSeconds 120
Assert-That (-not $tooManyProjectResult.TimedOut) 'too-many-projects regression must fail without timing out'
Assert-That ($tooManyProjectResult.ExitCode -eq 3) 'too-many-projects regression must return analysis failure exit 3'
Assert-That (($tooManyProjectResult.Stdout + $tooManyProjectResult.Stderr) -match 'Project analysis resource limit exceeded') 'too-many-projects regression must report a clear resource-limit error'
Write-Host ("Pathological project graph: exit={0} elapsed={1:N2}s peakWorkingSet={2:N1}MiB" -f $tooManyProjectResult.ExitCode, $tooManyProjectResult.ElapsedSeconds, $tooManyProjectResult.PeakWorkingSetMiB)

$tooManyDocumentRoot = Join-Path $artifactRoot 'too-many-documents'
$tooManyDocuments = New-DocumentCountFixture -Root $tooManyDocumentRoot -DocumentCount 513
Restore-Fixture -SolutionOrProject $tooManyDocuments -NuGetConfig $nugetConfig
$tooManyDocumentResult = Invoke-TrackedProcess -FileName 'dotnet' -Arguments @($tool, 'capture', $tooManyDocuments, '--format', 'json', '--no-telemetry') -TimeoutSeconds 120
Assert-That (-not $tooManyDocumentResult.TimedOut) 'too-many-documents regression must fail without timing out'
Assert-That ($tooManyDocumentResult.ExitCode -eq 3) 'too-many-documents regression must return analysis failure exit 3'
Assert-That (($tooManyDocumentResult.Stdout + $tooManyDocumentResult.Stderr) -match 'Project analysis resource limit exceeded') 'too-many-documents regression must report a clear resource-limit error'
Write-Host ("Pathological source graph: exit={0} elapsed={1:N2}s peakWorkingSet={2:N1}MiB" -f $tooManyDocumentResult.ExitCode, $tooManyDocumentResult.ElapsedSeconds, $tooManyDocumentResult.PeakWorkingSetMiB)

Write-Host 'Project-analysis resource and bounded-failure gate passed.'
