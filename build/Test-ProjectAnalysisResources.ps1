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

function New-PaddedSourceFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int64]$ByteCount,
        [Parameter(Mandatory = $true)][string]$Prefix
    )

    $encoding = [Text.UTF8Encoding]::new($false)
    $fixedCommentBytes = $encoding.GetByteCount('/*') + $encoding.GetByteCount('*/')
    $paddingLength = $ByteCount - $encoding.GetByteCount($Prefix) - $fixedCommentBytes
    Assert-That ($paddingLength -ge 0) "cannot create a $ByteCount-byte source file from the requested prefix"
    Write-Utf8File -Path $Path -Content ($Prefix + '/*' + [string]::new('x', [int]$paddingLength) + '*/')
    Assert-That ((Get-Item -LiteralPath $Path).Length -eq $ByteCount) "source fixture was not exactly $ByteCount bytes"
}

function New-SourceByteFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][int64]$ByteCount
    )

    $projectDirectory = Join-Path $Root 'SourceBytes'
    $projectPath = Join-Path $projectDirectory 'SourceBytes.csproj'
    Write-Utf8File -Path $projectPath -Content @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.1" />
  </ItemGroup>
</Project>
"@
    $prefix = @"
using Microsoft.Extensions.Logging;
public static partial class SourceBytesLogging
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Source bytes {Value}")]
    public static partial void Event(ILogger logger, int value);
}
"@
    New-PaddedSourceFile -Path (Join-Path $projectDirectory 'Logging.cs') -ByteCount $ByteCount -Prefix $prefix
    return $projectPath
}

function New-SourceTotalFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][int]$ProjectCount,
        [Parameter(Mandatory = $true)][int]$DocumentsPerProject,
        [Parameter(Mandatory = $true)][int64]$BytesPerDocument
    )

    $projectPaths = [Collections.Generic.List[string]]::new()
    for ($projectIndex = 1; $projectIndex -le $ProjectCount; $projectIndex++) {
        $projectName = 'SourceTotal{0:D2}' -f $projectIndex
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
        for ($documentIndex = 1; $documentIndex -le $DocumentsPerProject; $documentIndex++) {
            $prefix = "namespace SourceTotal.$projectName;`npublic sealed class Document${projectIndex}_${documentIndex} { }`n"
            New-PaddedSourceFile -Path (Join-Path $projectDirectory ("Document{0:D2}.cs" -f $documentIndex)) -ByteCount $BytesPerDocument -Prefix $prefix
        }
    }

    return New-SolutionFile -Root $Root -Name 'SourceTotal' -ProjectPaths $projectPaths
}

function New-GeneratedTreeFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$GeneratorProject,
        [Parameter(Mandatory = $true)][int]$TreeCount,
        [Parameter(Mandatory = $true)][int]$TreeSize
    )

    $projectDirectory = Join-Path $Root 'GeneratedTrees'
    $projectPath = Join-Path $projectDirectory 'GeneratedTrees.csproj'
    Write-Utf8File -Path $projectPath -Content @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <LogSchemaGeneratedTreeCount>$TreeCount</LogSchemaGeneratedTreeCount>
    <LogSchemaGeneratedTreeSize>$TreeSize</LogSchemaGeneratedTreeSize>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.1" />
    <ProjectReference Include="$GeneratorProject" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <CompilerVisibleProperty Include="LogSchemaGeneratedTreeCount" />
    <CompilerVisibleProperty Include="LogSchemaGeneratedTreeSize" />
  </ItemGroup>
</Project>
"@
    Write-Utf8File -Path (Join-Path $projectDirectory 'Logging.cs') -Content @"
using Microsoft.Extensions.Logging;
public static partial class GeneratedTreesLogging
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Generated trees {Value}")]
    public static partial void Event(ILogger logger, int value);
}
"@
    return $projectPath
}

function New-GeneratedTreeSolutionFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$GeneratorProject,
        [Parameter(Mandatory = $true)][int]$TreeCountPerProject
    )

    $projectPaths = [Collections.Generic.List[string]]::new()
    $generatorDirectory = Split-Path -Parent $GeneratorProject
    $generatorName = [IO.Path]::GetFileNameWithoutExtension($GeneratorProject)
    $generatorDll = Join-Path $generatorDirectory "bin/Debug/netstandard2.0/$generatorName.dll"
    for ($projectIndex = 1; $projectIndex -le 2; $projectIndex++) {
        $projectName = 'GeneratedTrees{0:D2}' -f $projectIndex
        $projectDirectory = Join-Path $Root $projectName
        $projectPath = Join-Path $projectDirectory "$projectName.csproj"
        $projectPaths.Add("$projectName/$projectName.csproj")
        Write-Utf8File -Path $projectPath -Content @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>$projectName</AssemblyName>
    <IsPackable>false</IsPackable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <LogSchemaGeneratedTreeCount>$TreeCountPerProject</LogSchemaGeneratedTreeCount>
    <LogSchemaGeneratedTreeSize>0</LogSchemaGeneratedTreeSize>
    <LogSchemaGeneratedTreeDeclarations>none</LogSchemaGeneratedTreeDeclarations>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.1" />
    <Analyzer Include="$generatorDll" />
    <CompilerVisibleProperty Include="LogSchemaGeneratedTreeCount" />
    <CompilerVisibleProperty Include="LogSchemaGeneratedTreeSize" />
    <CompilerVisibleProperty Include="LogSchemaGeneratedTreeDeclarations" />
  </ItemGroup>
</Project>
"@
        Write-Utf8File -Path (Join-Path $projectDirectory 'Logging.cs') -Content @"
using Microsoft.Extensions.Logging;
public static partial class GeneratedTreesLogging$projectIndex
{
    [LoggerMessage(EventId = $projectIndex, Level = LogLevel.Information, Message = "Generated trees {Value}")]
    public static partial void Event(ILogger logger, int value);
}
"@
    }

    $forward = New-SolutionFile -Root $Root -Name 'GeneratedTrees' -ProjectPaths $projectPaths.ToArray()
    $reverse = New-SolutionFile -Root $Root -Name 'GeneratedTreesReversed' -ProjectPaths @($projectPaths[1], $projectPaths[0])
    return [pscustomobject]@{
        Forward = $forward
        Reverse = $reverse
    }
}

function New-DeclarationCountFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][int]$DeclarationCount
    )

    $projectDirectory = Join-Path $Root 'Declarations'
    $projectPath = Join-Path $projectDirectory 'Declarations.csproj'
    Write-Utf8File -Path $projectPath -Content @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.1" />
  </ItemGroup>
</Project>
"@
    $source = [Collections.Generic.List[string]]::new()
    $source.Add('using Microsoft.Extensions.Logging;')
    $source.Add('public static partial class DeclarationLogging {')
    $source.Add('    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Supported {Value}")]')
    $source.Add('    public static partial void Supported(ILogger logger, int value);')
    for ($index = 1; $index -lt $DeclarationCount; $index++) {
        $source.Add("    [LoggerMessage(EventId = $($index + 1), Level = LogLevel.Information, Message = `"Unsupported $index`")]")
        $source.Add("    public static void Unsupported$index(ILogger logger) { }")
    }
    $source.Add('}')
    Write-Utf8File -Path (Join-Path $projectDirectory 'Logging.cs') -Content (($source -join "`n") + "`n")
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

function Assert-BoundedAnalysisFailure {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$ResourceText
    )

    $output = $Result.Stdout + $Result.Stderr
    Assert-That (-not $Result.TimedOut) "$Label must fail without timing out"
    Assert-That ($Result.ExitCode -eq 3) "$Label must return analysis failure exit 3"
    Assert-That ($output -match 'Project analysis resource limit exceeded') "$Label must report the shared resource-limit family"
    Assert-That ($output -match [regex]::Escape($ResourceText)) "$Label must identify the exceeded $ResourceText ceiling"
    Assert-That ($output -match 'ANALYSIS ERROR') "$Label must use the analysis-error diagnostic envelope"
    Assert-That ($output -notmatch '(?m)^\s*at\s+') "$Label must not print a stack trace"
    Assert-That ($output -notmatch [regex]::Escape($repositoryRoot)) "$Label must not print a machine path"
    Write-Host ("{0}: exit={1} elapsed={2:N2}s peakWorkingSet={3:N1}MiB" -f $Label, $Result.ExitCode, $Result.ElapsedSeconds, $Result.PeakWorkingSetMiB)
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
$tooManyProjectResult = Invoke-TrackedProcess -FileName 'dotnet' -Arguments @($tool, 'capture', $tooManyProjects, '--output', (Join-Path $tooManyProjectRoot 'manifest.json'), '--no-telemetry') -TimeoutSeconds 120
Assert-BoundedAnalysisFailure -Result $tooManyProjectResult -Label '65-project preflight' -ResourceText 'C# projects'

$tooManyDocumentRoot = Join-Path $artifactRoot 'too-many-documents'
$tooManyDocuments = New-DocumentCountFixture -Root $tooManyDocumentRoot -DocumentCount 513
Restore-Fixture -SolutionOrProject $tooManyDocuments -NuGetConfig $nugetConfig
$tooManyDocumentResult = Invoke-TrackedProcess -FileName 'dotnet' -Arguments @($tool, 'capture', $tooManyDocuments, '--output', (Join-Path $tooManyDocumentRoot 'manifest.json'), '--no-telemetry') -TimeoutSeconds 120
Assert-BoundedAnalysisFailure -Result $tooManyDocumentResult -Label '513-document preflight' -ResourceText 'source documents in one project'

$oneMiB = 1MB
$sourceByteRoot = Join-Path $artifactRoot 'too-large-source-document'
$tooLargeSource = New-SourceByteFixture -Root $sourceByteRoot -ByteCount ($oneMiB + 1)
Restore-Fixture -SolutionOrProject $tooLargeSource -NuGetConfig $nugetConfig
$tooLargeSourceResult = Invoke-TrackedProcess -FileName 'dotnet' -Arguments @($tool, 'capture', $tooLargeSource, '--output', (Join-Path $sourceByteRoot 'manifest.json'), '--no-telemetry') -TimeoutSeconds 120
Assert-BoundedAnalysisFailure -Result $tooLargeSourceResult -Label 'oversized source document (N+1)' -ResourceText 'source bytes in one document'

$sourceTotalRoot = Join-Path $artifactRoot 'too-large-source-total'
$sourceTotal = New-SourceTotalFixture -Root $sourceTotalRoot -ProjectCount 5 -DocumentsPerProject 13 -BytesPerDocument $oneMiB
Restore-Fixture -SolutionOrProject $sourceTotal -NuGetConfig $nugetConfig
$sourceTotalResult = Invoke-TrackedProcess -FileName 'dotnet' -Arguments @($tool, 'capture', $sourceTotal, '--output', (Join-Path $sourceTotalRoot 'manifest.json'), '--no-telemetry') -TimeoutSeconds 120
Assert-BoundedAnalysisFailure -Result $sourceTotalResult -Label 'source-byte total overage' -ResourceText 'source bytes across the input'

$generatorProject = Join-Path $repositoryRoot 'fixtures/Phase0.GeneratedDeclarationGenerator/Phase0.GeneratedDeclarationGenerator.csproj'
& dotnet build $generatorProject -c Debug --no-restore --nologo | Out-Null
Assert-That ($LASTEXITCODE -eq 0) 'the generated-tree resource fixture generator must build'

$generatedTreeRoot = Join-Path $artifactRoot 'too-many-generated-trees'
$tooManyGeneratedTrees = New-GeneratedTreeFixture -Root $generatedTreeRoot -GeneratorProject $generatorProject -TreeCount 4097 -TreeSize 0
Restore-Fixture -SolutionOrProject $tooManyGeneratedTrees -NuGetConfig $nugetConfig
$tooManyGeneratedTreesResult = Invoke-TrackedProcess -FileName 'dotnet' -Arguments @($tool, 'capture', $tooManyGeneratedTrees, '--output', (Join-Path $generatedTreeRoot 'manifest.json'), '--no-telemetry') -TimeoutSeconds 120
Assert-BoundedAnalysisFailure -Result $tooManyGeneratedTreesResult -Label 'generated-tree count (N+1)' -ResourceText 'generated syntax trees in one project'

$perProjectGeneratedTreeRoot = Join-Path $artifactRoot 'per-project-generated-trees'
$perProjectGeneratedTrees = New-GeneratedTreeSolutionFixture -Root $perProjectGeneratedTreeRoot -GeneratorProject $generatorProject -TreeCountPerProject 2049
Restore-Fixture -SolutionOrProject $perProjectGeneratedTrees.Forward -NuGetConfig $nugetConfig
Restore-Fixture -SolutionOrProject $perProjectGeneratedTrees.Reverse -NuGetConfig $nugetConfig
$forwardManifest = Join-Path $perProjectGeneratedTreeRoot 'forward.json'
$reverseManifest = Join-Path $perProjectGeneratedTreeRoot 'reverse.json'
$forwardResult = Invoke-TrackedProcess -FileName 'dotnet' -Arguments @($tool, 'capture', $perProjectGeneratedTrees.Forward, '--tfm', 'net8.0', '--output', $forwardManifest, '--format', 'json', '--no-telemetry') -TimeoutSeconds 120
$reverseResult = Invoke-TrackedProcess -FileName 'dotnet' -Arguments @($tool, 'capture', $perProjectGeneratedTrees.Reverse, '--tfm', 'net8.0', '--output', $reverseManifest, '--format', 'json', '--no-telemetry') -TimeoutSeconds 120
Assert-That ($forwardResult.ExitCode -eq 0) "two-project generated-tree fixture failed: $($forwardResult.Stderr) $($forwardResult.Stdout)"
Assert-That ($reverseResult.ExitCode -eq 0) "reversed two-project generated-tree fixture failed: $($reverseResult.Stderr) $($reverseResult.Stdout)"
Assert-That ((Get-FileHash -LiteralPath $forwardManifest -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $reverseManifest -Algorithm SHA256).Hash) 'reordering projects must preserve the canonical manifest'
Write-Host ("Per-project generated-tree lifecycle: projects=2 treesPerProject=2049 totalTrees=4098 forward={0:N2}s reverse={1:N2}s" -f $forwardResult.ElapsedSeconds, $reverseResult.ElapsedSeconds)

$largeGeneratedTreeRoot = Join-Path $artifactRoot 'too-large-generated-tree'
$largeGeneratedTree = New-GeneratedTreeFixture -Root $largeGeneratedTreeRoot -GeneratorProject $generatorProject -TreeCount 1 -TreeSize (4MB + 1)
Restore-Fixture -SolutionOrProject $largeGeneratedTree -NuGetConfig $nugetConfig
$largeGeneratedTreeResult = Invoke-TrackedProcess -FileName 'dotnet' -Arguments @($tool, 'capture', $largeGeneratedTree, '--output', (Join-Path $largeGeneratedTreeRoot 'manifest.json'), '--no-telemetry') -TimeoutSeconds 120
Assert-BoundedAnalysisFailure -Result $largeGeneratedTreeResult -Label 'generated-tree text (N+1)' -ResourceText 'generated source bytes in one syntax tree'

$declarationRoot = Join-Path $artifactRoot 'too-many-declarations'
$tooManyDeclarations = New-DeclarationCountFixture -Root $declarationRoot -DeclarationCount 4097
Restore-Fixture -SolutionOrProject $tooManyDeclarations -NuGetConfig $nugetConfig
$tooManyDeclarationsResult = Invoke-TrackedProcess -FileName 'dotnet' -Arguments @($tool, 'capture', $tooManyDeclarations, '--output', (Join-Path $declarationRoot 'manifest.json'), '--no-telemetry') -TimeoutSeconds 120
Assert-BoundedAnalysisFailure -Result $tooManyDeclarationsResult -Label 'LoggerMessage declarations (N+1)' -ResourceText 'discovered LoggerMessage declarations'

Write-Host 'Project-analysis resource and bounded-failure gate passed.'
