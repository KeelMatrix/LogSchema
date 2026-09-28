param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

function Get-Text {
    param([Parameter(Mandatory = $true)][int[]]$CodePoint)

    return (($CodePoint | ForEach-Object { [char]$_ }) -join '')
}

function Invoke-Git {
    param([Parameter(Mandatory = $true)][string[]]$ArgumentList)

    $output = (& git -C $RepositoryRoot @ArgumentList 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Git command failed: git -C $RepositoryRoot $($ArgumentList -join ' ')`n$output"
    }
    return $output
}

$expectedIdentity = 'KeelMatrix <keelmatrix@gmail.com>'
$commitIds = @(& git -C $RepositoryRoot rev-list --all 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "Git command failed: git -C $RepositoryRoot rev-list --all`n$($commitIds -join [Environment]::NewLine)"
}
$commitIds = @($commitIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($commitIds.Count -eq 0) {
    throw 'History hygiene cannot pass without at least one reachable commit.'
}

$trailer = Get-Text -CodePoint @(67, 111, 45, 65, 117, 116, 104, 111, 114, 101, 100, 45, 66, 121)
$identifierPattern = '(?i)(?<![A-Za-z0-9])[A-Z]+-\d+(?![A-Za-z0-9])'
$languageTokens = @(
    (Get-Text -CodePoint @(97, 103, 101, 110, 116)),
    (Get-Text -CodePoint @(109, 111, 100, 101, 108)),
    (Get-Text -CodePoint @(111, 114, 99, 104, 101, 115, 116, 114, 97, 116)),
    (Get-Text -CodePoint @(112, 114, 111, 109, 112, 116)),
    (Get-Text -CodePoint @(114, 101, 118, 105, 101, 119)),
    (Get-Text -CodePoint @(114, 101, 118, 105, 101, 119, 101, 114)),
    (Get-Text -CodePoint @(112, 97, 112, 101, 114, 99, 108, 105, 112)),
    (Get-Text -CodePoint @(99, 111, 100, 101, 120)),
    (Get-Text -CodePoint @(102, 114, 111, 110, 116, 105, 101, 114)),
    (Get-Text -CodePoint @(97, 105, 45, 103, 101, 110, 101, 114, 97, 116, 101, 100))
)

$violations = [System.Collections.Generic.List[string]]::new()
foreach ($commitId in $commitIds) {
    $author = (Invoke-Git -ArgumentList @('show', '-s', '--format=%an <%ae>', $commitId)).Trim()
    $committer = (Invoke-Git -ArgumentList @('show', '-s', '--format=%cn <%ce>', $commitId)).Trim()
    $message = Invoke-Git -ArgumentList @('show', '-s', '--format=%B', $commitId)

    if ($author -ne $expectedIdentity) {
        [void]$violations.Add("${commitId}: author identity")
    }
    if ($committer -ne $expectedIdentity) {
        [void]$violations.Add("${commitId}: committer identity")
    }
    if ($message -match "(?im)^\s*$([regex]::Escape($trailer))\s*:") {
        [void]$violations.Add("${commitId}: commit attribution")
    }
    if ($message -match $identifierPattern) {
        [void]$violations.Add("${commitId}: internal identifier")
    }
    foreach ($token in $languageTokens) {
        if ($message -match "(?i)(?<![A-Za-z])$([regex]::Escape($token))(?![A-Za-z])") {
            [void]$violations.Add("${commitId}: prohibited metadata language")
            break
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "History hygiene failed: $($violations.Count) violation(s)."
    $violations | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host "History hygiene passed: $($commitIds.Count) reachable commits scanned."
