[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$ReleaseReadiness,
    [switch]$PngValidationSelfTest,
    [string]$ArtifactRoot,
    [string]$Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$gateRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/package-gate'))
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $gateRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package gate output is outside the repository: $gateRoot"
}

$project = Join-Path $repoRoot 'src/KeelMatrix.ShutdownSpec/KeelMatrix.ShutdownSpec.csproj'
$solution = Join-Path $repoRoot 'KeelMatrix.ShutdownSpec.sln'
$smokeProject = Join-Path $repoRoot 'tests/PackageSmoke/PackageSmoke.csproj'
$auditScript = Join-Path $repoRoot 'scripts/Invoke-VulnerabilityAudit.ps1'
$feedRoot = Join-Path $gateRoot 'feed'
$packagesRoot = Join-Path $gateRoot 'packages'
$artifactRoot = if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) { Join-Path $gateRoot 'artifacts' } else { [IO.Path]::GetFullPath($ArtifactRoot) }
$iconPath = Join-Path $repoRoot 'icon.png'
$packageId = 'KeelMatrix.ShutdownSpec'
$version = $Version
$nupkgName = "$packageId.$version.nupkg"
$snupkgName = "$packageId.$version.snupkg"
$sensitivePackageEntryPattern = '(?i)(^|/)(?:.*\.env(?:\..*)?|.*\.(?:pfx|snk|key)|AGENTS\.md|.*(?:secret|credential).*)$'

if ($ReleaseReadiness -and -not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    throw "Release-readiness package gate requires the repository-root icon.png. Add the file at the repository root and run the package gate again."
}

if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be a stable semantic version: $version" }
if (-not $artifactRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package artifacts must stay inside the repository: $artifactRoot"
}

if (Test-Path -LiteralPath $gateRoot) {
    Remove-Item -LiteralPath $gateRoot -Recurse -Force
}
if (([IO.Path]::GetFullPath($artifactRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar) -ne ([IO.Path]::GetFullPath($gateRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar)) {
    if (Test-Path -LiteralPath $artifactRoot) { Remove-Item -LiteralPath $artifactRoot -Recurse -Force }
}
New-Item -ItemType Directory -Path $feedRoot, $packagesRoot, $artifactRoot | Out-Null

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Command failed with exit code ${exitCode}: $Command $($Arguments -join ' ')"
    }
}

function Get-ZipText {
    param([IO.Compression.ZipArchive]$Archive, [string]$EntryName)
    $entry = $Archive.GetEntry($EntryName)
    if ($null -eq $entry) { throw "Missing package entry: $EntryName" }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Assert-ZipEntries {
    param([IO.Compression.ZipArchive]$Archive, [string[]]$RequiredEntries, [string[]]$AllowedPatterns)
    $names = @($Archive.Entries | ForEach-Object FullName | Sort-Object)
    foreach ($required in $RequiredEntries) {
        if ($names -notcontains $required) { throw "Missing package entry: $required" }
    }
    foreach ($name in $names) {
        if ($name -match $sensitivePackageEntryPattern) {
            throw "Sensitive package entry rejected: $name"
        }
        if (-not ($AllowedPatterns | Where-Object { $name -match $_ })) {
            throw "Unexpected package entry: $name"
        }
    }
}

function Get-PngDimensions {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    if ($Bytes.Length -lt 24) { throw 'Icon must be a PNG with an IHDR dimension header.' }

    return [pscustomobject]@{
        Width = ([uint32]$Bytes[16] -shl 24) -bor ([uint32]$Bytes[17] -shl 16) -bor ([uint32]$Bytes[18] -shl 8) -bor [uint32]$Bytes[19]
        Height = ([uint32]$Bytes[20] -shl 24) -bor ([uint32]$Bytes[21] -shl 16) -bor ([uint32]$Bytes[22] -shl 8) -bor [uint32]$Bytes[23]
    }
}

function Assert-PngIconBytes {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    if ($Bytes.Length -gt 200KB) { throw 'Icon exceeds the 200 KB limit.' }

    $signature = @(137, 80, 78, 71, 13, 10, 26, 10)
    if ($Bytes.Length -lt 24 -or (0..7 | Where-Object { $Bytes[$_] -ne $signature[$_] }).Count -gt 0) {
        throw 'Icon must be a PNG.'
    }

    $dimensions = Get-PngDimensions -Bytes $Bytes
    if ($dimensions.Width -ne 512 -or $dimensions.Height -ne 512) {
        throw 'Icon must be exactly 512x512.'
    }
}

function New-PngHeaderBytes {
    param([int]$Width, [int]$Height)

    $bytes = [byte[]]::new(24)
    [Array]::Copy([byte[]](137, 80, 78, 71, 13, 10, 26, 10), 0, $bytes, 0, 8)
    [Array]::Copy([byte[]](73, 72, 68, 82), 0, $bytes, 12, 4)
    $bytes[18] = [byte](($Width -shr 8) -band 0xff)
    $bytes[19] = [byte]($Width -band 0xff)
    $bytes[22] = [byte](($Height -shr 8) -band 0xff)
    $bytes[23] = [byte]($Height -band 0xff)
    return $bytes
}

function Invoke-PngValidationSelfTest {
    foreach ($case in @(
            @{ Name = '256x256'; Width = 256; Height = 256 },
            @{ Name = '1024x1024'; Width = 1024; Height = 1024 })) {
        $bytes = New-PngHeaderBytes -Width $case.Width -Height $case.Height
        $dimensions = Get-PngDimensions -Bytes $bytes
        if ($dimensions.Width -ne $case.Width -or $dimensions.Height -ne $case.Height) {
            throw "PNG dimension self-test failed for $($case.Name): got $($dimensions.Width)x$($dimensions.Height)."
        }
    }

    try {
        Assert-PngIconBytes -Bytes (New-PngHeaderBytes -Width 256 -Height 256)
        throw 'PNG validation self-test expected a non-512x512 icon to fail closed.'
    } catch {
        if ($_.Exception.Message -ne 'Icon must be exactly 512x512.') { throw }
    }

    $oversized = [byte[]]::new(200KB + 1)
    [Array]::Copy((New-PngHeaderBytes -Width 512 -Height 512), 0, $oversized, 0, 24)
    try {
        Assert-PngIconBytes -Bytes $oversized
        throw 'PNG validation self-test expected an oversized icon to fail closed.'
    } catch {
        if ($_.Exception.Message -ne 'Icon exceeds the 200 KB limit.') { throw }
    }

    foreach ($sensitiveName in @('settings.env', 'private.pfx', 'signing.snk', 'private.key', 'AGENTS.md', 'client-secret.json', 'service-credential.json')) {
        if ($sensitiveName -notmatch $sensitivePackageEntryPattern) { throw "Packaging boundary self-test missed sensitive filename '$sensitiveName'." }
    }
    foreach ($safeName in @('README.md', 'LICENSE', 'icon.png', 'lib/net8.0/KeelMatrix.ShutdownSpec.dll')) {
        if ($safeName -match $sensitivePackageEntryPattern) { throw "Packaging boundary self-test rejected safe filename '$safeName'." }
    }
    $shippingProjectText = Get-Content -LiteralPath $project -Raw
    $packTargetsText = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.targets') -Raw
    if ($shippingProjectText -match 'PackageIcon\s+Condition=') { throw 'Packaging boundary self-test found a conditional package icon.' }
    if ($packTargetsText -notmatch 'Sensitive filename cannot be packed') { throw 'Packaging boundary self-test found no MSBuild sensitive-file rejection.' }
    if ($packTargetsText -notmatch 'Unexpected pack input cannot be packed') { throw 'Packaging boundary self-test found no MSBuild pack-input allowlist rejection.' }
    foreach ($allowlistedInput in @(
            @{ Identity = 'README.md'; PackagePath = 'README.md' },
            @{ Identity = '../../LICENSE'; PackagePath = '' },
            @{ Identity = '../../icon.png'; PackagePath = 'icon.png' })) {
        $allowlistCondition = [regex]::Escape("('%(None.Identity)' != '$($allowlistedInput.Identity)' or '%(None.PackagePath)' != '$($allowlistedInput.PackagePath)')")
        $contentAllowlistCondition = [regex]::Escape("('%(Content.Identity)' != '$($allowlistedInput.Identity)' or '%(Content.PackagePath)' != '$($allowlistedInput.PackagePath)')")
        if ($packTargetsText -notmatch $allowlistCondition -or $packTargetsText -notmatch $contentAllowlistCondition) {
            throw "Packaging boundary self-test found no allowlist entry for '$($allowlistedInput.Identity)' -> '$($allowlistedInput.PackagePath)'."
        }
    }
    $unexpectedInput = @{ Identity = 'unexpected.txt'; PackagePath = 'unexpected.txt' }
    if ($unexpectedInput.Identity -in @('README.md', '../../LICENSE', '../../icon.png')) {
        throw 'Packaging boundary self-test accepted an unexpected pack input.'
    }

    Write-Host 'PNG and packaging-boundary self-test passed: invalid icons and sensitive filenames fail closed.'
}

if ($PngValidationSelfTest) {
    Invoke-PngValidationSelfTest
    exit 0
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$nugetConfig = Join-Path $repoRoot 'NuGet.config'
Invoke-Checked 'dotnet' @('restore', $solution, '--configfile', $nugetConfig)
& $auditScript -Solution $solution -ConfigFile $nugetConfig
Invoke-Checked 'dotnet' @('pack', $project, '--configuration', $Configuration, '--no-restore', '--output', $artifactRoot, '--configfile', $nugetConfig)

$artifactNames = @(Get-ChildItem -LiteralPath $artifactRoot -File | ForEach-Object Name | Sort-Object)
$expectedArtifacts = @($nupkgName, $snupkgName) | Sort-Object
if (Compare-Object -ReferenceObject $expectedArtifacts -DifferenceObject $artifactNames) {
    throw "Unexpected package artifacts. Expected: $($expectedArtifacts -join ', '); actual: $($artifactNames -join ', ')"
}

$nupkgPath = Join-Path $artifactRoot $nupkgName
$snupkgPath = Join-Path $artifactRoot $snupkgName
$nupkg = [IO.Compression.ZipFile]::OpenRead($nupkgPath)
try {
    $required = @('_rels/.rels', '[Content_Types].xml', "$packageId.nuspec", 'README.md', 'LICENSE',
        'lib/net8.0/KeelMatrix.ShutdownSpec.dll', 'lib/net8.0/KeelMatrix.ShutdownSpec.xml',
        'lib/netstandard2.0/KeelMatrix.ShutdownSpec.dll', 'lib/netstandard2.0/KeelMatrix.ShutdownSpec.xml')
    $allowed = @('^_rels/\.rels$', '^\[Content_Types\]\.xml$', "^$packageId\.nuspec$", '^README\.md$', '^LICENSE$',
        '^lib/net8\.0/KeelMatrix\.ShutdownSpec\.(dll|xml)$', '^lib/netstandard2\.0/KeelMatrix\.ShutdownSpec\.(dll|xml)$',
        '^package/services/metadata/core-properties/[^/]+\.psmdcp$')
    if ($ReleaseReadiness -or (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
        $required += 'icon.png'
        $allowed += '^icon\.png$'
    }
    Assert-ZipEntries $nupkg $required $allowed

    [xml]$nuspec = Get-ZipText $nupkg "$packageId.nuspec"
    $metadata = $nuspec.package.metadata
    if ($metadata.id -ne $packageId -or $metadata.version -ne $version) { throw 'Package id or version is incorrect.' }
    if ($metadata.readme -ne 'README.md' -or $metadata.license.type -ne 'file' -or $metadata.license.'#text' -ne 'LICENSE') { throw 'Package readme or license metadata is incorrect.' }
    if ($metadata.repository.type -ne 'git' -or $metadata.repository.url -ne 'https://github.com/KeelMatrix/ShutdownSpec') { throw 'Repository metadata is incorrect.' }
    $dependencyGroups = @($metadata.dependencies.group | ForEach-Object targetFramework | Sort-Object)
    if (Compare-Object -ReferenceObject @('.NETStandard2.0', 'net8.0') -DifferenceObject $dependencyGroups) { throw 'Package dependency target frameworks are incorrect.' }
    foreach ($group in @($metadata.dependencies.group)) {
        $dependencies = @($group.dependency)
        if ($dependencies.Count -ne 1) { throw "Dependency group '$($group.targetFramework)' must contain exactly one runtime dependency." }
        $dependency = $dependencies[0]
        if ($dependency.id -ne 'Microsoft.Extensions.Hosting') { throw "Unexpected dependency id '$($dependency.id)'." }
        if ($dependency.version -notmatch '^(?:10\.0\.12|\[10\.0\.12\]|\[10\.0\.12,\s*10\.0\.13\))$') {
            throw "Dependency '$($dependency.id)' has an unsafe or unexpected version range '$($dependency.version)'."
        }
    }

    $iconMetadata = $metadata.icon
    if ($ReleaseReadiness -or (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
        if ($iconMetadata -ne 'icon.png') { throw 'Package icon metadata is not icon.png.' }
        $rootHash = (Get-FileHash -LiteralPath $iconPath -Algorithm SHA256).Hash
        $iconEntry = $nupkg.GetEntry('icon.png')
        $tempIcon = Join-Path $gateRoot 'icon-check.png'
        $stream = $iconEntry.Open()
        $file = [IO.File]::Create($tempIcon)
        try { $stream.CopyTo($file) } finally { $file.Dispose(); $stream.Dispose() }
        if ((Get-FileHash -LiteralPath $tempIcon -Algorithm SHA256).Hash -ne $rootHash) { throw 'Embedded icon is not byte-identical to the repository icon.' }
        $bytes = [IO.File]::ReadAllBytes($tempIcon)
        Assert-PngIconBytes -Bytes $bytes
        Write-Host 'Icon checks passed: 512x512, <=200 KB, byte-identical.'
    } elseif ($null -ne $iconMetadata) {
        throw 'Package declares an icon but the repository icon is absent.'
    }
}
finally { $nupkg.Dispose() }

$snupkg = [IO.Compression.ZipFile]::OpenRead($snupkgPath)
try {
    $requiredSymbols = @('_rels/.rels', '[Content_Types].xml', "$packageId.nuspec",
        'lib/net8.0/KeelMatrix.ShutdownSpec.pdb', 'lib/netstandard2.0/KeelMatrix.ShutdownSpec.pdb')
    $allowedSymbols = @('^_rels/\.rels$', '^\[Content_Types\]\.xml$', "^$packageId\.nuspec$",
        '^lib/net8\.0/KeelMatrix\.ShutdownSpec\.pdb$', '^lib/netstandard2\.0/KeelMatrix\.ShutdownSpec\.pdb$',
        '^package/services/metadata/core-properties/[^/]+\.psmdcp$')
    Assert-ZipEntries $snupkg $requiredSymbols $allowedSymbols
}
finally { $snupkg.Dispose() }

Copy-Item -LiteralPath $nupkgPath -Destination $feedRoot
$smokeConfig = Join-Path $repoRoot 'tests/PackageSmoke/NuGet.config'
Invoke-Checked 'dotnet' @('restore', $smokeProject, '--configfile', $smokeConfig, '--packages', $packagesRoot)
Invoke-Checked 'dotnet' @('run', '--project', $smokeProject, '--configuration', $Configuration, '--no-restore')

$netstandardProject = Join-Path $repoRoot 'tests/PackageSmokeNetStandard/PackageSmokeNetStandard.csproj'
$netstandardConfig = Join-Path $repoRoot 'tests/PackageSmokeNetStandard/NuGet.config'
Invoke-Checked 'dotnet' @('restore', $netstandardProject, '--configfile', $netstandardConfig, '--packages', $packagesRoot)
Invoke-Checked 'dotnet' @('build', $netstandardProject, '--configuration', $Configuration, '--no-restore', '--nologo')
$assetsPath = Join-Path $repoRoot 'tests/PackageSmokeNetStandard/obj/project.assets.json'
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$netstandardTarget = $assets.targets.PSObject.Properties | Where-Object Name -eq '.NETStandard,Version=v2.0' | Select-Object -ExpandProperty Value
$packageLibrary = $netstandardTarget."$packageId/$version"
if ($null -eq $packageLibrary -or $null -eq $packageLibrary.compile.PSObject.Properties['lib/netstandard2.0/KeelMatrix.ShutdownSpec.dll']) {
    throw 'The isolated netstandard2.0 consumer did not select the shipped netstandard2.0 library asset.'
}
Write-Host 'Isolated netstandard2.0 consumer passed: lib/netstandard2.0/KeelMatrix.ShutdownSpec.dll selected.'

Write-Host "Package gate passed: $nupkgName, $snupkgName"
