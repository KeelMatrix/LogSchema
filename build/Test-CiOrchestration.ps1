$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$workflowPath = Join-Path $repositoryRoot '.github/workflows/ci.yml'
$validatorPath = Join-Path $repositoryRoot 'build/validate.ps1'
$workflow = [IO.File]::ReadAllText($workflowPath)

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

foreach ($runner in @('ubuntu-latest', 'windows-latest', 'macos-latest')) {
    Assert-That ($workflow -match "(?m)^\s+-\s+$([regex]::Escape($runner))\s*$") "the CI matrix must retain $runner"
}
Assert-That ($workflow -match '(?m)^\s+name:\s+Validate \(\$\{\{ matrix\.os \}\}\)\s*$') 'the existing Validate matrix job must remain required'
Assert-That ($workflow -match '(?m)^\s*uses:\s+actions/cache@v6\s*$') 'the NuGet package cache must remain enabled'
Assert-That ($workflow -match '(?m)^\s+path:\s+\.nuget/packages\s*$') 'the cache must cover the restored NuGet packages'

$runCommands = @([regex]::Matches($workflow, '(?m)^\s*run:\s*(.+?)\s*$') | ForEach-Object { $_.Groups[1].Value })
Assert-That (($runCommands | Where-Object { $_ -eq './build/Test-CiOrchestration.ps1' }).Count -eq 1) 'CI must run the orchestration regression once'
Assert-That (($runCommands | Where-Object { $_ -eq './build/validate.ps1' }).Count -eq 1) 'CI must run the repository validator once'
Assert-That ($runCommands.Count -eq 2) 'CI must contain only the focused orchestration check and the single repository validation command'
Assert-That ($workflow -notmatch '(?im)^\s*run:\s*dotnet\s+(restore|build|test)\b') 'CI must not repeat restore, build, or tests outside the repository validator'
Assert-That ($workflow -notmatch '(?im)^\s*run:\s*.*Test-HistoryHygiene\.ps1') 'CI must not repeat history hygiene outside the repository validator'
Assert-That ($workflow -notmatch '(?im)^\s*continue-on-error:\s*true\b|\|\|\s*true\b|(?m)^\s*if:\s*always\(\)\s*$') 'validation failures must remain workflow failures'
Assert-That ($workflow -match '(?s)Test CI orchestration and failure propagation.*?run:\s*\./build/Test-CiOrchestration\.ps1.*?Run repository validation.*?run:\s*\./build/validate\.ps1') 'the regression check must run before the final validator'

$tempParent = if (-not [string]::IsNullOrWhiteSpace($env:PAPERCLIP_SCRATCH_DIR)) {
    $env:PAPERCLIP_SCRATCH_DIR
}
elseif (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    $env:RUNNER_TEMP
}
else {
    [IO.Path]::GetTempPath()
}
$probeRoot = Join-Path $tempParent "logschema-ci-orchestration-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $probeRoot
$pathBeforeProbe = $env:PATH
$realDotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1
$realDotnetPath = $realDotnetCommand.Source
$solutionPath = Join-Path $repositoryRoot 'LogSchema.slnx'
$realDotnetEnvironmentVariable = 'LOGSCHEMA_CI_ORCHESTRATION_REAL_DOTNET'
$failedRestoreEnvironmentVariable = 'LOGSCHEMA_CI_ORCHESTRATION_FAIL_RESTORE_SOLUTION'
$realDotnetEnvironmentBefore = [Environment]::GetEnvironmentVariable($realDotnetEnvironmentVariable)
$failedRestoreEnvironmentBefore = [Environment]::GetEnvironmentVariable($failedRestoreEnvironmentVariable)
$injectedFailureMarker = 'CI orchestration injected failure for solution restore'
$validatorOutput = $null

try {
    [Environment]::SetEnvironmentVariable($realDotnetEnvironmentVariable, $realDotnetPath)
    [Environment]::SetEnvironmentVariable($failedRestoreEnvironmentVariable, $solutionPath)

    if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        $dotnetShim = Join-Path $probeRoot 'dotnet.cmd'
        $windowsShim = @'
@echo off
if /I "%~1"=="restore" if /I "%~2"=="%LOGSCHEMA_CI_ORCHESTRATION_FAIL_RESTORE_SOLUTION%" (
    echo CI orchestration injected failure for solution restore 1>&2
    exit /b 86
)
"%LOGSCHEMA_CI_ORCHESTRATION_REAL_DOTNET%" %*
exit /b %ERRORLEVEL%
'@
        [IO.File]::WriteAllText($dotnetShim, $windowsShim.Replace("`n", "`r`n") + "`r`n", [Text.Encoding]::ASCII)
    }
    else {
        $dotnetShim = Join-Path $probeRoot 'dotnet'
        $unixShim = @'
#!/bin/sh
if [ "${1-}" = "restore" ] && [ "${2-}" = "$LOGSCHEMA_CI_ORCHESTRATION_FAIL_RESTORE_SOLUTION" ]; then
    printf '%s\n' 'CI orchestration injected failure for solution restore' >&2
    exit 86
fi
exec "$LOGSCHEMA_CI_ORCHESTRATION_REAL_DOTNET" "$@"
'@
        [IO.File]::WriteAllText($dotnetShim, $unixShim.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
        & chmod +x $dotnetShim
        Assert-That ($LASTEXITCODE -eq 0) 'the injected dotnet failure command must be executable'
    }

    $env:PATH = $probeRoot + [IO.Path]::PathSeparator + $pathBeforeProbe
    $pwshPath = (Get-Command pwsh -ErrorAction Stop).Source
    $validatorOutput = (& $pwshPath -NoProfile -NonInteractive -File $validatorPath 2>&1 | Out-String)
    $validatorExitCode = $LASTEXITCODE
    $expectedFailureLine = @($validatorOutput -split "`r?`n" | Where-Object { $_ -match 'Restore solution failed with exit code 86' } | Select-Object -First 1)
    $injectedFailureLines = @($validatorOutput -split "`r?`n" | Where-Object { $_ -match [regex]::Escape($injectedFailureMarker) })

    Assert-That ($validatorExitCode -ne 0) 'the repository validator must return failure when restore fails'
    Assert-That ($expectedFailureLine.Count -eq 1) 'the repository validator must report the injected restore failure'
    Assert-That ($injectedFailureLines.Count -eq 1) 'the dotnet shim must fail exactly the intended solution restore and forward every other invocation'
    # The nested process failure is expected; do not leak it to GitHub's outer pwsh shell.
    $global:LASTEXITCODE = 0
    Write-Host 'Expected fail-closed negative control passed: injected dotnet restore exit 86 failed the validator.'
    Write-Host ("Expected failure detail: {0}" -f $expectedFailureLine[0].Trim())
}
catch {
    if ($null -ne $validatorOutput) {
        throw "CI orchestration regression failed. Captured validator output:`n$validatorOutput`nRegression error: $($_.Exception.Message)"
    }
    throw
}
finally {
    $env:PATH = $pathBeforeProbe
    [Environment]::SetEnvironmentVariable($realDotnetEnvironmentVariable, $realDotnetEnvironmentBefore)
    [Environment]::SetEnvironmentVariable($failedRestoreEnvironmentVariable, $failedRestoreEnvironmentBefore)
    Remove-Item -LiteralPath $probeRoot -Recurse -Force -ErrorAction Stop
}

Write-Host 'CI orchestration regression passed: one required validator path retains the three-platform matrix and cache.'
