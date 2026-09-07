[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

# Interactive lifecycle checks: duplicate launch, hidden overlay, and quit without an orphan process.
# These need a real desktop session; they are not something CI success can stand in for.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw 'Run this check in PowerShell 7 on an interactive Windows desktop.'
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run from a non-elevated shell to verify standard-user behaviour.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$applicationPath = Join-Path $repositoryRoot "src/UseNotch.App/bin/$Configuration/net8.0-windows/UseNotch.App.exe"
if (-not (Test-Path -LiteralPath $applicationPath)) {
    throw "Build the application in $Configuration first."
}

if (Get-Process -Name 'UseNotch.App' -ErrorAction SilentlyContinue) {
    throw 'A UseNotch process is already running. Quit it before running this check.'
}

$primary = $null
$duplicate = $null
$shutdown = $null
try {
    Write-Output 'Starting the first instance'
    $primary = Start-Process -FilePath $applicationPath -WorkingDirectory $repositoryRoot -PassThru
    Start-Sleep -Seconds 8
    if ($primary.HasExited) {
        throw "The first instance exited immediately with code $($primary.ExitCode)."
    }

    Write-Output 'Launching a duplicate instance'
    $duplicate = Start-Process -FilePath $applicationPath -ArgumentList '--show-status' -WorkingDirectory $repositoryRoot -PassThru
    if (-not $duplicate.WaitForExit(10000)) {
        throw 'The duplicate launch did not hand over to the running instance and exit.'
    }

    if ($duplicate.ExitCode -ne 0) {
        throw "The duplicate launch exited with code $($duplicate.ExitCode)."
    }

    $running = @(Get-Process -Name 'UseNotch.App' -ErrorAction SilentlyContinue)
    if ($running.Count -ne 1) {
        throw "Expected exactly one UseNotch process after a duplicate launch, found $($running.Count)."
    }

    Write-Output '  duplicate launch handed over and exited, one process remains'

    Write-Output 'Quitting through the bounded shutdown signal'
    $shutdown = Start-Process -FilePath $applicationPath -ArgumentList '--smoke-quit' -WorkingDirectory $repositoryRoot -PassThru
    if (-not $shutdown.WaitForExit(10000)) {
        throw 'The shutdown signal process did not exit.'
    }

    if (-not $primary.WaitForExit(10000)) {
        throw 'The running instance did not exit after the bounded shutdown signal.'
    }

    if ($primary.ExitCode -ne 0) {
        throw "The running instance exited with code $($primary.ExitCode)."
    }

    Start-Sleep -Milliseconds 500
    $orphans = @(Get-Process -Name 'UseNotch.App' -ErrorAction SilentlyContinue)
    if ($orphans.Count -ne 0) {
        throw "Quit left $($orphans.Count) orphan process(es) behind."
    }

    Write-Output '  quit left no orphan process'
    Write-Output 'PASS: duplicate launch handed over, a single instance ran, and quit left nothing behind.'
}
finally {
    foreach ($process in @($shutdown, $duplicate, $primary)) {
        if ($null -ne $process) {
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit(5000) | Out-Null
            }

            $process.Dispose()
        }
    }
}
