[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

# Verifies the overlay lands on the requested edge of the monitor work area, so taskbar offsets are
# respected rather than assumed. Settings drive placement, exactly as they do for a user.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw 'Run this check in PowerShell 7 on an interactive Windows desktop.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$applicationPath = Join-Path $repositoryRoot "src/UseNotch.App/bin/$Configuration/net8.0-windows/UseNotch.App.exe"
if (-not (Test-Path -LiteralPath $applicationPath)) {
    throw "Build the application in $Configuration first."
}

if (Get-Process -Name 'UseNotch.App' -ErrorAction SilentlyContinue) {
    throw 'A UseNotch process is already running. Quit it before running this check.'
}

# The application is per-monitor DPI aware. This check has to be too, or every rectangle it reads back
# is virtualised and the comparison is meaningless.
if (-not ('UseNotch.Placement.Dpi' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace UseNotch.Placement
{
    public static class Dpi
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        public static void MakeAware()
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4));
        }
    }
}
'@
}

[UseNotch.Placement.Dpi]::MakeAware()

if (-not ('UseNotch.Placement.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

namespace UseNotch.Placement
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
        private static extern bool IsWindowVisible(IntPtr window);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

        public static string GetTitle(IntPtr window)
        {
            var text = new StringBuilder(512);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }

        /// <summary>
        /// Finds the overlay window, which is the small one. The process also owns a hidden full-screen
        /// helper window, so matching on the title alone can pick the wrong one.
        /// </summary>
        public static IntPtr FindByPrefix(int processId, string prefix)
        {
            IntPtr found = IntPtr.Zero;
            int foundArea = int.MaxValue;
            EnumWindows((window, _) =>
            {
                uint candidateProcessId;
                GetWindowThreadProcessId(window, out candidateProcessId);
                // The process also owns invisible helper windows. Only a visible one can be the overlay.
                if (candidateProcessId != processId
                    || !IsWindowVisible(window)
                    || !GetTitle(window).StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }

                RECT rectangle = new RECT();
                if (!GetWindowRect(window, ref rectangle))
                {
                    return true;
                }

                int area = (rectangle.Right - rectangle.Left) * (rectangle.Bottom - rectangle.Top);
                if (area > 0 && area < foundArea)
                {
                    found = window;
                    foundArea = area;
                }

                return true;
            }, IntPtr.Zero);
            return found;
        }
    }
}
'@
}

Add-Type -AssemblyName System.Windows.Forms
$workArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
Write-Output "Primary work area: $($workArea.X),$($workArea.Y) $($workArea.Width)x$($workArea.Height)"

$settingsPath = Join-Path $env:LOCALAPPDATA 'UseNotch/settings.json'
$backupPath = "$settingsPath.placementbak"
$hadSettings = Test-Path -LiteralPath $settingsPath
if ($hadSettings) {
    Copy-Item -LiteralPath $settingsPath -Destination $backupPath -Force
}

function Set-Edge {
    param([string]$Edge)

    $settings = @{
        SchemaVersion = 1
        OpenAi        = @{ Enabled = $false; SelectedRoot = $null }
        Anthropic     = @{ Enabled = $false; SelectedRoot = $null }
        Overlay       = @{ MonitorId = $null; Edge = $Edge; OffsetX = 0; OffsetY = 0; Pinned = $false; Visible = $true; UiScale = 1.0; ReducedMotion = $false; VisibleOverFullScreen = $false }
        Privacy       = @{ ActivityMonitoringEnabled = $false; DiagnosticsEnabled = $false }
        LaunchAtLogin = $false
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $settingsPath) -Force | Out-Null
    $settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $settingsPath
}

$failures = @()
try {
    foreach ($edge in @('Right', 'Left', 'Top', 'Bottom')) {
        Set-Edge $edge
        # Settings alone drive this, with both providers disabled, so the only thing that places the
        # overlay is the stored edge. The smoke switch is deliberately not used: it would show the overlay
        # once at the default edge before settings are applied.
        $application = Start-Process -FilePath $applicationPath -WorkingDirectory $repositoryRoot -PassThru
        try {
            $window = [IntPtr]::Zero
            $deadline = [Environment]::TickCount64 + 20000
            while ([Environment]::TickCount64 -lt $deadline -and $window -eq [IntPtr]::Zero) {
                $window = [UseNotch.Placement.Native]::FindByPrefix($application.Id, 'UseNotch overlay')
                if ($window -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 200 }
            }

            if ($window -eq [IntPtr]::Zero) {
                throw "The overlay window never appeared for edge $edge."
            }

            $rectangle = [UseNotch.Placement.Native+RECT]::new()
            [void][UseNotch.Placement.Native]::GetWindowRect($window, [ref]$rectangle)
            $width = $rectangle.Right - $rectangle.Left
            $height = $rectangle.Bottom - $rectangle.Top
            Write-Output ("  {0,-6} -> {1},{2} {3}x{4}" -f $edge, $rectangle.Left, $rectangle.Top, $width, $height)

            $tolerance = 2
            switch ($edge) {
                'Right' { if ([math]::Abs($rectangle.Right - ($workArea.X + $workArea.Width)) -gt $tolerance) { $failures += "Right edge landed at $($rectangle.Right) rather than $($workArea.X + $workArea.Width)." } }
                'Left' { if ([math]::Abs($rectangle.Left - $workArea.X) -gt $tolerance) { $failures += "Left edge landed at $($rectangle.Left) rather than $($workArea.X)." } }
                'Top' { if ([math]::Abs($rectangle.Top - $workArea.Y) -gt $tolerance) { $failures += "Top edge landed at $($rectangle.Top) rather than $($workArea.Y)." } }
                'Bottom' { if ([math]::Abs($rectangle.Bottom - ($workArea.Y + $workArea.Height)) -gt $tolerance) { $failures += "Bottom edge landed at $($rectangle.Bottom) rather than $($workArea.Y + $workArea.Height)." } }
            }

            # The work area excludes the taskbar, so staying inside it proves the offset is respected.
            if ($rectangle.Left -lt $workArea.X - $tolerance -or $rectangle.Top -lt $workArea.Y - $tolerance -or
                $rectangle.Right -gt $workArea.X + $workArea.Width + $tolerance -or
                $rectangle.Bottom -gt $workArea.Y + $workArea.Height + $tolerance) {
                $failures += "Edge $edge placed the overlay outside the work area."
            }
        }
        finally {
            $shutdown = Start-Process -FilePath $applicationPath -ArgumentList '--smoke-quit' -WorkingDirectory $repositoryRoot -PassThru
            $shutdown.WaitForExit(10000) | Out-Null
            if (-not $application.WaitForExit(10000)) { $application.Kill() }
            $shutdown.Dispose()
            $application.Dispose()
            Start-Sleep -Milliseconds 500
        }
    }
}
finally {
    if ($hadSettings) {
        Move-Item -LiteralPath $backupPath -Destination $settingsPath -Force
    }
    elseif (Test-Path -LiteralPath $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output "FAIL: $_" }
    throw "$($failures.Count) placement check(s) failed."
}

Write-Output 'PASS: every edge placed the overlay against the requested side of the work area, inside the taskbar offset.'
