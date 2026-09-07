[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath
)

# Structural validation of the built MSI. This deliberately does not install anything: the clean
# install, upgrade, downgrade, and uninstall matrix belongs in a disposable Windows environment, not in
# the developer's daily one.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $MsiPath)) {
    throw "No MSI at '$MsiPath'."
}

$resolvedMsi = (Resolve-Path -LiteralPath $MsiPath).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($resolvedMsi, 0))

function Invoke-MsiQuery {
    param([string]$Sql)

    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($Sql))
    [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $rows = @()
    while ($true) {
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if (-not $record) { break }
        $fields = @()
        for ($index = 1; $index -le 4; $index++) {
            try { $fields += $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, $index) }
            catch { break }
        }
        $rows += , $fields
    }

    [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
    # Comma-wrapped so a single-row result is not unwrapped into its own fields by the pipeline.
    return , $rows
}

# The _Tables pseudo-table does not accept a WHERE clause, so the full list is read once.
# Every query result is assigned before it is piped: piping the call directly would enumerate the
# wrapper array instead of the rows.
$tableRows = Invoke-MsiQuery 'SELECT `Name` FROM `_Tables`'
$tableNames = @($tableRows | ForEach-Object { $_[0] })

function Invoke-MsiQueryIfTableExists {
    param([string]$Table, [string]$Sql)

    if ($tableNames -notcontains $Table) {
        return , @()
    }

    # Assigned first: using the call directly as an expression would wrap the result a second time.
    $rows = Invoke-MsiQuery $Sql
    return , $rows
}

function Get-MsiProperty {
    param([string]$Name)

    $rows = Invoke-MsiQuery "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$Name'"
    if ($null -eq $rows -or $rows.Count -eq 0) { return $null }
    return $rows[0][0]
}

$failures = @()
function Assert-That {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { $script:failures += $Message }
}

$productName = Get-MsiProperty 'ProductName'
$productVersion = Get-MsiProperty 'ProductVersion'
$upgradeCode = Get-MsiProperty 'UpgradeCode'

Assert-That ($productName -eq 'UseNotch') "ProductName is '$productName'."
Assert-That ($productVersion -match '^\d+\.\d+\.\d+$') "ProductVersion '$productVersion' is not major.minor.patch."

# The UpgradeCode is this product's permanent identity. A changed value silently breaks every upgrade.
Assert-That ($upgradeCode -eq '{8F3D6C41-5B27-4A19-9D0E-2C7A41F6B8D5}') "UpgradeCode is '$upgradeCode'."

$buildVersion = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Directory.Build.props') -Raw)).Project.PropertyGroup.Version
Assert-That ($productVersion -eq $buildVersion) "Installer version '$productVersion' does not match the application version '$buildVersion'."

# Per-user install: no ALLUSERS, and the install folder must hang off LocalAppData.
$allUsers = Get-MsiProperty 'ALLUSERS'
Assert-That ($null -eq $allUsers -or $allUsers -eq '' -or $allUsers -eq '0') "ALLUSERS is '$allUsers'; the install must stay per user."

$directories = Invoke-MsiQuery 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`'
$installFolder = $directories | Where-Object { $_[0] -eq 'INSTALLFOLDER' }
Assert-That ($null -ne $installFolder) 'The MSI has no INSTALLFOLDER directory.'
if ($installFolder) {
    Assert-That ($installFolder[1] -eq 'LocalAppDataFolder') "INSTALLFOLDER is parented to '$($installFolder[1])' rather than LocalAppDataFolder."
}

Assert-That ($null -eq ($directories | Where-Object { $_[1] -eq 'ProgramFilesFolder' -or $_[1] -eq 'ProgramFiles64Folder' })) 'The MSI installs into Program Files, which a per-user package must not do.'

# The application and its runtime must be present, so a standard user needs no separate runtime.
$files = Invoke-MsiQuery 'SELECT `FileName`, `Component_` FROM `File`'
Assert-That ($files.Count -gt 50) "The MSI carries only $($files.Count) files; a self-contained publish should carry far more."

function Test-HasFile {
    param([string]$Name)
    return $null -ne ($files | Where-Object { $_[0] -like "*$Name" -or $_[0] -like "*|$Name" })
}

Assert-That (Test-HasFile 'UseNotch.App.exe') 'The MSI does not contain UseNotch.App.exe.'
Assert-That (Test-HasFile 'hostfxr.dll') 'The MSI does not contain the .NET host, so it is not self-contained.'
Assert-That (Test-HasFile 'System.Private.CoreLib.dll') 'The MSI does not contain the .NET runtime assemblies.'

# Uninstall must remove the launch-at-login entry this application owns.
$removeRegistry = Invoke-MsiQueryIfTableExists 'RemoveRegistry' 'SELECT `RemoveRegistry`, `Root`, `Key`, `Name` FROM `RemoveRegistry`'
$runEntry = $removeRegistry | Where-Object { $_[2] -like '*CurrentVersion\Run' -and $_[3] -eq 'UseNotch' }
Assert-That ($null -ne $runEntry) 'Uninstall does not remove the UseNotch launch-at-login registration.'
if ($runEntry) {
    Assert-That ($runEntry[1] -eq '1') "The launch-at-login removal targets registry root '$($runEntry[1])' rather than HKCU."
}

# A downgrade must be rejected rather than overwriting newer files.
$upgradeRows = Invoke-MsiQuery 'SELECT `UpgradeCode`, `VersionMin`, `VersionMax`, `Attributes` FROM `Upgrade`'
Assert-That ($upgradeRows.Count -gt 0) 'The MSI defines no upgrade behaviour.'
$downgradeRow = $upgradeRows | Where-Object { $_[1] -eq $productVersion -and [string]::IsNullOrEmpty($_[2]) }
Assert-That ($null -ne $downgradeRow) 'The MSI does not detect a newer installed version, so a downgrade would not be rejected.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output "FAIL: $_" }
    throw "$($failures.Count) installer validation check(s) failed."
}

Write-Output "PASS: $([IO.Path]::GetFileName($resolvedMsi)) is a per-user, self-contained package with a stable identity, downgrade rejection, and startup cleanup."
Write-Output "  product $productName $productVersion, upgrade code $upgradeCode, $($files.Count) files"
Write-Output '  Clean install, upgrade, downgrade, and uninstall still require a disposable Windows environment.'
