[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    throw "Website metadata contract failed: $Message"
}

function Get-ProjectPropertyValue(
    [xml]$Project,
    [string]$Name,
    [string]$ProjectPath,
    [switch]$Required
) {
    $nodes = @($Project.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='$Name']"))
    if ($nodes.Count -gt 1) {
        Fail "project '$ProjectPath' must declare '$Name' no more than once."
    }
    if ($nodes.Count -eq 0) {
        if ($Required) {
            Fail "project '$ProjectPath' must declare '$Name'."
        }
        return $null
    }

    return $nodes[0].InnerText.Trim()
}

function Assert-JsonObjectProperties(
    [System.Text.Json.JsonElement]$Element,
    [string[]]$ExpectedNames,
    [string]$Context
) {
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        Fail "$Context must be a JSON object."
    }

    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $count = 0
    foreach ($property in $Element.EnumerateObject()) {
        $count++
        if (-not $names.Add($property.Name)) {
            Fail "$Context contains duplicate property '$($property.Name)'."
        }
    }

    if ($count -ne $ExpectedNames.Count) {
        Fail "$Context must contain exactly these properties: $($ExpectedNames -join ', ')."
    }
    foreach ($expectedName in $ExpectedNames) {
        if (-not $names.Contains($expectedName)) {
            Fail "$Context is missing property '$expectedName'."
        }
    }
}

function Get-JsonPropertyValue(
    [System.Text.Json.JsonElement]$Element,
    [string]$Name,
    [string]$Context
) {
    foreach ($property in $Element.EnumerateObject()) {
        if ($property.Name -ceq $Name) {
            return $property.Value
        }
    }

    Fail "$Context is missing property '$Name'."
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}

$repositoryRootPath = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not (Test-Path -LiteralPath $repositoryRootPath -PathType Container)) {
    Fail "repository root '$repositoryRootPath' does not exist."
}

$projectFiles = @(
    Get-ChildItem -LiteralPath $repositoryRootPath -Filter '*.csproj' -File -Recurse |
        Where-Object { $_.FullName -notmatch '(?i)[\\/](?:bin|obj|artifacts)[\\/]' }
)
$packableProjects = [System.Collections.Generic.List[object]]::new()
$shippingProjectCount = 0

foreach ($projectFile in $projectFiles) {
    try {
        [xml]$project = [IO.File]::ReadAllText($projectFile.FullName)
    }
    catch {
        Fail "project '$($projectFile.FullName)' is not valid XML."
    }

    $relativeProjectPath = [IO.Path]::GetRelativePath($repositoryRootPath, $projectFile.FullName).Replace('\', '/')
    $isPackable = Get-ProjectPropertyValue -Project $project -Name 'IsPackable' -ProjectPath $relativeProjectPath
    $isShippingProject = Get-ProjectPropertyValue -Project $project -Name 'IsShippingProject' -ProjectPath $relativeProjectPath

    if ($isShippingProject -ceq 'true') {
        $shippingProjectCount++
        if ($isPackable -cne 'true') {
            Fail "shipping project '$relativeProjectPath' must explicitly set IsPackable to true."
        }
    }
    if ($isPackable -ceq 'true') {
        if ($isShippingProject -cne 'true') {
            Fail "packable project '$relativeProjectPath' must be identified as a shipping project."
        }

        $packageId = Get-ProjectPropertyValue -Project $project -Name 'PackageId' -ProjectPath $relativeProjectPath -Required
        if ([string]::IsNullOrWhiteSpace($packageId)) {
            Fail "packable project '$relativeProjectPath' must declare a non-empty PackageId."
        }

        $packableProjects.Add([pscustomobject]@{
            Project = $project
            Path = $relativeProjectPath
            PackageId = $packageId
            Description = Get-ProjectPropertyValue -Project $project -Name 'Description' -ProjectPath $relativeProjectPath -Required
            PackageTags = Get-ProjectPropertyValue -Project $project -Name 'PackageTags' -ProjectPath $relativeProjectPath -Required
            Authors = Get-ProjectPropertyValue -Project $project -Name 'Authors' -ProjectPath $relativeProjectPath -Required
            PackageProjectUrl = Get-ProjectPropertyValue -Project $project -Name 'PackageProjectUrl' -ProjectPath $relativeProjectPath -Required
            RepositoryUrl = Get-ProjectPropertyValue -Project $project -Name 'RepositoryUrl' -ProjectPath $relativeProjectPath -Required
            RepositoryType = Get-ProjectPropertyValue -Project $project -Name 'RepositoryType' -ProjectPath $relativeProjectPath -Required
            PackageType = Get-ProjectPropertyValue -Project $project -Name 'PackageType' -ProjectPath $relativeProjectPath
        })
    }
}

if ($shippingProjectCount -eq 0 -or $packableProjects.Count -ne $shippingProjectCount) {
    Fail 'every packable project must be a shipping project, and at least one shipping project must exist.'
}

$packageIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($packableProject in $packableProjects) {
    if (-not $packageIds.Add($packableProject.PackageId)) {
        Fail "package id '$($packableProject.PackageId)' is declared by more than one packable project."
    }

    if ([string]::IsNullOrWhiteSpace($packableProject.Description)) {
        Fail "package '$($packableProject.PackageId)' must have a non-empty description."
    }
    if ($packableProject.Authors -cne 'KeelMatrix') {
        Fail "package '$($packableProject.PackageId)' must declare KeelMatrix as its author."
    }
    if ($packableProject.PackageProjectUrl -cne 'https://github.com/KeelMatrix/ShutdownSpec') {
        Fail "package '$($packableProject.PackageId)' PackageProjectUrl must be the canonical KeelMatrix/ShutdownSpec URL without a fragment."
    }
    if ($packableProject.RepositoryUrl -cne 'https://github.com/KeelMatrix/ShutdownSpec' -or $packableProject.RepositoryType -cne 'git') {
        Fail "package '$($packableProject.PackageId)' repository metadata must identify the canonical KeelMatrix/ShutdownSpec git repository."
    }
    if (-not [string]::IsNullOrWhiteSpace($packableProject.PackageType)) {
        $declaredTypes = @($packableProject.PackageType -split '[;\s]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($declaredTypes.Count -ne 1 -or $declaredTypes[0] -cne 'Dependency') {
            Fail "package '$($packableProject.PackageId)' must remain a standard Dependency package."
        }
    }
}

$manifestPath = Join-Path $repositoryRootPath 'keelmatrix.website.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    Fail 'root keelmatrix.website.json is required.'
}

$manifestText = [IO.File]::ReadAllText($manifestPath)
try {
    $manifestDocument = [System.Text.Json.JsonDocument]::Parse($manifestText)
}
catch {
    Fail "root keelmatrix.website.json is not valid strict JSON: $($_.Exception.Message)"
}

$visibilitySentinels = @('keelmatrix-public-product', 'keelmatrix-internal-package')
$roleSentinels = @('keelmatrix-primary', 'keelmatrix-component')
$allSentinels = $visibilitySentinels + $roleSentinels
$primaryCount = 0
$publicPackageCount = 0
$publicPrimaryCount = 0
try {
    $manifest = $manifestDocument.RootElement
    Assert-JsonObjectProperties -Element $manifest -ExpectedNames @('schemaVersion', 'packages') -Context 'manifest root'

    $schemaVersion = Get-JsonPropertyValue -Element $manifest -Name 'schemaVersion' -Context 'manifest root'
    if ($schemaVersion.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or $schemaVersion.GetRawText() -cne '1') {
        Fail 'manifest schemaVersion must be the integer 1.'
    }

    $manifestPackages = Get-JsonPropertyValue -Element $manifest -Name 'packages' -Context 'manifest root'
    if ($manifestPackages.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        Fail 'manifest packages must be an object keyed by exact package id.'
    }

    $manifestPackageIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $manifestPackages.EnumerateObject()) {
        if (-not $manifestPackageIds.Add($entry.Name)) {
            Fail "manifest contains duplicate package id '$($entry.Name)'."
        }
        if (-not $packageIds.Contains($entry.Name)) {
            Fail "manifest package '$($entry.Name)' is not owned by a packable project in this repository."
        }

        $context = "manifest package '$($entry.Name)'"
        Assert-JsonObjectProperties -Element $entry.Value -ExpectedNames @('visibility', 'role') -Context $context
        $visibilityValue = Get-JsonPropertyValue -Element $entry.Value -Name 'visibility' -Context $context
        $roleValue = Get-JsonPropertyValue -Element $entry.Value -Name 'role' -Context $context
        if ($visibilityValue.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            $visibilityValue.GetString() -cnotin @('public-product', 'internal-package')) {
            Fail "$context visibility must be public-product or internal-package."
        }
        if ($roleValue.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            $roleValue.GetString() -cnotin @('primary', 'component')) {
            Fail "$context role must be primary or component."
        }
        if ($roleValue.GetString() -ceq 'primary') {
            $primaryCount++
        }
        if ($visibilityValue.GetString() -ceq 'public-product') {
            $publicPackageCount++
            if ($roleValue.GetString() -ceq 'primary') {
                $publicPrimaryCount++
            }
        }

        $packableProject = @($packableProjects | Where-Object { $_.PackageId -ceq $entry.Name })[0]
        $tags = @($packableProject.PackageTags -split '[;\s]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        foreach ($tag in $tags) {
            foreach ($sentinel in $allSentinels) {
                if ($tag -ieq $sentinel -and $tag -cne $sentinel) {
                    Fail "package '$($entry.Name)' uses incorrectly cased website sentinel '$tag'."
                }
            }
        }

        $visibilityTags = @($tags | Where-Object { $visibilitySentinels -ccontains $_ })
        $roleTags = @($tags | Where-Object { $roleSentinels -ccontains $_ })
        $expectedVisibilityTag = if ($visibilityValue.GetString() -ceq 'public-product') {
            'keelmatrix-public-product'
        }
        else {
            'keelmatrix-internal-package'
        }
        $expectedRoleTag = if ($roleValue.GetString() -ceq 'primary') {
            'keelmatrix-primary'
        }
        else {
            'keelmatrix-component'
        }

        if ($visibilityTags.Count -ne 1 -or $visibilityTags[0] -cne $expectedVisibilityTag) {
            Fail "package '$($entry.Name)' must have exactly one visibility sentinel matching its manifest classification."
        }
        if ($roleTags.Count -ne 1 -or $roleTags[0] -cne $expectedRoleTag) {
            Fail "package '$($entry.Name)' must have exactly one role sentinel matching its manifest classification."
        }
    }

    if ($manifestPackageIds.Count -ne $packageIds.Count) {
        Fail 'manifest package ids must exactly match the package ids of all packable projects.'
    }
    foreach ($packageId in $packageIds) {
        if (-not $manifestPackageIds.Contains($packageId)) {
            Fail "packable package '$packageId' is missing from the manifest."
        }
    }
    if ($primaryCount -ne 1) {
        Fail 'all packages in this repository family must have exactly one primary artifact.'
    }
    if ($publicPackageCount -gt 0 -and $publicPrimaryCount -ne 1) {
        Fail 'a public product family must have exactly one public primary artifact.'
    }
}
finally {
    $manifestDocument.Dispose()
}

Write-Output 'Website metadata contract passed for the repository package set.'
