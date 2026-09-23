[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$RepositoryRoot,

    [switch]$FirstPublicRelease
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    throw "Release contract failed: $Message"
}

function Resolve-RepositoryPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }

    return [IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot $Path))
}

function Read-RequiredText([string]$RelativePath) {
    $path = Resolve-RepositoryPath $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Fail "required file '$RelativePath' is missing."
    }

    try {
        return Get-Content -LiteralPath $path -Raw -Encoding UTF8
    }
    catch {
        Fail "required file '$RelativePath' could not be read: $($_.Exception.Message)"
    }
}

function Read-RequiredXml([string]$RelativePath) {
    $text = Read-RequiredText $RelativePath
    try {
        return [xml]$text
    }
    catch {
        Fail "required XML file '$RelativePath' is invalid: $($_.Exception.Message)"
    }
}

function Get-AttributeValue([System.Xml.XmlElement]$Element, [string]$Name) {
    $attribute = $Element.Attributes[$Name]
    if ($null -eq $attribute) {
        return $null
    }

    return $attribute.Value.Trim()
}

function Get-ConcreteVersion([string]$Value, [string]$Description) {
    $version = if ($null -eq $Value) { '' } else { $Value.Trim() }
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        Fail "$Description must be a concrete stable semantic version; found '$version'."
    }

    return $version
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}

$script:RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not (Test-Path -LiteralPath $script:RepositoryRoot -PathType Container)) {
    Fail "repository root '$script:RepositoryRoot' does not exist."
}

$tagMatch = [regex]::Match($Tag.Trim(), '^v(?<version>\d+\.\d+\.\d+)$')
if (-not $tagMatch.Success) {
    Fail "tag '$Tag' must match vX.Y.Z exactly."
}
$tagVersion = $tagMatch.Groups['version'].Value

$buildProps = Read-RequiredXml 'Directory.Build.props'
$versionNodes = @($buildProps.SelectNodes("//*[local-name()='Version']") | Where-Object { -not [string]::IsNullOrWhiteSpace($_.InnerText) })
if ($versionNodes.Count -ne 1) {
    Fail 'Directory.Build.props must contain exactly one concrete Version property.'
}
$packageVersion = Get-ConcreteVersion $versionNodes[0].InnerText 'Directory.Build.props Version'
if ($packageVersion -ne $tagVersion) {
    Fail "package version '$packageVersion' does not match tag version '$tagVersion'."
}

$shippingProjectPath = 'src/KeelMatrix.ShutdownSpec/KeelMatrix.ShutdownSpec.csproj'
$shippingProject = Read-RequiredXml $shippingProjectPath
$packageIdNode = @($shippingProject.SelectNodes("//*[local-name()='PackageId']") | Select-Object -First 1)
if ($packageIdNode.Count -ne 1 -or [string]::IsNullOrWhiteSpace($packageIdNode[0].InnerText)) {
    Fail 'shipping project must declare a concrete PackageId.'
}
$packageId = $packageIdNode[0].InnerText.Trim()

$centralPackages = Read-RequiredXml 'Directory.Packages.props'
$centralVersions = @{}
foreach ($node in @($centralPackages.SelectNodes("//*[local-name()='PackageVersion']"))) {
    $dependencyId = Get-AttributeValue $node 'Include'
    $dependencyVersion = Get-AttributeValue $node 'Version'
    if ([string]::IsNullOrWhiteSpace($dependencyId) -or [string]::IsNullOrWhiteSpace($dependencyVersion)) {
        Fail 'every central PackageVersion must have concrete Include and Version attributes.'
    }
    if ($dependencyVersion -eq '$(Version)') {
        $dependencyVersion = $packageVersion
    }
    if ($centralVersions.ContainsKey($dependencyId) -and $centralVersions[$dependencyId] -ne $dependencyVersion) {
        Fail "central package '$dependencyId' has conflicting versions '$($centralVersions[$dependencyId])' and '$dependencyVersion'."
    }
    $centralVersions[$dependencyId] = Get-ConcreteVersion $dependencyVersion "central package '$dependencyId'"
}

$runtimeDependencies = @()
foreach ($reference in @($shippingProject.SelectNodes("//*[local-name()='PackageReference']"))) {
    $dependencyId = Get-AttributeValue $reference 'Include'
    if ([string]::IsNullOrWhiteSpace($dependencyId)) {
        Fail 'shipping project contains a PackageReference without an Include attribute.'
    }

    $explicitVersion = Get-AttributeValue $reference 'Version'
    $centralVersion = if ($centralVersions.ContainsKey($dependencyId)) { $centralVersions[$dependencyId] } else { $null }
    if ([string]::IsNullOrWhiteSpace($explicitVersion) -and $null -eq $centralVersion) {
        Fail "shipping dependency '$dependencyId' has no explicit or central version."
    }
    if (-not [string]::IsNullOrWhiteSpace($explicitVersion)) {
        $explicitVersion = Get-ConcreteVersion $explicitVersion "shipping dependency '$dependencyId'"
        if ($null -ne $centralVersion -and $explicitVersion -ne $centralVersion) {
            Fail "shipping dependency '$dependencyId' declares '$explicitVersion' but central policy declares '$centralVersion'."
        }
        $resolvedVersion = $explicitVersion
    }
    else {
        $resolvedVersion = $centralVersion
    }

    $privateAssets = Get-AttributeValue $reference 'PrivateAssets'
    if ($privateAssets -notmatch '(?i)(^|[;\s])all([;\s]|$)') {
        $runtimeDependencies += [pscustomobject]@{ Id = $dependencyId; Version = $resolvedVersion }
    }
}

$changelogText = Read-RequiredText 'CHANGELOG.md'
$changelogLines = @($changelogText -split "`r?`n")

if ($FirstPublicRelease) {
    $unreleasedHeadingIndexes = @()
    for ($index = 0; $index -lt $changelogLines.Count; $index++) {
        if ($changelogLines[$index] -match '^##[ \t]+\[Unreleased\][ \t]*$') {
            $unreleasedHeadingIndexes += $index
        }
    }

    foreach ($unreleasedHeadingIndex in $unreleasedHeadingIndexes) {
        $unreleasedSectionEnd = $changelogLines.Count
        for ($index = $unreleasedHeadingIndex + 1; $index -lt $changelogLines.Count; $index++) {
            if ($changelogLines[$index] -match '^##[ \t]+') {
                $unreleasedSectionEnd = $index
                break
            }
        }

        $unreleasedSectionLines = if ($unreleasedSectionEnd -gt ($unreleasedHeadingIndex + 1)) {
            @($changelogLines[($unreleasedHeadingIndex + 1)..($unreleasedSectionEnd - 1)])
        }
        else {
            @()
        }
        $unreleasedContentLines = @($unreleasedSectionLines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($unreleasedContentLines.Count -gt 0) {
            Fail "initial public release [$tagVersion] requires an empty [Unreleased] section; found release-note content."
        }
    }
}

$releaseHeadingPattern = '^##[ \t]+\[(?<version>\d+\.\d+\.\d+)\](?<suffix>.*)$'
$targetHeadingIndexes = @()
$releaseRecords = @()
for ($index = 0; $index -lt $changelogLines.Count; $index++) {
    $match = [regex]::Match($changelogLines[$index], $releaseHeadingPattern)
    if ($match.Success) {
        $record = [pscustomobject]@{
            Version = $match.Groups['version'].Value
            Suffix = $match.Groups['suffix'].Value.Trim()
            Line = $index + 1
            Index = $index
        }
        $releaseRecords += $record
        if ($record.Version -eq $tagVersion) {
            $targetHeadingIndexes += $record
        }
    }
}

if ($targetHeadingIndexes.Count -ne 1) {
    Fail "CHANGELOG.md must contain exactly one level-two release heading for [$tagVersion]."
}
$targetHeading = $targetHeadingIndexes[0]
$preReleaseWording = '(?i)(?<![A-Za-z])(?:planned|unreleased|unpublished|not[ \t]+(?:yet[ \t]+)?published|not[ \t]+released|to[ \t]+be[ \t]+(?:published|released)|tbd|upcoming|draft|pending|pre[ -]?release)(?![A-Za-z])'
if ($targetHeading.Suffix -match $preReleaseWording) {
    Fail "CHANGELOG.md release [$tagVersion] still contains pre-release wording in its heading."
}

$dateMatches = @([regex]::Matches($targetHeading.Suffix, '(?<!\d)(?<date>\d{4}-\d{2}-\d{2})(?!\d)'))
if ($dateMatches.Count -ne 1) {
    Fail "CHANGELOG.md release [$tagVersion] must contain exactly one ISO release date."
}
$dateText = $dateMatches[0].Groups['date'].Value
[DateTime]$releaseDate = [DateTime]::MinValue
if (-not [DateTime]::TryParseExact($dateText, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$releaseDate)) {
    Fail "CHANGELOG.md release date '$dateText' is invalid."
}
if ($releaseDate.Date -gt [DateTime]::UtcNow.Date) {
    Fail "CHANGELOG.md release date '$dateText' is in the future."
}

$sectionEnd = $changelogLines.Count
for ($index = $targetHeading.Index + 1; $index -lt $changelogLines.Count; $index++) {
    if ($changelogLines[$index] -match '^##[ \t]+') {
        $sectionEnd = $index
        break
    }
}
$sectionLines = if ($sectionEnd -gt ($targetHeading.Index + 1)) {
    @($changelogLines[($targetHeading.Index + 1)..($sectionEnd - 1)])
}
else {
    @()
}
foreach ($line in $sectionLines) {
    if ($line -match $preReleaseWording) {
        Fail "CHANGELOG.md release [$tagVersion] contains pre-release wording: '$line'."
    }
}

$versionedReleaseRecords = @($releaseRecords | ForEach-Object {
    [pscustomobject]@{ Record = $_; Version = [Version]::Parse($_.Version) }
})
$targetSemanticVersion = [Version]::Parse($tagVersion)
$isInitialRelease = $FirstPublicRelease -or (@($versionedReleaseRecords | Where-Object { $_.Version -lt $targetSemanticVersion }).Count -eq 0)
if ($isInitialRelease) {
    $categoryRecords = @()
    $firstCategoryIndex = $null
    foreach ($line in $sectionLines) {
        $categoryMatch = [regex]::Match($line, '^###[ \t]+(?<category>.+?)[ \t]*$')
        if ($categoryMatch.Success) {
            if ($null -eq $firstCategoryIndex) {
                $firstCategoryIndex = $sectionLines.IndexOf($line)
            }
            $categoryRecords += $categoryMatch.Groups['category'].Value.Trim()
        }
    }
    if ($categoryRecords.Count -ne 1 -or $categoryRecords[0] -cne 'Added') {
        $foundCategories = if ($categoryRecords.Count -eq 0) { '<none>' } else { $categoryRecords -join ', ' }
        Fail "initial public release [$tagVersion] must contain only an Added section; found $foundCategories."
    }
    if ($null -ne $firstCategoryIndex -and $firstCategoryIndex -gt 0) {
        $textBeforeCategory = @($sectionLines[0..($firstCategoryIndex - 1)] | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($textBeforeCategory.Count -gt 0) {
            Fail "initial public release [$tagVersion] contains release text outside its Added section."
        }
    }

    $remediationPattern = '(?i)(?<![A-Za-z])(?:fixed|fixes|corrected|resolved|addressed|remediation|regression|previously|formerly|no[ \t]+longer|changed[ \t]+from|false[ \t]+(?:result|positive|negative)|bug)(?![A-Za-z])'
    foreach ($line in $sectionLines) {
        if ($line -match $remediationPattern) {
            Fail "initial public release [$tagVersion] contains remediation-history wording: '$line'."
        }
    }
}

$readmePaths = @('README.md', 'src/KeelMatrix.ShutdownSpec/README.md')
$installCommandCount = 0
foreach ($readmePath in $readmePaths) {
    $readmeText = Read-RequiredText $readmePath
    $commands = @([regex]::Matches($readmeText, '(?im)^\s*dotnet\s+add\s+package\s+(?<id>[A-Za-z0-9_.-]+)(?<args>[^\r\n]*)$'))
    if ($commands.Count -eq 0) {
        Fail "README '$readmePath' must contain a dotnet add package command."
    }
    foreach ($command in $commands) {
        $commandId = $command.Groups['id'].Value
        if ($commandId -ne $packageId) {
            Fail "README '$readmePath' installs '$commandId' instead of '$packageId'."
        }
        $installCommandCount++
        $versionMatches = @([regex]::Matches($command.Groups['args'].Value, '(?i)(?:--version|-v)(?:\s+|=)(?<version>[^\s]+)'))
        if ($versionMatches.Count -gt 1) {
            Fail "README '$readmePath' has more than one package version on its install command."
        }
        if ($versionMatches.Count -eq 1 -and $versionMatches[0].Groups['version'].Value -ne $tagVersion) {
            Fail "README '$readmePath' install version '$($versionMatches[0].Groups['version'].Value)' does not match '$tagVersion'."
        }
    }
}
if ($installCommandCount -eq 0) {
    Fail 'no README install command was validated.'
}

$packageReadmeText = Read-RequiredText 'src/KeelMatrix.ShutdownSpec/README.md'
foreach ($dependency in $runtimeDependencies) {
    $dependencyPattern = '(?i)`' + [regex]::Escape($dependency.Id) + '`\s+(?<version>\d+\.\d+\.\d+)'
    $dependencyMatches = @([regex]::Matches($packageReadmeText, $dependencyPattern))
    if ($dependencyMatches.Count -ne 1) {
        Fail "package README must document runtime dependency '$($dependency.Id)' exactly once with its resolved version."
    }
    $documentedVersion = $dependencyMatches[0].Groups['version'].Value
    if ($documentedVersion -ne $dependency.Version) {
        Fail "package README documents '$($dependency.Id)' as '$documentedVersion', but the resolved dependency version is '$($dependency.Version)'."
    }
}

Write-Output "Release contract passed for $packageId $tagVersion."
