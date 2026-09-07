[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Codex,
    [switch]$Claude
)

# Live vertical slice for M06 and M07: local credential source, provider request, snapshot, overlay,
# detail pane, sanitized cache, offline restart from that cache, and bounded shutdown.
#
# This check contacts the real provider endpoints with the signed-in account's borrowed credentials.
# It prints only percentages, window identifiers, and file sizes. It never prints, copies, or stores a
# token, account identifier, email address, or raw response body.

$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'Run this slice check in PowerShell 7 on an interactive Windows desktop.'
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run from a non-elevated shell to verify standard-user behavior.'
}

if (-not $Codex -and -not $Claude) {
    $Codex = $true
    $Claude = $true
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$applicationPath = Join-Path $repositoryRoot "src/UseNotch.App/bin/$Configuration/net8.0-windows/UseNotch.App.exe"
if (-not (Test-Path -LiteralPath $applicationPath)) {
    throw "Build the application in $Configuration before running the live slice check."
}

$cacheRoot = Join-Path $env:LOCALAPPDATA 'UseNotch/cache'
$expected = @()
if ($Codex) { $expected += [pscustomobject]@{ Provider = 'OpenAi'; Path = Join-Path $cacheRoot 'OpenAi.json' } }
if ($Claude) { $expected += [pscustomobject]@{ Provider = 'Anthropic'; Path = Join-Path $cacheRoot 'Anthropic.json' } }

if (-not ('UseNotch.LiveSlice.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

namespace UseNotch.LiveSlice
{
    public static class Native
    {
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr window, ref RECT rectangle);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint flags, int x, int y, int data, UIntPtr extra);

        public const uint LeftDown = 0x0002;
        public const uint LeftUp = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        public static string GetTitle(IntPtr window)
        {
            var text = new StringBuilder(512);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }

        public static IntPtr FindByTitle(int processId, string title)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                uint candidateProcessId;
                GetWindowThreadProcessId(window, out candidateProcessId);
                if (candidateProcessId == processId && GetTitle(window) == title)
                {
                    found = window;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);
    }
}
'@
}

function Wait-ForWindow {
    param([int]$ProcessId, [string]$Title, [int]$TimeoutMilliseconds = 20000)

    $deadline = [Environment]::TickCount64 + $TimeoutMilliseconds
    while ([Environment]::TickCount64 -lt $deadline) {
        $window = [UseNotch.LiveSlice.Native]::FindByTitle($ProcessId, $Title)
        if ($window -ne [IntPtr]::Zero) {
            return $window
        }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out waiting for the window titled '$Title'."
}

function Wait-ForCacheFiles {
    param([object[]]$Files, [int]$TimeoutMilliseconds = 60000)

    $deadline = [Environment]::TickCount64 + $TimeoutMilliseconds
    while ([Environment]::TickCount64 -lt $deadline) {
        if (@($Files | Where-Object { Test-Path -LiteralPath $_.Path }).Count -eq $Files.Count) {
            return
        }
        Start-Sleep -Milliseconds 250
    }
    $missing = ($Files | Where-Object { -not (Test-Path -LiteralPath $_.Path) }).Provider -join ', '
    throw "Timed out waiting for the sanitized cache files: $missing."
}

function Assert-SanitizedCache {
    param([string]$Path, [string]$Provider)

    $raw = Get-Content -LiteralPath $Path -Raw
    foreach ($pattern in @('sk-ant', 'sk-proj', 'eyJ', 'accessToken', 'access_token', 'refreshToken', 'refresh_token', 'Authorization', '@')) {
        if ($raw.Contains($pattern)) {
            throw "$Provider cache contains a forbidden pattern: $pattern"
        }
    }

    $entry = $raw | ConvertFrom-Json
    if ($entry.SchemaVersion -ne 1) {
        throw "$Provider cache has an unexpected schema version: $($entry.SchemaVersion)"
    }
    return $entry
}

$authenticationNames = @('Disabled', 'Discovering', 'Missing', 'PresentUnverified', 'Authenticated', 'Expired', 'Rejected', 'AccessDenied', 'Unsupported')
$freshnessNames = @('Fresh', 'Stale', 'Expired', 'Unknown')
$originNames = @('Live', 'CachedStartup')

function Get-EnumName {
    param([object]$Value, [string[]]$Names)

    if ($null -eq $Value) { return 'none' }
    $index = [int]$Value
    if ($index -ge 0 -and $index -lt $Names.Length) { return $Names[$index] }
    return "unknown($index)"
}

function Show-Reading {
    param([object]$Entry, [string]$Provider)

    $snapshot = $Entry.State.Snapshot
    $auth = Get-EnumName $Entry.State.Status.Authentication $authenticationNames
    $freshness = Get-EnumName $Entry.State.Status.Freshness $freshnessNames
    $origin = Get-EnumName $Entry.State.Origin $originNames
    if ($null -eq $snapshot) {
        Write-Output "  $Provider : no snapshot; status $auth/$freshness"
        return
    }
    $partition = $snapshot.Account.Partition
    Write-Output "  $Provider : origin=$origin auth=$auth freshness=$freshness source=$($snapshot.Source.Kind) partitionLength=$($partition.Length) headline=$($snapshot.HeadlineWindowId)"
    foreach ($window in $snapshot.Windows) {
        $percent = if ($null -eq $window.Limit.UsedFraction) { 'unavailable' } else { '{0:0.##}%' -f ($window.Limit.UsedFraction * 100) }
        Write-Output "    window $($window.Id) [$($window.Scope)] used=$percent resets=$($window.ResetsAt)"
    }
}

function Invoke-Click {
    param([int]$X, [int]$Y)

    [UseNotch.LiveSlice.Native]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 80
    [UseNotch.LiveSlice.Native]::mouse_event([UseNotch.LiveSlice.Native]::LeftDown, 0, 0, 0, [UIntPtr]::Zero)
    [UseNotch.LiveSlice.Native]::mouse_event([UseNotch.LiveSlice.Native]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
}

function Stop-Application {
    param([System.Diagnostics.Process]$Application)

    $shutdown = Start-Process -FilePath $applicationPath -ArgumentList '--smoke-quit' -WorkingDirectory $repositoryRoot -PassThru
    try {
        if (-not $shutdown.WaitForExit(8000) -or -not $Application.WaitForExit(8000)) {
            throw 'The application did not exit after the bounded shutdown signal.'
        }
        if ($Application.ExitCode -ne 0) {
            throw "The application exited with code $($Application.ExitCode)."
        }
    }
    finally {
        if (-not $shutdown.HasExited) { $shutdown.Kill() }
        $shutdown.Dispose()
    }
}

$arguments = @()
if ($Codex) { $arguments += '--enable-codex' }
if ($Claude) { $arguments += '--enable-claude' }

$online = $null
$offline = $null
try {
    foreach ($file in $expected) {
        if (Test-Path -LiteralPath $file.Path) {
            Remove-Item -LiteralPath $file.Path -Force
        }
    }

    Write-Output "Phase 1: online read with $($arguments -join ' ')"
    $online = Start-Process -FilePath $applicationPath -ArgumentList $arguments -WorkingDirectory $repositoryRoot -PassThru
    $overlay = Wait-ForWindow $online.Id 'UseNotch overlay'
    Wait-ForCacheFiles $expected

    $liveReadings = @{}
    foreach ($file in $expected) {
        $entry = Assert-SanitizedCache $file.Path $file.Provider
        if ($null -eq $entry.State.Snapshot) {
            throw "$($file.Provider) produced no live snapshot: $($entry.State.Status.Error.SafeMessage)"
        }
        $liveReadings[$file.Provider] = $entry
        Show-Reading $entry $file.Provider
    }

    $rectangle = [UseNotch.LiveSlice.Native+RECT]::new()
    if (-not [UseNotch.LiveSlice.Native]::GetWindowRect($overlay, [ref]$rectangle)) {
        throw 'Could not read the overlay bounds.'
    }
    Write-Output "  overlay bounds: $($rectangle.Left),$($rectangle.Top) $($rectangle.Right - $rectangle.Left)x$($rectangle.Bottom - $rectangle.Top)"

    Invoke-Click ($rectangle.Right - 38) ($rectangle.Top + 72)
    Wait-ForWindow $online.Id 'UseNotch overlay expanded' 5000 | Out-Null
    Write-Output '  detail pane opened from the OpenAI cell'
    Invoke-Click ($rectangle.Right - 38) ($rectangle.Top + 148)
    Wait-ForWindow $online.Id 'UseNotch overlay expanded' 5000 | Out-Null
    Write-Output '  detail pane opened from the Anthropic cell'

    Stop-Application $online
    $online.Dispose()
    $online = $null
    Write-Output '  bounded shutdown completed'

    Write-Output 'Phase 2: offline restart from the sanitized cache'
    # .NET resolves its default proxy from the per-user WinINET settings on Windows and ignores the
    # HTTP_PROXY environment variables there, so point that setting at an unreachable local port. This is
    # a current-user registry value, needs no elevation, and is restored in the finally block below.
    $proxyKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $originalProxy = Get-ItemProperty -Path $proxyKey
    $originalEnable = $originalProxy.ProxyEnable
    $originalServer = $originalProxy.ProxyServer
    try {
        Set-ItemProperty -Path $proxyKey -Name 'ProxyServer' -Value '127.0.0.1:1'
        Set-ItemProperty -Path $proxyKey -Name 'ProxyEnable' -Value 1 -Type DWord
        $offline = Start-Process -FilePath $applicationPath -ArgumentList $arguments -WorkingDirectory $repositoryRoot -PassThru
        Wait-ForWindow $offline.Id 'UseNotch overlay' | Out-Null

        $deadline = [Environment]::TickCount64 + 60000
        $offlineReadings = @{}
        while ([Environment]::TickCount64 -lt $deadline) {
            $offlineReadings = @{}
            foreach ($file in $expected) {
                $entry = Assert-SanitizedCache $file.Path $file.Provider
                if ($null -ne $entry.State.Status.Error) {
                    $offlineReadings[$file.Provider] = $entry
                }
            }
            if ($offlineReadings.Count -eq $expected.Count) { break }
            Start-Sleep -Milliseconds 500
        }
        if ($offlineReadings.Count -ne $expected.Count) {
            throw 'The offline restart did not record a request failure for every provider.'
        }

        foreach ($file in $expected) {
            $entry = $offlineReadings[$file.Provider]
            if ($null -eq $entry.State.Snapshot) {
                throw "$($file.Provider) discarded its last good reading while offline."
            }
            $live = $liveReadings[$file.Provider].State.Snapshot
            if ($entry.State.Snapshot.Account.Partition -ne $live.Account.Partition) {
                throw "$($file.Provider) changed its account partition while offline."
            }
            Show-Reading $entry $file.Provider
            Write-Output "    offline error: $($entry.State.Status.Error.SafeMessage) (retry $($entry.State.Status.NextAttempt))"
        }

        Stop-Application $offline
        $offline.Dispose()
        $offline = $null
    }
    finally {
        if ($null -eq $originalServer) {
            Remove-ItemProperty -Path $proxyKey -Name 'ProxyServer' -ErrorAction SilentlyContinue
        }
        else {
            Set-ItemProperty -Path $proxyKey -Name 'ProxyServer' -Value $originalServer
        }
        if ($null -eq $originalEnable) {
            Remove-ItemProperty -Path $proxyKey -Name 'ProxyEnable' -ErrorAction SilentlyContinue
        }
        else {
            Set-ItemProperty -Path $proxyKey -Name 'ProxyEnable' -Value $originalEnable -Type DWord
        }
        $restored = Get-ItemProperty -Path $proxyKey
        Write-Output "  restored proxy settings: enable=$($restored.ProxyEnable) server=$($restored.ProxyServer)"
    }

    Write-Output 'PASS: live source, request, snapshot, overlay, detail pane, sanitized cache, offline restart from cache, and bounded shutdown.'
}
finally {
    foreach ($process in @($online, $offline)) {
        if ($null -ne $process) {
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit(5000) | Out-Null
            }
            $process.Dispose()
        }
    }
}
