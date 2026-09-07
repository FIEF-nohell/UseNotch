[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NegativeMonitor
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
        private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>
        /// Windows only lets the foreground process hand focus away. This check runs from a background
        /// shell, so borrow the current foreground thread's input queue long enough to place focus on the
        /// independent target. This affects only the harness, never the overlay behaviour under test.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        public static bool ForceForeground(IntPtr window)
        {
            if (SetForegroundWindow(window) && GetForegroundWindow() == window)
            {
                return true;
            }

            // Windows grants foreground rights to a process that owns the most recent input event. A
            // benign ALT press and release satisfies that rule without typing anything anywhere.
            const byte AltKey = 0x12;
            const uint KeyUp = 0x0002;
            keybd_event(AltKey, 0, 0, UIntPtr.Zero);
            keybd_event(AltKey, 0, KeyUp, UIntPtr.Zero);
            if (SetForegroundWindow(window) && GetForegroundWindow() == window)
            {
                return true;
            }

            IntPtr current = GetForegroundWindow();
            uint currentProcessId;
            uint currentThread = GetWindowThreadProcessId(current, out currentProcessId);
            uint thisThread = GetCurrentThreadId();
            if (currentThread == 0 || currentThread == thisThread)
            {
                return GetForegroundWindow() == window;
            }

            AttachThreadInput(thisThread, currentThread, true);
            try
            {
                SetForegroundWindow(window);
            }
            finally
            {
                AttachThreadInput(thisThread, currentThread, false);
            }

            return GetForegroundWindow() == window;
        }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr window, out RECT rectangle);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        public static extern int GetWindowRgn(IntPtr window, IntPtr region);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        public static extern int GetRgnBox(IntPtr region, out RECT rectangle);

        [DllImport("gdi32.dll")]
        public static extern bool PtInRegion(IntPtr region, int x, int y);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr objectHandle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder buffer, int maximumCount);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT point);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        public static IntPtr WindowAt(int x, int y)
        {
            POINT point;
            point.X = x;
            point.Y = y;
            return WindowFromPoint(point);
        }

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
    # The overlay decides click-through from the cursor position on a short poll, exactly as it does for a
    # real pointer. Let that settle before pressing, instead of clicking in the same instant as the move.
    Start-Sleep -Milliseconds 200
    [UseNotch.OverlaySmoke.Native]::mouse_event([UseNotch.OverlaySmoke.Native]::LeftDown, 0, 0, 0, [UIntPtr]::Zero)
    [UseNotch.OverlaySmoke.Native]::mouse_event([UseNotch.OverlaySmoke.Native]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
}

$targetProcess = $null
$overlayProcess = $null
$shutdownProcess = $null
try {
    if ($NegativeMonitor) {
        Add-Type -AssemblyName System.Windows.Forms
        $negativeScreen = [System.Windows.Forms.Screen]::AllScreens | Where-Object { $_.Bounds.X -lt 0 -or $_.Bounds.Y -lt 0 }
        if (-not $negativeScreen) {
            throw 'This check needs a secondary monitor placed at negative coordinates. None is attached, so the negative-coordinate case cannot be verified here.'
        }
    }

    $targetProcess = Start-Process -FilePath $harnessPath -WorkingDirectory $repositoryRoot -PassThru
    $targetWindow = Wait-ForWindow $targetProcess.Id 'UseNotch overlay input target | clicks=0 | wheels=0'
    if ($NegativeMonitor) {
        [UseNotch.OverlaySmoke.Native]::SetWindowPos($targetWindow, [IntPtr]::Zero, -900, -1400, 3000, 2000, 0x0040) | Out-Null
    }
    $focused = $false
    for ($attempt = 0; $attempt -lt 5 -and -not $focused; $attempt++) {
        $focused = [UseNotch.OverlaySmoke.Native]::ForceForeground($targetWindow)
        if (-not $focused) {
            Start-Sleep -Milliseconds 250
        }
    }
    if (-not $focused) {
        throw 'The independent input target could not receive foreground focus.'
    }

    $overlayArguments = @('--overlay-smoke')
    if ($NegativeMonitor) {
        $overlayArguments += '--overlay-smoke-negative'
    }
    $overlayProcess = Start-Process -FilePath $applicationPath -ArgumentList $overlayArguments -WorkingDirectory $repositoryRoot -PassThru
    $overlayWindow = Wait-ForWindow $overlayProcess.Id 'UseNotch overlay'
    Start-Sleep -Milliseconds 500
    if ([UseNotch.OverlaySmoke.Native]::GetForegroundWindow() -ne $targetWindow) {
        throw 'Showing the overlay stole foreground focus.'
    }

    $rectangle = [UseNotch.OverlaySmoke.Native+RECT]::new()
    if (-not [UseNotch.OverlaySmoke.Native]::GetWindowRect($overlayWindow, [ref]$rectangle)) {
        throw 'Could not read the overlay bounds.'
    }

    $region = [UseNotch.OverlaySmoke.Native]::CreateRectRgn(0, 0, 0, 0)
    try {
        $regionStatus = [UseNotch.OverlaySmoke.Native]::GetWindowRgn($overlayWindow, $region)
        $regionBounds = [UseNotch.OverlaySmoke.Native+RECT]::new()
        [UseNotch.OverlaySmoke.Native]::GetRgnBox($region, [ref]$regionBounds) | Out-Null
        Write-Output "Overlay bounds: $($rectangle.Left),$($rectangle.Top) $($rectangle.Right - $rectangle.Left)x$($rectangle.Bottom - $rectangle.Top); region=$regionStatus box=$($regionBounds.Left),$($regionBounds.Top) $($regionBounds.Right - $regionBounds.Left)x$($regionBounds.Bottom - $regionBounds.Top)"
    }
    finally {
        [UseNotch.OverlaySmoke.Native]::DeleteObject($region) | Out-Null
    }

    # A real click and wheel event decide this, not WindowFromPoint, which does not run the window's own
    # hit test and therefore cannot see the transparent result.
    $transparentX = $rectangle.Left + 8
    $transparentY = $rectangle.Top + 8
    Invoke-Click $transparentX $transparentY
    [UseNotch.OverlaySmoke.Native]::mouse_event([UseNotch.OverlaySmoke.Native]::Wheel, 0, 0, 120, [UIntPtr]::Zero)
    $targetWindow = Wait-ForWindow $targetProcess.Id 'UseNotch overlay input target | clicks=1 | wheels=1'
    if ([UseNotch.OverlaySmoke.Native]::GetForegroundWindow() -ne $targetWindow) {
        throw 'Transparent overlay space did not preserve target focus.'
    }

    # The collapsed handle is right-aligned and vertically centred. Hovering it expands the overlay.
    $handleX = $rectangle.Right - 32
    $handleY = [int](($rectangle.Top + $rectangle.Bottom) / 2)
    [UseNotch.OverlaySmoke.Native]::SetCursorPos($handleX, $handleY) | Out-Null
    $overlayWindow = Wait-ForWindow $overlayProcess.Id 'UseNotch overlay open'
    if ([UseNotch.OverlaySmoke.Native]::GetForegroundWindow() -ne $targetWindow) {
        throw 'Hovering the overlay stole foreground focus.'
    }

    # Once expanded, the two provider cells are right-aligned and stacked around the vertical centre.
    # Aim at the middle of the upper cell rather than the gap between them.
    $cellX = $rectangle.Right - 80
    $cellY = $handleY - 46
    Invoke-Click $cellX $cellY
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

    Write-Output 'PASS: transparent space belonged to the independent process for both the hit test and real input; hovering the handle expanded the overlay; the visible cell opened details without foreground activation; overlay exited cleanly.'
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
