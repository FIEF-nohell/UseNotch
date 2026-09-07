[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [int]$DurationMinutes = 60,
    [int]$SampleSeconds = 30,
    [string]$OutputPath
)

# Runs UseNotch for a bounded period and records resource use. Only counters are written: no account
# data, no provider response, and no credential ever reaches the report.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw 'Run this measurement in PowerShell 7 on Windows.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$applicationPath = Join-Path $repositoryRoot "src/UseNotch.App/bin/$Configuration/net8.0-windows/UseNotch.App.exe"
if (-not (Test-Path -LiteralPath $applicationPath)) {
    throw "Build the application in $Configuration first."
}

if (-not $OutputPath) {
    $OutputPath = Join-Path $repositoryRoot 'artifacts/soak/resources.csv'
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$cacheRoot = Join-Path $env:LOCALAPPDATA 'UseNotch/cache'

function Get-CacheBytes {
    if (-not (Test-Path -LiteralPath $cacheRoot)) { return 0 }
    return (Get-ChildItem -LiteralPath $cacheRoot -Recurse -File | Measure-Object -Property Length -Sum).Sum
}

$application = $null
$samples = @()
try {
    $application = Start-Process -FilePath $applicationPath -WorkingDirectory $repositoryRoot -PassThru
    Start-Sleep -Seconds 10
    if ($application.HasExited) {
        throw "The application exited immediately with code $($application.ExitCode)."
    }

    $process = Get-Process -Id $application.Id
    $startCpu = $process.TotalProcessorTime
    $startCacheBytes = Get-CacheBytes
    $deadline = (Get-Date).AddMinutes($DurationMinutes)
    $previousCpu = $startCpu
    $previousAt = Get-Date

    Write-Output "Sampling every $SampleSeconds s until $deadline"
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $SampleSeconds
        $process.Refresh()
        if ($process.HasExited) {
            throw 'The application exited during the measurement.'
        }

        $now = Get-Date
        $cpu = $process.TotalProcessorTime
        # Per-sample CPU as a percentage of one core, which is what an idle claim has to stand on.
        $cpuPercent = [math]::Round((($cpu - $previousCpu).TotalSeconds / ($now - $previousAt).TotalSeconds) * 100, 2)
        $previousCpu = $cpu
        $previousAt = $now

        $samples += [pscustomobject]@{
            TimestampUtc     = $now.ToUniversalTime().ToString('o')
            ElapsedMinutes   = [math]::Round(($now - $previousAt).TotalMinutes + ($DurationMinutes - ($deadline - $now).TotalMinutes), 2)
            CpuPercentOfCore = $cpuPercent
            WorkingSetMB     = [math]::Round($process.WorkingSet64 / 1MB, 1)
            PrivateBytesMB   = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
            Handles          = $process.HandleCount
            Threads          = $process.Threads.Count
            CacheBytes       = Get-CacheBytes
        }

        $latest = $samples[-1]
        Write-Output ("  cpu {0}% ws {1} MB private {2} MB handles {3} threads {4} cache {5} B" -f $latest.CpuPercentOfCore, $latest.WorkingSetMB, $latest.PrivateBytesMB, $latest.Handles, $latest.Threads, $latest.CacheBytes)
    }

    $samples | Export-Csv -LiteralPath $OutputPath -NoTypeInformation
    $first = $samples[0]
    $last = $samples[-1]
    Write-Output ''
    Write-Output "Samples: $($samples.Count) over $DurationMinutes minute(s)"
    Write-Output ("Average CPU: {0}% of one core" -f [math]::Round((($samples | Measure-Object -Property CpuPercentOfCore -Average).Average), 2))
    Write-Output ("Working set: {0} MB -> {1} MB (peak {2} MB)" -f $first.WorkingSetMB, $last.WorkingSetMB, ($samples | Measure-Object -Property WorkingSetMB -Maximum).Maximum)
    Write-Output ("Handles: {0} -> {1}; threads: {2} -> {3}" -f $first.Handles, $last.Handles, $first.Threads, $last.Threads)
    Write-Output ("Cache growth: {0} bytes" -f ($last.CacheBytes - $startCacheBytes))
    Write-Output "Report: $OutputPath"
}
finally {
    if ($null -ne $application -and -not $application.HasExited) {
        $shutdown = Start-Process -FilePath $applicationPath -ArgumentList '--smoke-quit' -WorkingDirectory $repositoryRoot -PassThru
        $shutdown.WaitForExit(10000) | Out-Null
        if (-not $application.WaitForExit(10000)) {
            $application.Kill()
        }

        $shutdown.Dispose()
    }

    if ($null -ne $application) { $application.Dispose() }
}
