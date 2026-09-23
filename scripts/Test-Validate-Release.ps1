[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function New-ScenarioRoot([string]$Name, [string]$SourceRoot, [string]$ParentRoot) {
    $scenarioRoot = Join-Path $ParentRoot $Name
    New-Item -ItemType Directory -Force -Path (Join-Path $scenarioRoot 'src/KeelMatrix.ShutdownSpec') | Out-Null

    foreach ($relativePath in @(
        'Directory.Build.props',
        'Directory.Packages.props',
        'CHANGELOG.md',
        'src/KeelMatrix.ShutdownSpec/KeelMatrix.ShutdownSpec.csproj'
    )) {
        $sourcePath = Join-Path $SourceRoot $relativePath
        $destinationPath = Join-Path $scenarioRoot $relativePath
        [IO.File]::Copy($sourcePath, $destinationPath, $true)
    }

    Set-Content -LiteralPath (Join-Path $scenarioRoot 'README.md') -Value @(
        '# Validator fixture',
        '',
        'dotnet add package KeelMatrix.ShutdownSpec'
    ) -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $scenarioRoot 'src/KeelMatrix.ShutdownSpec/README.md') -Value @(
        '# Validator fixture',
        '',
        'dotnet add package KeelMatrix.ShutdownSpec',
        '',
        '`Microsoft.Extensions.Hosting` 10.0.12'
    ) -Encoding UTF8

    return $scenarioRoot
}

function Invoke-Validator([string]$ValidatorPath, [string]$RepositoryRoot) {
    $outputLines = & pwsh -NoProfile -File $ValidatorPath -Tag v0.1.0 -FirstPublicRelease -RepositoryRoot $RepositoryRoot 2>&1
    $exitCode = $LASTEXITCODE
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = ($outputLines | Out-String).Trim()
    }
}

function Assert-Pass([string]$Name, [pscustomobject]$Result) {
    if ($Result.ExitCode -ne 0) {
        throw "$Name should pass but exited $($Result.ExitCode): $($Result.Output)"
    }
}

function Assert-FailsWith([string]$Name, [pscustomobject]$Result, [string]$ExpectedText) {
    if ($Result.ExitCode -eq 0) {
        throw "$Name should fail closed but passed: $($Result.Output)"
    }
    $normalizedOutput = [regex]::Replace($Result.Output, '\s+', ' ')
    $normalizedExpectedText = [regex]::Replace($ExpectedText, '\s+', ' ')
    if (-not $normalizedOutput.Contains($normalizedExpectedText)) {
        throw "$Name failed with unexpected output. Expected '$ExpectedText', got: $($Result.Output)"
    }
}

$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$validatorPath = Join-Path $PSScriptRoot 'Validate-Release.ps1'
$temporaryParent = if ([string]::IsNullOrWhiteSpace($env:PAPERCLIP_RUN_SCRATCH_DIR)) {
    [IO.Path]::GetTempPath()
}
else {
    $env:PAPERCLIP_RUN_SCRATCH_DIR
}
$testRoot = Join-Path $temporaryParent "shutdownspec-release-validator-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

try {
    $passRoot = New-ScenarioRoot 'pass' $sourceRoot $testRoot
    $passResult = Invoke-Validator $validatorPath $passRoot
    Assert-Pass 'finalized changelog' $passResult

    $unreleasedRoot = New-ScenarioRoot 'unreleased-entry' $sourceRoot $testRoot
    $unreleasedPath = Join-Path $unreleasedRoot 'CHANGELOG.md'
    $unreleasedText = Get-Content -LiteralPath $unreleasedPath -Raw -Encoding UTF8
    $unreleasedText = $unreleasedText -replace '(?m)^## \[Unreleased\]\r?\n', "## [Unreleased]`n`n- Unreleased note injected for validator regression coverage.`n"
    Set-Content -LiteralPath $unreleasedPath -Value $unreleasedText -Encoding UTF8 -NoNewline
    $unreleasedResult = Invoke-Validator $validatorPath $unreleasedRoot
    Assert-FailsWith 'Unreleased content' $unreleasedResult 'requires an empty [Unreleased] section'

    $plannedRoot = New-ScenarioRoot 'planned-heading' $sourceRoot $testRoot
    $plannedPath = Join-Path $plannedRoot 'CHANGELOG.md'
    $plannedText = Get-Content -LiteralPath $plannedPath -Raw -Encoding UTF8
    $plannedText = $plannedText.Replace('## [0.1.0] - 2026-09-23', '## [0.1.0] - Planned')
    Set-Content -LiteralPath $plannedPath -Value $plannedText -Encoding UTF8 -NoNewline
    $plannedResult = Invoke-Validator $validatorPath $plannedRoot
    Assert-FailsWith 'Planned heading' $plannedResult 'still contains pre-release wording in its heading'

    $remediationRoot = New-ScenarioRoot 'remediation-wording' $sourceRoot $testRoot
    $remediationPath = Join-Path $remediationRoot 'CHANGELOG.md'
    $remediationText = Get-Content -LiteralPath $remediationPath -Raw -Encoding UTF8
    $remediationText = $remediationText.Replace(
        '- Provides deterministic direct and host-backed lifecycle contracts for `IHostedService` and `BackgroundService` implementations.',
        '- Fixed a lifecycle completion bug.'
    )
    Set-Content -LiteralPath $remediationPath -Value $remediationText -Encoding UTF8 -NoNewline
    $remediationResult = Invoke-Validator $validatorPath $remediationRoot
    Assert-FailsWith 'Remediation wording' $remediationResult 'contains remediation-history wording'

    Write-Output 'Release validator regression self-test passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
