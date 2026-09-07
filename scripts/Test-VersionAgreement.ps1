[CmdletBinding()]
param(
    [string]$Tag
)

# The application version, the installer version, and the release tag must all agree before anything is
# packaged. This runs without creating or pushing a tag; a tag is supplied only when one already exists.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$buildProperties = [xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw)
$applicationVersion = $buildProperties.Project.PropertyGroup.Version

if (-not $applicationVersion) {
    throw 'Directory.Build.props does not define a Version.'
}

if ($applicationVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Application version '$applicationVersion' is not a valid MSI ProductVersion. Use major.minor.patch."
}

# The installer takes its version from the same property, so agreement is structural rather than copied.
$wxs = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer/UseNotch.wxs') -Raw
if ($wxs -notmatch 'Version="\$\(var\.ProductVersion\)"') {
    throw 'The installer does not take its version from the build, so the two can drift apart.'
}

Write-Output "Application and installer version: $applicationVersion"

if ($Tag) {
    $tagVersion = $Tag.TrimStart('v')
    # A prerelease label belongs to the tag, not to the MSI ProductVersion, so it is compared separately.
    $tagCore = ($tagVersion -split '-')[0]
    if ($tagCore -ne $applicationVersion) {
        throw "Release tag '$Tag' does not match the application version '$applicationVersion'."
    }

    Write-Output "Release tag $Tag agrees with the application version."
}
else {
    Write-Output 'No tag supplied; only the application and installer agreement was checked.'
}

Write-Output 'PASS: version agreement verified.'
