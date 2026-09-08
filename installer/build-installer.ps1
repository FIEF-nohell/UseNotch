[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory
)

# Builds the per-user MSI from a clean checkout. Every step stops the build on failure, so partial
# output is never presented as a successful package.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stagingRoot = Join-Path $repositoryRoot 'artifacts'
$publishDirectory = Join-Path $stagingRoot "publish/$Runtime"
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $stagingRoot 'installer'
}

function Assert-InsideWorkspace {
    param([string]$Path, [string]$Description)

    # Resolve first, then prove the resolved path is inside the repository. A recursive delete must
    # never run against a path that escaped the workspace through a symlink or a caller-supplied value.
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $resolvedParent = (Resolve-Path -LiteralPath $parent).Path
    $resolved = Join-Path $resolvedParent (Split-Path -Leaf $Path)
    $boundary = [IO.Path]::TrimEndingDirectorySeparator($repositoryRoot) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description resolved to '$resolved', which is outside the repository at '$repositoryRoot'."
    }

    return $resolved
}

function Remove-Directory {
    param([string]$Path, [string]$Description)

    $safePath = Assert-InsideWorkspace -Path $Path -Description $Description
    if (Test-Path -LiteralPath $safePath) {
        Remove-Item -LiteralPath $safePath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $safePath -Force | Out-Null
    return $safePath
}

function Get-ProductVersion {
    $buildProperties = [xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw)
    $version = $buildProperties.Project.PropertyGroup.Version
    if (-not $version) {
        throw 'Directory.Build.props does not define a Version.'
    }

    # An MSI ProductVersion is numeric only. A prerelease label is intentionally not encoded here; it
    # belongs to the release tag, not to the installer version.
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version '$version' is not a valid MSI ProductVersion. Use major.minor.patch."
    }

    $parts = $version.Split('.')
    if ([int]$parts[0] -gt 255 -or [int]$parts[1] -gt 255 -or [int]$parts[2] -gt 65535) {
        throw "Version '$version' exceeds the ranges an MSI ProductVersion allows."
    }

    return $version
}

$productVersion = Get-ProductVersion
Write-Output "Product version: $productVersion"

$publishDirectory = Remove-Directory -Path $publishDirectory -Description 'Publish directory'
$OutputDirectory = Remove-Directory -Path $OutputDirectory -Description 'Installer output directory'

Write-Output 'Restoring locked dependencies'
dotnet restore (Join-Path $repositoryRoot 'UseNotch.sln') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

Write-Output 'Restoring pinned build tools'
dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }

Write-Output 'Running tests'
dotnet test (Join-Path $repositoryRoot 'UseNotch.sln') -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; no installer was produced.' }

Write-Output 'Publishing self-contained application'
# Self-contained so a standard user needs no separately installed runtime.
dotnet publish (Join-Path $repositoryRoot 'src/UseNotch.App/UseNotch.App.csproj') `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$applicationPath = Join-Path $publishDirectory 'UseNotch.App.exe'
if (-not (Test-Path -LiteralPath $applicationPath)) {
    throw 'The publish output does not contain UseNotch.App.exe.'
}

$iconSource = Join-Path $repositoryRoot 'src/UseNotch.App/Assets/usenotch.ico'
if (-not (Test-Path -LiteralPath $iconSource)) {
    throw "The application icon is missing at '$iconSource'."
}

Write-Output 'Adding the pinned WiX extension'
dotnet wix extension add --global WixToolset.Util.wixext/5.0.2
if ($LASTEXITCODE -ne 0) { throw 'Adding the WiX util extension failed.' }

Write-Output 'Building the MSI'
$msiPath = Join-Path $OutputDirectory "UseNotch-$productVersion-$Runtime.msi"
dotnet wix build `
    -arch x64 `
    -ext WixToolset.Util.wixext `
    -d ProductVersion=$productVersion `
    -d PublishDir=$publishDirectory `
    -d IconSource=$iconSource `
    -bindpath $publishDirectory `
    -out $msiPath `
    (Join-Path $PSScriptRoot 'UseNotch.wxs')
if ($LASTEXITCODE -ne 0) { throw 'Building the MSI failed.' }

if (-not (Test-Path -LiteralPath $msiPath)) {
    throw 'The MSI was reported as built but does not exist.'
}

Write-Output "PASS: $msiPath"
