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
try {
    $applicationProcess = Start-Process -FilePath $applicationPath -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru
    if (-not $applicationProcess.WaitForInputIdle(10000)) {
        throw 'The application did not finish native UI initialization within 10 seconds.'
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $applicationProcess.Refresh()
        if ($applicationProcess.HasExited) {
            throw "The application exited before opening its window (exit $($applicationProcess.ExitCode))."
        }
        if ($applicationProcess.MainWindowHandle -ne [IntPtr]::Zero) {
            break
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($applicationProcess.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'No native application window appeared.'
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

    if (-not $applicationProcess.CloseMainWindow() -or -not $applicationProcess.WaitForExit(5000)) {
        throw 'Closing the M01 foundation window did not end the application within five seconds.'
    }
    if ($applicationProcess.ExitCode -ne 0) {
        throw "Application shutdown failed (exit $($applicationProcess.ExitCode))."
    }

    Write-Output 'PASS: native window and binding loaded; process non-elevated; PerMonitorV2; clean exit.'
}
finally {
    if ($null -ne $applicationProcess) {
        if (-not $applicationProcess.HasExited) {
            # Only clean up the process started by this smoke check.
            $applicationProcess.Kill()
            $applicationProcess.WaitForExit(5000) | Out-Null
        }
        $applicationProcess.Dispose()
    }
}
