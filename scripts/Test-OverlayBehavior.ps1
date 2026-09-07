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
    throw 'Run from a non-elevated shell to verify standard-user overlay behavior.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$applicationPath = Join-Path $repositoryRoot "src/UseNotch.App/bin/$Configuration/net8.0-windows/UseNotch.App.exe"
$harnessPath = Join-Path $repositoryRoot "tests/OverlayInputHarness/bin/$Configuration/net8.0-windows/OverlayInputHarness.exe"
if (-not (Test-Path -LiteralPath $applicationPath) -or -not (Test-Path -LiteralPath $harnessPath)) {
    throw 'Build the application and OverlayInputHarness before running the overlay smoke check.'
}

if (-not ('UseNotch.OverlaySmoke.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

namespace UseNotch.OverlaySmoke
{
    public static class Native
    {
        [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr window, out RECT rectangle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder buffer, int maximumCount);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);

        public static string GetTitle(IntPtr window)
        {
            var buffer = new StringBuilder(512);
            GetWindowText(window, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        public static IntPtr FindWindowForProcess(int processId, string title)
        {
            IntPtr result = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                uint candidateProcessId;
                GetWindowThreadProcessId(window, out candidateProcessId);
                if (candidateProcessId == processId && GetTitle(window) == title)
                {
                    result = window;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public const uint LeftDown = 0x0002;
        public const uint LeftUp = 0x0004;
        public const uint Wheel = 0x0800;

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
'@
}

function Wait-ForWindow([int]$processId, [string]$title, [int]$timeoutMilliseconds = 10000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMilliseconds)
    do {
        $window = [UseNotch.OverlaySmoke.Native]::FindWindowForProcess($processId, $title)
        if ($window -ne [IntPtr]::Zero) {
            return $window
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Window '$title' did not appear."
}

function Invoke-Click([int]$x, [int]$y) {
    [UseNotch.OverlaySmoke.Native]::SetCursorPos($x, $y) | Out-Null
    [UseNotch.OverlaySmoke.Native]::mouse_event([UseNotch.OverlaySmoke.Native]::LeftDown, 0, 0, 0, [UIntPtr]::Zero)
    [UseNotch.OverlaySmoke.Native]::mouse_event([UseNotch.OverlaySmoke.Native]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
}

$targetProcess = $null
$overlayProcess = $null
$shutdownProcess = $null
try {
    $targetProcess = Start-Process -FilePath $harnessPath -WorkingDirectory $repositoryRoot -PassThru
    $targetWindow = Wait-ForWindow $targetProcess.Id 'UseNotch overlay input target | clicks=0 | wheels=0'
    [UseNotch.OverlaySmoke.Native]::SetForegroundWindow($targetWindow) | Out-Null
    Start-Sleep -Milliseconds 250
    if ([UseNotch.OverlaySmoke.Native]::GetForegroundWindow() -ne $targetWindow) {
        throw 'The independent input target could not receive foreground focus.'
    }

    $overlayProcess = Start-Process -FilePath $applicationPath -ArgumentList '--overlay-smoke' -WorkingDirectory $repositoryRoot -PassThru
    $overlayWindow = Wait-ForWindow $overlayProcess.Id 'UseNotch overlay'
    if ([UseNotch.OverlaySmoke.Native]::GetForegroundWindow() -ne $targetWindow) {
        throw 'Showing the overlay stole foreground focus.'
    }

    $rectangle = [UseNotch.OverlaySmoke.Native+RECT]::new()
    if (-not [UseNotch.OverlaySmoke.Native]::GetWindowRect($overlayWindow, [ref]$rectangle)) {
        throw 'Could not read the overlay bounds.'
    }

    Invoke-Click ($rectangle.Left + 8) ($rectangle.Top + 8)
    [UseNotch.OverlaySmoke.Native]::mouse_event([UseNotch.OverlaySmoke.Native]::Wheel, 0, 0, 120, [UIntPtr]::Zero)
    $targetWindow = Wait-ForWindow $targetProcess.Id 'UseNotch overlay input target | clicks=1 | wheels=1'
    if ([UseNotch.OverlaySmoke.Native]::GetForegroundWindow() -ne $targetWindow) {
        throw 'Transparent overlay space did not preserve target focus.'
    }

    Invoke-Click ($rectangle.Right - 38) ($rectangle.Top + 72)
    Start-Sleep -Milliseconds 250
    $targetTitleAfterVisibleClick = [UseNotch.OverlaySmoke.Native]::GetTitle($targetWindow)
    if ($targetTitleAfterVisibleClick -ne 'UseNotch overlay input target | clicks=1 | wheels=1') {
        throw "Visible overlay cell did not own the click. Target title: $targetTitleAfterVisibleClick"
    }
    $overlayWindow = Wait-ForWindow $overlayProcess.Id 'UseNotch overlay expanded'
    if ([UseNotch.OverlaySmoke.Native]::GetForegroundWindow() -ne $targetWindow) {
        throw 'Interactive overlay controls activated the overlay window.'
    }

    $shutdownProcess = Start-Process -FilePath $applicationPath -ArgumentList '--smoke-quit' -WorkingDirectory $repositoryRoot -PassThru
    if (-not $shutdownProcess.WaitForExit(5000) -or -not $overlayProcess.WaitForExit(5000)) {
        throw 'The overlay process did not exit after the bounded shutdown signal.'
    }
    if ($overlayProcess.ExitCode -ne 0) {
        throw "The overlay process exited with code $($overlayProcess.ExitCode)."
    }

    Write-Output 'PASS: transparent corner click and wheel reached an independent process; visible cell expanded without foreground activation; overlay exited cleanly.'
}
finally {
    foreach ($process in @($shutdownProcess, $overlayProcess, $targetProcess)) {
        if ($null -ne $process) {
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit(5000) | Out-Null
            }
            $process.Dispose()
        }
    }
}
