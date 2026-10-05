[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$ScratchRoot
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}
if ([string]::IsNullOrWhiteSpace($ScratchRoot)) {
    $ScratchRoot = if (-not [string]::IsNullOrWhiteSpace($env:PAPERCLIP_RUN_SCRATCH_DIR)) {
        $env:PAPERCLIP_RUN_SCRATCH_DIR
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:PAPERCLIP_SCRATCH_DIR)) {
        $env:PAPERCLIP_SCRATCH_DIR
    }
    else {
        [IO.Path]::GetTempPath()
    }
}

$repositoryRootPath = [IO.Path]::GetFullPath($RepositoryRoot)
$scratchRootPath = [IO.Path]::GetFullPath($ScratchRoot)
$validatorPath = Join-Path $PSScriptRoot 'Validate-WebsiteMetadata.ps1'
$canonicalUrl = 'https://github.com/KeelMatrix/ShutdownSpec'

function Invoke-ValidatorCase(
    [string]$Name,
    [object[]]$Projects,
    [string]$ManifestJson,
    [bool]$ShouldPass
) {
    $fixtureRoot = Join-Path $scratchRootPath ('shutdownspec-website-metadata-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
    $fixtureFullPath = [IO.Path]::GetFullPath($fixtureRoot)
    $scratchPrefix = $scratchRootPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fixtureFullPath.StartsWith($scratchPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Website metadata test fixture escaped its scratch directory.'
    }

    try {
        foreach ($projectSpec in $Projects) {
            $projectDirectory = Join-Path $fixtureFullPath ('src/' + $projectSpec.PackageId)
            New-Item -ItemType Directory -Path $projectDirectory -Force | Out-Null
            $packageTypeXml = if ([string]::IsNullOrWhiteSpace($projectSpec.PackageType)) {
                ''
            }
            else {
                "<PackageType>$($projectSpec.PackageType)</PackageType>"
            }
            $projectXml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsShippingProject>true</IsShippingProject>
    <IsPackable>true</IsPackable>
    <PackageId>$($projectSpec.PackageId)</PackageId>
    <Description>Test framework neutral shutdown contract package.</Description>
    <PackageTags>$($projectSpec.PackageTags)</PackageTags>
    <Authors>KeelMatrix</Authors>
    <PackageProjectUrl>$($projectSpec.PackageProjectUrl)</PackageProjectUrl>
    <RepositoryUrl>$($projectSpec.RepositoryUrl)</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    $packageTypeXml
  </PropertyGroup>
</Project>
"@
            [IO.File]::WriteAllText(
                (Join-Path $projectDirectory 'Package.csproj'),
                $projectXml,
                [Text.UTF8Encoding]::new($false)
            )
        }

        [IO.File]::WriteAllText(
            (Join-Path $fixtureFullPath 'keelmatrix.website.json'),
            $ManifestJson,
            [Text.UTF8Encoding]::new($false)
        )

        $validatorPassed = $true
        $validatorError = $null
        try {
            & $validatorPath -RepositoryRoot $fixtureFullPath | Out-Null
        }
        catch {
            $validatorPassed = $false
            $validatorError = $_.Exception.Message
        }

        if ($validatorPassed -ne $ShouldPass) {
            if ($ShouldPass) {
                throw "Valid case '$Name' failed: $validatorError"
            }
            throw "Invalid case '$Name' unexpectedly passed validation."
        }
    }
    finally {
        if (Test-Path -LiteralPath $fixtureFullPath) {
            Remove-Item -LiteralPath $fixtureFullPath -Recurse -Force
        }
    }
}

function New-TestProject(
    [string]$PackageId,
    [string]$PackageTags,
    [string]$PackageProjectUrl = $canonicalUrl,
    [string]$RepositoryUrl = $canonicalUrl,
    [string]$PackageType = ''
) {
    return [pscustomobject]@{
        PackageId = $PackageId
        PackageTags = $PackageTags
        PackageProjectUrl = $PackageProjectUrl
        RepositoryUrl = $RepositoryUrl
        PackageType = $PackageType
    }
}

$singlePublicPrimary = '{"schemaVersion":1,"packages":{"KeelMatrix.ShutdownSpec":{"visibility":"public-product","role":"primary"}}}'
$singlePublicProject = New-TestProject -PackageId 'KeelMatrix.ShutdownSpec' -PackageTags 'dotnet;keelmatrix-public-product;keelmatrix-primary'
Invoke-ValidatorCase 'public primary' @($singlePublicProject) $singlePublicPrimary $true

$singleInternalPrimary = '{"schemaVersion":1,"packages":{"KeelMatrix.ShutdownSpec":{"visibility":"internal-package","role":"primary"}}}'
$singleInternalProject = New-TestProject -PackageId 'KeelMatrix.ShutdownSpec' -PackageTags 'telemetry;keelmatrix-internal-package;keelmatrix-primary'
Invoke-ValidatorCase 'internal primary' @($singleInternalProject) $singleInternalPrimary $true

$primaryProject = New-TestProject -PackageId 'KeelMatrix.ShutdownSpec' -PackageTags 'keelmatrix-public-product;keelmatrix-primary'
$componentId = 'KeelMatrix.ShutdownSpec.Hosting'
$componentProject = New-TestProject -PackageId $componentId -PackageTags 'keelmatrix-public-product;keelmatrix-component'
$primaryAndComponent = '{"schemaVersion":1,"packages":{"KeelMatrix.ShutdownSpec":{"visibility":"public-product","role":"primary"},"KeelMatrix.ShutdownSpec.Hosting":{"visibility":"public-product","role":"component"}}}'
Invoke-ValidatorCase 'one primary with component sibling' @($primaryProject, $componentProject) $primaryAndComponent $true

Invoke-ValidatorCase 'unsupported schema version' @($singlePublicProject) $singlePublicPrimary.Replace('"schemaVersion":1', '"schemaVersion":2') $false
Invoke-ValidatorCase 'duplicate JSON property' @($singlePublicProject) '{"schemaVersion":1,"schemaVersion":1,"packages":{"KeelMatrix.ShutdownSpec":{"visibility":"public-product","role":"primary"}}}' $false
Invoke-ValidatorCase 'unexpected manifest field' @($singlePublicProject) '{"schemaVersion":1,"description":"not classification","packages":{"KeelMatrix.ShutdownSpec":{"visibility":"public-product","role":"primary"}}}' $false
Invoke-ValidatorCase 'missing package entry' @($singlePublicProject) '{"schemaVersion":1,"packages":{}}' $false
Invoke-ValidatorCase 'manifest and tag visibility mismatch' @($singlePublicProject) '{"schemaVersion":1,"packages":{"KeelMatrix.ShutdownSpec":{"visibility":"internal-package","role":"primary"}}}' $false

$wrongCaseProject = New-TestProject -PackageId 'KeelMatrix.ShutdownSpec' -PackageTags 'keelmatrix-Public-Product;keelmatrix-primary'
Invoke-ValidatorCase 'incorrect sentinel casing' @($wrongCaseProject) $singlePublicPrimary $false
$duplicateVisibilityProject = New-TestProject -PackageId 'KeelMatrix.ShutdownSpec' -PackageTags 'keelmatrix-public-product;keelmatrix-internal-package;keelmatrix-primary'
Invoke-ValidatorCase 'duplicate visibility sentinels' @($duplicateVisibilityProject) $singlePublicPrimary $false
$duplicateRoleProject = New-TestProject -PackageId 'KeelMatrix.ShutdownSpec' -PackageTags 'keelmatrix-public-product;keelmatrix-primary;keelmatrix-component'
Invoke-ValidatorCase 'duplicate role sentinels' @($duplicateRoleProject) $singlePublicPrimary $false

$componentOnlyProject = New-TestProject -PackageId 'KeelMatrix.ShutdownSpec' -PackageTags 'keelmatrix-public-product;keelmatrix-component'
$componentOnlyManifest = '{"schemaVersion":1,"packages":{"KeelMatrix.ShutdownSpec":{"visibility":"public-product","role":"component"}}}'
Invoke-ValidatorCase 'missing primary boundary' @($componentOnlyProject) $componentOnlyManifest $false

$twoPrimariesManifest = '{"schemaVersion":1,"packages":{"KeelMatrix.ShutdownSpec":{"visibility":"public-product","role":"primary"},"KeelMatrix.ShutdownSpec.Hosting":{"visibility":"public-product","role":"primary"}}}'
$secondPrimaryProject = New-TestProject -PackageId $componentId -PackageTags 'keelmatrix-public-product;keelmatrix-primary'
Invoke-ValidatorCase 'duplicate primary boundary' @($primaryProject, $secondPrimaryProject) $twoPrimariesManifest $false
$internalPrimaryProject = New-TestProject -PackageId 'KeelMatrix.ShutdownSpec' -PackageTags 'keelmatrix-internal-package;keelmatrix-primary'
$publicComponentManifest = '{"schemaVersion":1,"packages":{"KeelMatrix.ShutdownSpec":{"visibility":"internal-package","role":"primary"},"KeelMatrix.ShutdownSpec.Hosting":{"visibility":"public-product","role":"component"}}}'
Invoke-ValidatorCase 'public family with only an internal primary' @($internalPrimaryProject, $componentProject) $publicComponentManifest $false

$fragmentProject = New-TestProject -PackageId 'KeelMatrix.ShutdownSpec' -PackageTags 'keelmatrix-public-product;keelmatrix-primary' -PackageProjectUrl "$canonicalUrl#readme"
Invoke-ValidatorCase 'project URL fragment' @($fragmentProject) $singlePublicPrimary $false
$wrongRepositoryProject = New-TestProject -PackageId 'KeelMatrix.ShutdownSpec' -PackageTags 'keelmatrix-public-product;keelmatrix-primary' -RepositoryUrl 'https://github.com/OtherOwner/OtherRepo'
Invoke-ValidatorCase 'repository URL mismatch' @($wrongRepositoryProject) $singlePublicPrimary $false
$toolProject = New-TestProject -PackageId 'KeelMatrix.ShutdownSpec' -PackageTags 'keelmatrix-public-product;keelmatrix-primary' -PackageType 'DotnetTool'
Invoke-ValidatorCase 'incorrect package type' @($toolProject) $singlePublicPrimary $false

Write-Output 'Website metadata validation tests passed (17 cases).'
