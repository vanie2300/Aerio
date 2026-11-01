<#
.SYNOPSIS
    Repeatable browser performance measurement harness (Phase 6 of the
    Aerio optimization roadmap).

.DESCRIPTION
    Two modes:

    Cold-start (default, requires -ExePath):
      Launches the browser several times and reports, per run:
        - Window-create time   (Process.Start -> main window handle exists)
        - Settle time          (window -> CPU drops below the idle threshold)
        - RAM and CPU snapshot (app process + WebView2 child processes)
      The session file is reset before each run so every run takes the same
      path (Start page). Use -SeedTabs N to pre-seed a session with N tabs.

    -ObserveRunning:
      Measures an already-running browser for -ObserveSeconds (RAM + CPU of the
      main process and its WebView2 children). Use this for the interactive
      multi-tab runs of the benchmark protocol (open tabs by hand, then measure).

    Works for any WebView2-based browser by parameterizing the process name and
    the child-process command-line filter (e.g. Aerio and Zephium).

.EXAMPLE
    ./measure-browser.ps1 -ExePath C:\path\Aerio.exe
    ./measure-browser.ps1 -ObserveRunning -ProcessName Aerio -WebviewFilter TabloidBrowser
    ./measure-browser.ps1 -ExePath C:\path\Zephium.exe -ProcessName Zephium -WebviewFilter Zephium
#>
param(
    [string]$ExePath,
    [string]$ProcessName = "Aerio",
    [string]$WebviewFilter = "TabloidBrowser",
    [switch]$ObserveRunning,
    [int]$ObserveSeconds = 30,
    [int]$Runs = 3,
    [int]$MinSettleSeconds = 12,
    [int]$SeedTabs = 0,
    [switch]$KeepSession,
    [string]$DotNetRoot,
    [string]$OutFile
)

$ErrorActionPreference = "Stop"

$SessionPath = Join-Path $env:LOCALAPPDATA "TabloidBrowser\Data\session.json"

function Get-WebviewProcesses([string]$filter) {
    Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match $filter } |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue } |
        Where-Object { $_ -is [System.Diagnostics.Process] }
}

function Sum-ProcessMetrics([System.Diagnostics.Process[]]$procs) {
    $ws = 0.0; $private = 0.0; $cpu = 0.0
    foreach ($p in $procs) {
        $ws += $p.WorkingSet64
        $private += $p.PrivateMemorySize64
        try { $cpu += $p.TotalProcessorTime.TotalSeconds } catch { }
    }
    [pscustomobject]@{
        Count      = $procs.Count
        WsMB       = [math]::Round($ws / 1MB, 1)
        PrivateMB  = [math]::Round($private / 1MB, 1)
        CpuSeconds = [math]::Round($cpu, 2)
    }
}

function Get-MainProcess([string]$name) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Stop-Browser([string]$name) {
    $main = Get-MainProcess $name
    if ($main) {
        $null = $main.CloseMainWindow()
        for ($i = 0; $i -lt 20; $i++) {
            Start-Sleep -Milliseconds 500
            $main.Refresh()
            if ($main.HasExited) { break }
        }
        if (-not $main.HasExited) { $main.Kill() }
    }
    Start-Sleep -Seconds 2
}

function Wait-Settled([System.Diagnostics.Process]$proc, [double]$windowCreateAt) {
    # Sample CPU deltas until the process is below the idle threshold for two
    # consecutive 1-second windows (startup burst finished). Cap at 60s.
    $idleThresholdPerSec = 0.15
    $prevCpu = 0.0
    try { $prevCpu = $proc.TotalProcessorTime.TotalSeconds } catch { return $windowCreateAt }
    $belowSince = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 1000
        try {
            $proc.Refresh()
            if ($proc.HasExited) { return $windowCreateAt }
            $cpu = $proc.TotalProcessorTime.TotalSeconds
            $delta = $cpu - $prevCpu
            $prevCpu = $cpu
            if ($delta -lt $idleThresholdPerSec) {
                if ($null -eq $belowSince) { $belowSince = [DateTime]::UtcNow }
                if (([DateTime]::UtcNow - $belowSince).TotalMilliseconds -ge 1900) {
                    return ([DateTime]::UtcNow - $belowSince).TotalSeconds + $windowCreateAt
                }
            } else {
                $belowSince = $null
            }
        } catch { return $windowCreateAt }
    }
    return 60
}

function Invoke-ColdStartRun([int]$index) {
    if (-not $KeepSession -and (Test-Path $SessionPath)) {
        Remove-Item $SessionPath -Force -ErrorAction SilentlyContinue
    }
    if ($SeedTabs -gt 0) {
        $tabs = 1..$SeedTabs | ForEach-Object {
            @{ Title = "Tab $_"; Url = "https://example.com/$_"; ScrollX = 0.0; ScrollY = 0.0 }
        }
        $session = @{ Tabs = $tabs; ActiveIndex = 0 } | ConvertTo-Json -Depth 4
        $dir = Split-Path $SessionPath
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
        Set-Content -Path $SessionPath -Value $session -Encoding UTF8
    }

    $t0 = [DateTime]::UtcNow
    # For framework-dependent builds where the required runtime lives in a
    # non-default location (e.g. a user-dir .NET install), point the apphost
    # at it; without this the .NET "runtime not found" dialog appears instead.
    $hadDotNetRoot = $null -ne $env:DOTNET_ROOT
    $prevDotNetRoot = $env:DOTNET_ROOT
    if ($DotNetRoot) {
        $env:DOTNET_ROOT = $DotNetRoot
    }
    $proc = Start-Process -FilePath $ExePath -PassThru
    if ($DotNetRoot) {
        if ($hadDotNetRoot) { $env:DOTNET_ROOT = $prevDotNetRoot }
        else { Remove-Item Env:\DOTNET_ROOT -ErrorAction SilentlyContinue }
    }
    $windowCreate = $null
    while (-not $windowCreate) {
        if (([DateTime]::UtcNow - $t0).TotalSeconds -gt 30) { throw "Window never appeared (run $index)" }
        Start-Sleep -Milliseconds 50
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
            $windowCreate = ([DateTime]::UtcNow - $t0).TotalSeconds
        } elseif ($proc.HasExited) {
            throw "Process exited during startup (run $index)"
        }
    }

    $settle = Wait-Settled $proc $windowCreate

    # The CPU-settle heuristic can fire during gaps in the async startup
    # pipeline (e.g. while awaiting WebView2 environment creation); hold the
    # run for a floor period so the full process tree exists before sampling.
    $elapsed = ([DateTime]::UtcNow - $t0).TotalSeconds
    if ($elapsed -lt $MinSettleSeconds) {
        Start-Sleep -Seconds ([int][math]::Ceiling($MinSettleSeconds - $elapsed))
    }

    Start-Sleep -Seconds 2
    $proc.Refresh()
    $main = Sum-ProcessMetrics @($proc)
    $webs = Sum-ProcessMetrics @(Get-WebviewProcesses $WebviewFilter)

    $result = [pscustomobject]@{
        Mode            = "ColdStart"
        Run             = $index
        SeedTabs        = $SeedTabs
        WindowCreateSec = [math]::Round($windowCreate, 2)
        SettleSec       = [math]::Round($settle, 2)
        Main            = $main
        WebViews        = $webs
    }
    Stop-Browser $ProcessName
    return $result
}

function Invoke-ObserveRunning {
    $main = Get-MainProcess $ProcessName
    if (-not $main) { throw "No running process named '$ProcessName' found." }
    $main.Refresh()
    $cpuBefore = Sum-ProcessMetrics @($main)
    $websBefore = Sum-ProcessMetrics @(Get-WebviewProcesses $WebviewFilter)
    $cpuStart = $cpuBefore.CpuSeconds + $websBefore.CpuSeconds

    Start-Sleep -Seconds $ObserveSeconds

    $main.Refresh()
    $cpuAfter = Sum-ProcessMetrics @($main)
    $websAfter = Sum-ProcessMetrics @(Get-WebviewProcesses $WebviewFilter)
    $cpuEnd = $cpuAfter.CpuSeconds + $websAfter.CpuSeconds

    return [pscustomobject]@{
        Mode            = "ObserveRunning"
        ObserveSeconds  = $ObserveSeconds
        Main            = $cpuAfter
        WebViews        = $websAfter
        IdleCpuPercent  = [math]::Round((($cpuEnd - $cpuStart) / $ObserveSeconds) * 100, 1)
        IdleCpuCoreSec  = [math]::Round($cpuEnd - $cpuStart, 2)
    }
}

# --- main ---
$results = @()
if ($ObserveRunning) {
    $results += Invoke-ObserveRunning
} else {
    if (-not $ExePath -or -not (Test-Path $ExePath)) { throw "Provide -ExePath pointing at the browser executable." }
    for ($i = 1; $i -le $Runs; $i++) {
        Write-Host "--- Cold-start run $i of $Runs ---"
        $results += Invoke-ColdStartRun $i
    }
    if (-not $KeepSession -and (Test-Path $SessionPath)) {
        Remove-Item $SessionPath -Force -ErrorAction SilentlyContinue
    }
}

foreach ($r in $results) {
    $r | Format-List | Out-String -Stream | Where-Object { $_ }
}

if ($OutFile) {
    $results | ConvertTo-Json -Depth 5 | Set-Content -Path $OutFile -Encoding UTF8
    Write-Host "Results written to $OutFile"
}
