<#
  Release-gate performance/memory benchmark driver (headless, never touches the GUI).
  Generates deterministic synthetic traces OUTSIDE the repo, runs every stage in a fresh process
  (N repetitions), probes the CLI end to end, and aborts a size when free RAM drops below the guard.
  Usage: pwsh tools/RbgCanInsight.PerfBench/run-bench.ps1 [-Sizes 1000000,5000000,10000000] [-Reps 3] [-Work $env:TEMP\rbg-bench]
#>
param(
    [long[]]$Sizes = @(1000000, 5000000, 10000000),
    [int]$Reps = 3,
    [string]$Work = (Join-Path $env:TEMP 'rbg-bench'),
    [int]$GuardMB = 2048,
    [switch]$SkipCli,
    [switch]$CliOnly,
    [string]$CliPrefix = 'cli'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$bench = Join-Path $PSScriptRoot 'bin\Release\net10.0\RbgCanInsight.PerfBench.exe'
$cli = Join-Path $repo 'tools\RbgCanInsight.Cli\bin\Release\net10.0\rbgcan.exe'
$results = Join-Path $Work 'results'
New-Item -ItemType Directory -Force $Work, $results | Out-Null
$log = Join-Path $Work 'driver.log'
function Log($m) { $line = "{0:HH:mm:ss} {1}" -f (Get-Date), $m; Write-Host $line; Add-Content $log $line }
function FreeMB { [int]((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1024) }

Log "free RAM at start: $(FreeMB) MB; guard: $GuardMB MB"
foreach ($n in $Sizes) {
    $tag = if ($n % 1000000 -eq 0) { "$($n / 1000000)M" } else { "$n" }
    $asc = Join-Path $Work "trace-$tag.asc"; $blf = Join-Path $Work "trace-$tag.blf"
    $exp = Join-Path $Work "expected-$tag.json"; $dbc = Join-Path $Work 'bench.dbc'
    if (-not (Test-Path $exp)) { Log "generating $tag"; & $bench gen $Work $n | ForEach-Object { Log $_ } }
    $aborted = $false
    foreach ($fmt in 'asc', 'blf') {
        if ($aborted -or $CliOnly) { break }
        $trace = Join-Path $Work "trace-$tag.$fmt"
        for ($rep = 1; $rep -le $Reps -and -not $aborted; $rep++) {
            if ((FreeMB) -lt ($GuardMB + 500)) { Log "SKIP $tag/$fmt/rep$rep : free RAM $(FreeMB) MB too low to start"; $aborted = $true; break }
            $out = Join-Path $results "$tag-$fmt-rep$rep.json"
            Log "RUN $tag $fmt rep $rep (free $(FreeMB) MB)"
            & $bench run $trace $exp $dbc $out "--guard-mb=$GuardMB" 2>&1 | ForEach-Object { Log "  $_" }
            $code = $LASTEXITCODE
            Log "  exit $code"
            if ($code -eq 9) { Log "ABORT size $tag : free RAM fell below $GuardMB MB (see $out)"; $aborted = $true }
        }
    }
    if ($SkipCli -or $aborted) { continue }
    # End-to-end CLI probe: `rbgcan info <trace> --dbc bench.dbc` (loads the whole capture into a List<CanFrame>).
    foreach ($fmt in 'asc', 'blf') {
        $trace = Join-Path $Work "trace-$tag.$fmt"
        for ($rep = 1; $rep -le $Reps; $rep++) {
            if ((FreeMB) -lt ($GuardMB + 3000)) { Log "SKIP CLI $tag/$fmt/rep$rep : free RAM $(FreeMB) MB"; break }
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $psi = [Diagnostics.ProcessStartInfo]::new($cli, "info `"$trace`" --dbc `"$dbc`"")
            $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false
            $p = [Diagnostics.Process]::Start($psi)
            $stdout = $p.StandardOutput.ReadToEndAsync()
            $peakWs = 0L; $peakPriv = 0L; $killed = $false
            while (-not $p.HasExited) {
                try { $p.Refresh(); $peakWs = [Math]::Max($peakWs, $p.WorkingSet64); $peakPriv = [Math]::Max($peakPriv, $p.PrivateMemorySize64) } catch {}
                if ((FreeMB) -lt $GuardMB) { $p.Kill(); $killed = $true; break }
                Start-Sleep -Milliseconds 100
            }
            $p.WaitForExit(); $sw.Stop()
            $rec = [ordered]@{ size = $tag; format = $fmt; rep = $rep; exitCode = $p.ExitCode; killedByGuard = $killed
                wallMs = [int]$sw.ElapsedMilliseconds; polledPeakWorkingSetMB = [int]($peakWs / 1MB); polledPeakPrivateMB = [int]($peakPriv / 1MB)
                output = ($stdout.Result -split "`r?`n" | Where-Object { $_ }) }
            $rec | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $results "$CliPrefix-$tag-$fmt-rep$rep.json")
            Log "CLI $tag $fmt rep $rep : $($rec.wallMs) ms, peakWS $($rec.polledPeakWorkingSetMB) MB, exit $($p.ExitCode)"
            if ($killed) { Log "ABORT CLI size $tag (guard)"; break }
        }
    }
}
Log 'done'
