[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'Run this smoke check in PowerShell 7 on an interactive Windows desktop.'
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run from a non-elevated shell to verify the standard-user startup requirement.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$applicationPath = Join-Path $repositoryRoot "src/UseNotch.App/bin/$Configuration/net8.0-windows/UseNotch.App.exe"
if (-not (Test-Path -LiteralPath $applicationPath)) {
    throw 'Build the solution before running the desktop smoke check.'
}

# Test-only native inspection; production interop belongs in Platform.Windows.
if (-not ('UseNotch.Smoke.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UseNotch.Smoke
{
    public static class Native
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
            out int information, int length, out int returnedLength);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

        public static bool IsElevated(IntPtr process)
        {
            if (!OpenProcessToken(process, 0x0008, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            using (token)
            {
                if (!GetTokenInformation(token, 20, out var elevated, sizeof(int), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return elevated != 0;
            }
        }

        public static bool IsPerMonitorV2(IntPtr window) =>
            AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(window), new IntPtr(-4));
    }
}
'@
}

$applicationProcess = $null
$activationProcess = $null
$shutdownProcess = $null
try {
    $applicationProcess = Start-Process -FilePath $applicationPath -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 750
    $applicationProcess.Refresh()
    if ($applicationProcess.HasExited) {
        throw "The tray owner exited before activation (exit $($applicationProcess.ExitCode))."
    }

    $activationProcess = Start-Process -FilePath $applicationPath -ArgumentList @('--show-status') -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru
    if (-not $activationProcess.WaitForExit(5000)) {
        throw 'The second launch did not return after signalling the existing instance.'
    }
    if ($activationProcess.ExitCode -ne 0) {
        throw "The second launch failed (exit $($activationProcess.ExitCode))."
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $applicationProcess.Refresh()
        if ($applicationProcess.HasExited) {
            throw "The tray owner exited before opening its status window (exit $($applicationProcess.ExitCode))."
        }
        if ($applicationProcess.MainWindowHandle -ne [IntPtr]::Zero) {
            break
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($applicationProcess.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'No native status window appeared after activation.'
    }
    if ($applicationProcess.MainWindowTitle -ne 'UseNotch') {
        throw 'The native window title did not receive its compiled binding.'
    }
    if ([UseNotch.Smoke.Native]::IsElevated($applicationProcess.Handle)) {
        throw 'The application unexpectedly runs elevated.'
    }
    if (-not [UseNotch.Smoke.Native]::IsPerMonitorV2($applicationProcess.MainWindowHandle)) {
        throw 'The native window is not per-monitor-v2 DPI aware.'
    }

    if (-not $applicationProcess.CloseMainWindow()) {
        throw 'The status window rejected its close request.'
    }
    Start-Sleep -Milliseconds 500
    $applicationProcess.Refresh()
    if ($applicationProcess.HasExited) {
        throw 'Closing the status window ended the tray owner instead of hiding the window.'
    }

    $shutdownProcess = Start-Process -FilePath $applicationPath -ArgumentList @('--smoke-quit') -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru
    if (-not $shutdownProcess.WaitForExit(5000)) {
        throw 'The shutdown signal process did not return.'
    }
    if ($shutdownProcess.ExitCode -ne 0 -or -not $applicationProcess.WaitForExit(5000)) {
        throw 'The tray owner did not exit cleanly after the shutdown signal.'
    }
    if ($applicationProcess.ExitCode -ne 0) {
        throw "Application shutdown failed (exit $($applicationProcess.ExitCode))."
    }

    Write-Output 'PASS: one tray owner accepted activation; status window bound; non-elevated; PerMonitorV2; close hid; shutdown clean.'
}
finally {
    foreach ($process in @($shutdownProcess, $activationProcess, $applicationProcess)) {
        if ($null -ne $process) {
            if (-not $process.HasExited) {
                # Only clean up processes started by this smoke check.
                $process.Kill()
                $process.WaitForExit(5000) | Out-Null
            }
            $process.Dispose()
        }
    }
}
