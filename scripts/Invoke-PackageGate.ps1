[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$gateRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/package-gate'))
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $gateRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package gate output is outside the repository: $gateRoot"
}

$project = Join-Path $repoRoot 'src/KeelMatrix.ShutdownSpec/KeelMatrix.ShutdownSpec.csproj'
$smokeProject = Join-Path $repoRoot 'tests/PackageSmoke/PackageSmoke.csproj'
$feedRoot = Join-Path $gateRoot 'feed'
$packagesRoot = Join-Path $gateRoot 'packages'
$artifactRoot = Join-Path $gateRoot 'artifacts'
$packageId = 'KeelMatrix.ShutdownSpec'
$version = '0.1.0'
$nupkgName = "$packageId.$version.nupkg"
$snupkgName = "$packageId.$version.snupkg"

if (Test-Path -LiteralPath $gateRoot) {
    Remove-Item -LiteralPath $gateRoot -Recurse -Force
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
        if (-not ($AllowedPatterns | Where-Object { $name -match $_ })) {
            throw "Unexpected package entry: $name"
        }
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Invoke-Checked 'dotnet' @('pack', $project, '--configuration', $Configuration, '--output', $artifactRoot, '--configfile', (Join-Path $repoRoot 'NuGet.config'))

$artifactNames = @(Get-ChildItem -LiteralPath $artifactRoot -File | ForEach-Object Name | Sort-Object)
$expectedArtifacts = @($nupkgName, $snupkgName) | Sort-Object
if (Compare-Object -ReferenceObject $expectedArtifacts -DifferenceObject $artifactNames) {
    throw "Unexpected package artifacts. Expected: $($expectedArtifacts -join ', '); actual: $($artifactNames -join ', ')"
}

$nupkgPath = Join-Path $artifactRoot $nupkgName
$snupkgPath = Join-Path $artifactRoot $snupkgName
$iconPath = Join-Path $repoRoot 'icon.png'
$nupkg = [IO.Compression.ZipFile]::OpenRead($nupkgPath)
try {
    $required = @('_rels/.rels', '[Content_Types].xml', "$packageId.nuspec", 'README.md', 'LICENSE',
        'lib/net8.0/KeelMatrix.ShutdownSpec.dll', 'lib/net8.0/KeelMatrix.ShutdownSpec.xml',
        'lib/netstandard2.0/KeelMatrix.ShutdownSpec.dll', 'lib/netstandard2.0/KeelMatrix.ShutdownSpec.xml')
    $allowed = @('^_rels/\.rels$', '^\[Content_Types\]\.xml$', "^$packageId\.nuspec$", '^README\.md$', '^LICENSE$',
        '^lib/net8\.0/KeelMatrix\.ShutdownSpec\.(dll|xml)$', '^lib/netstandard2\.0/KeelMatrix\.ShutdownSpec\.(dll|xml)$',
        '^package/services/metadata/core-properties/[^/]+\.psmdcp$')
    if (Test-Path -LiteralPath $iconPath) {
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

    $iconMetadata = $metadata.icon
    if (Test-Path -LiteralPath $iconPath) {
        if ($iconMetadata -ne 'icon.png') { throw 'Package icon metadata is not icon.png.' }
        $rootHash = (Get-FileHash -LiteralPath $iconPath -Algorithm SHA256).Hash
        $iconEntry = $nupkg.GetEntry('icon.png')
        $tempIcon = Join-Path $gateRoot 'icon-check.png'
        $stream = $iconEntry.Open()
        $file = [IO.File]::Create($tempIcon)
        try { $stream.CopyTo($file) } finally { $file.Dispose(); $stream.Dispose() }
        if ((Get-FileHash -LiteralPath $tempIcon -Algorithm SHA256).Hash -ne $rootHash) { throw 'Embedded icon is not byte-identical to the repository icon.' }
        if ((Get-Item -LiteralPath $tempIcon).Length -gt 200KB) { throw 'Icon exceeds the 200 KB limit.' }
        $bytes = [IO.File]::ReadAllBytes($tempIcon)
        $signature = @(137, 80, 78, 71, 13, 10, 26, 10)
        if ($bytes.Length -lt 24 -or (0..7 | Where-Object { $bytes[$_] -ne $signature[$_] }).Count -gt 0) { throw 'Icon must be a PNG.' }
        $width = ($bytes[16] -shl 24) -bor ($bytes[17] -shl 16) -bor ($bytes[18] -shl 8) -bor $bytes[19]
        $height = ($bytes[20] -shl 24) -bor ($bytes[21] -shl 16) -bor ($bytes[22] -shl 8) -bor $bytes[23]
        if ($width -ne 512 -or $height -ne 512) { throw 'Icon must be exactly 512x512.' }
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

Write-Host "Package gate passed: $nupkgName, $snupkgName"
