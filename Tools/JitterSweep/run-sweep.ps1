<#
.SYNOPSIS
    Runs the NDI jitter sweep — one sender + one receiver per config row.

.DESCRIPTION
    Reads Tools/JitterSweep/configs.jsonl (one JSON object per line), and for each row:
      1. Spawns the sender (Tractus.HtmlToNdi.exe) with the config's CLI flags.
      2. Polls the sender log for "Application started" (max 15 s).
      3. Runs the NdiTelemetryReceiver against the source for the configured duration.
      4. Kills the sender, waits 3 s for cleanup.
      5. Parses the sender log via SenderTelemetryParser over the receiver's window.
      6. Merges sender + receiver JSON into a row appended to Docs/jitter-test-results.md.

    Resumable: scans the existing results doc for run_ids already present and skips them.
    Robust: a sender crash, NDI find timeout, or receiver timeout produces a row with pass=ERROR
            and the sweep continues with the next config.

.PARAMETER ConfigsPath
    Path to configs.jsonl. Default: Tools/JitterSweep/configs.jsonl.

.PARAMETER ResultsPath
    Path to results markdown. Default: Docs/jitter-test-results.md.

.PARAMETER Filter
    Optional regex matched against run_id; only configs whose run_id matches will run.

.PARAMETER MaxRuns
    Optional cap on the number of configs run in this invocation.

.EXAMPLE
    pwsh ./run-sweep.ps1
    pwsh ./run-sweep.ps1 -Filter '^T0_'      # only T0 configs
    pwsh ./run-sweep.ps1 -MaxRuns 3
#>

[CmdletBinding()]
param(
    [string]$ConfigsPath = (Join-Path $PSScriptRoot 'configs.jsonl'),
    [string]$ResultsPath = (Join-Path (Join-Path $PSScriptRoot '..\..\Docs') 'jitter-test-results.md'),
    [string]$Filter,
    [int]$MaxRuns = [int]::MaxValue
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

# Resolve paths.
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$senderExe = Join-Path $repoRoot 'bin\Debug\net8.0-windows\win-x64\Tractus.HtmlToNdi.exe'
$receiverExe = Join-Path $repoRoot 'Tools\NdiTelemetryReceiver\bin\Debug\net8.0\win-x64\NdiTelemetryReceiver.exe'
$parserDll = Join-Path $repoRoot 'Tools\SenderTelemetryParser\bin\Debug\net8.0\SenderTelemetryParser.dll'
$runsDir = Join-Path $PSScriptRoot 'runs'
$pagesDir = Join-Path $PSScriptRoot 'test-pages'
$pageHttpPort = 18080

if (-not (Test-Path $senderExe)) { throw "Sender exe not found: $senderExe. Build the main project first." }
if (-not (Test-Path $receiverExe)) { throw "Receiver exe not found: $receiverExe. Build Tools/NdiTelemetryReceiver first." }
if (-not (Test-Path $parserDll)) { throw "Parser dll not found: $parserDll. Build Tools/SenderTelemetryParser first." }
if (-not (Test-Path $ConfigsPath)) { throw "Configs file not found: $ConfigsPath. Run generate-configs.ps1 first." }
if (-not (Test-Path $ResultsPath)) { throw "Results file not found: $ResultsPath. Initialize the doc first." }
New-Item -ItemType Directory -Path $runsDir -Force | Out-Null

function Get-CodeRev {
    try {
        return (& git -C $repoRoot rev-parse --short HEAD 2>$null).Trim()
    } catch {
        return 'unknown'
    }
}

function Get-CompletedRunIds {
    # A real result row begins with "| <run_id with T0..T9_ prefix> |" AND has
    # 30+ pipe characters (indicating the full data schema). The leaderboard
    # tables and any incidental references have far fewer columns and are
    # rejected.
    $existing = @{}
    $inLeaderboard = $false
    foreach ($line in Get-Content $ResultsPath -Encoding UTF8) {
        if ($line -match 'LEADERBOARD:START') { $inLeaderboard = $true; continue }
        if ($line -match 'LEADERBOARD:END')   { $inLeaderboard = $false; continue }
        if ($inLeaderboard) { continue }
        if ($line -match '^\| (T[0-9][A-Za-z0-9_\.\-]*) \|' -and ($line.Split('|').Count -ge 30)) {
            $existing[$Matches[1]] = $true
        }
    }
    return $existing
}

function Stop-AnyRunningSender {
    Get-Process -Name 'Tractus.HtmlToNdi' -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch {}
    }
    Start-Sleep -Milliseconds 200
}

function Start-PageServer {
    # Start a simple static-file http server for the deterministic test page.
    # We use Python (likely installed) or fall back to PowerShell HttpListener.
    $script:pageServerJob = $null
    try {
        $pyCheck = & python -c 'import sys' 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:pageServerJob = Start-Job -ScriptBlock {
                param($dir, $port)
                Set-Location $dir
                python -m http.server $port --bind 127.0.0.1 2>&1
            } -ArgumentList $pagesDir, $pageHttpPort
            Start-Sleep -Milliseconds 800
            return
        }
    } catch {}
    # Fallback: HttpListener PS job
    $script:pageServerJob = Start-Job -ScriptBlock {
        param($dir, $port)
        Add-Type -AssemblyName System.Net.HttpListener -ErrorAction SilentlyContinue
        $listener = [System.Net.HttpListener]::new()
        $listener.Prefixes.Add("http://127.0.0.1:$port/")
        $listener.Start()
        try {
            while ($listener.IsListening) {
                $ctx = $listener.GetContext()
                $rel = $ctx.Request.Url.LocalPath.TrimStart('/')
                if ([string]::IsNullOrEmpty($rel)) { $rel = 'motion.html' }
                $path = Join-Path $dir $rel
                if (Test-Path $path) {
                    $bytes = [System.IO.File]::ReadAllBytes($path)
                    $ctx.Response.ContentType = if ($path.EndsWith('.html')) { 'text/html' } else { 'application/octet-stream' }
                    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                } else {
                    $ctx.Response.StatusCode = 404
                }
                $ctx.Response.Close()
            }
        } finally { $listener.Stop() }
    } -ArgumentList $pagesDir, $pageHttpPort
    Start-Sleep -Milliseconds 500
}

function Stop-PageServer {
    if ($script:pageServerJob) {
        Stop-Job -Job $script:pageServerJob -ErrorAction SilentlyContinue
        Remove-Job -Job $script:pageServerJob -Force -ErrorAction SilentlyContinue
    }
}

function Build-SenderArgs {
    param([Parameter(Mandatory)] $cfg)
    $a = New-Object System.Collections.Generic.List[string]
    $a.Add('--no-launcher')
    $a.Add("--ndiname=$($cfg.ndi_name)")
    $a.Add('--port=9999')
    $a.Add("--w=$($cfg.width)")
    $a.Add("--h=$($cfg.height)")
    $a.Add("--fps=$($cfg.fps)")
    $a.Add("--url=$($cfg.url)")

    if ($cfg.PSObject.Properties.Match('flags').Count -gt 0 -and $cfg.flags) {
        foreach ($f in $cfg.flags) {
            $a.Add($f)
        }
    }
    return $a
}

function Run-OneConfig {
    param([Parameter(Mandatory)] $cfg, [string]$codeRev)

    $runDir = Join-Path $runsDir $cfg.run_id
    New-Item -ItemType Directory -Path $runDir -Force | Out-Null
    $senderLog = Join-Path $runDir 'sender.log'
    $receiverJson = Join-Path $runDir 'receiver.json'
    $senderJson = Join-Path $runDir 'sender.json'
    $sysmonCsv = Join-Path $runDir 'sysmon.csv'
    $rowJson = Join-Path $runDir 'row.json'

    # Clear stale artifacts from any previous attempt at this run_id so the
    # success gates can't be fooled by leftover files. New-Item -Force creates
    # the dir without wiping its contents, and a retry of an ERROR row would
    # otherwise mark itself PASS using last attempt's metrics.
    foreach ($stale in @($senderLog, "$senderLog.err", $receiverJson, $senderJson, $sysmonCsv,
                         (Join-Path $runDir 'receiver.stderr'),
                         (Join-Path $runDir 'receiver.stdout'),
                         (Join-Path $runDir 'parser.stdout'),
                         (Join-Path $runDir 'parser.stderr'))) {
        if (Test-Path $stale) { Remove-Item $stale -Force -ErrorAction SilentlyContinue }
    }

    Stop-AnyRunningSender

    $argList = Build-SenderArgs -cfg $cfg
    Write-Host "[$($cfg.run_id)] start: $senderExe $($argList -join ' ')" -ForegroundColor Cyan

    $procStart = Get-Date
    $senderProc = Start-Process -FilePath $senderExe -ArgumentList $argList -PassThru `
        -RedirectStandardOutput $senderLog -RedirectStandardError "$senderLog.err" -WindowStyle Hidden

    # Wait for "Application started" or timeout.
    $deadline = (Get-Date).AddSeconds(15)
    $appStarted = $false
    while ((Get-Date) -lt $deadline) {
        if (-not $senderProc.HasExited -and (Test-Path $senderLog)) {
            $tail = Get-Content $senderLog -Tail 200 -ErrorAction SilentlyContinue
            if ($tail -and ($tail | Where-Object { $_ -match 'Application started' })) {
                $appStarted = $true
                break
            }
        }
        if ($senderProc.HasExited) { break }
        Start-Sleep -Milliseconds 250
    }

    if (-not $appStarted) {
        Write-Host "[$($cfg.run_id)] ERROR: sender did not reach 'Application started'" -ForegroundColor Red
        try { if (-not $senderProc.HasExited) { $senderProc | Stop-Process -Force -ErrorAction SilentlyContinue } } catch {}
        # No sysmon was started yet; pass null so Read-SysmonPeaks short-circuits.
        Append-RunRow -cfg $cfg -codeRev $codeRev -senderJsonPath $null -receiverJsonPath $null -sysmonCsvPath $null -pass 'ERROR' -notes 'sender failed to reach Application started'
        return
    }

    # Brief settle so the buffer can prime.
    Start-Sleep -Milliseconds 1500

    $captureStart = Get-Date
    $duration = if ($cfg.PSObject.Properties.Match('duration_seconds').Count -gt 0) { [int]$cfg.duration_seconds } else { 60 }

    Write-Host "[$($cfg.run_id)] capturing $duration s on '$($cfg.ndi_name)'" -ForegroundColor DarkCyan

    # Start a parallel system perf-counter sampler. We use Get-Counter inside
    # a background job so we can correlate jitter spikes with CPU/IO/paging.
    # English counter names are translated by the underlying API on non-English
    # Windows, so this works across locales. ($sysmonCsv was defined at the
    # top of this function and any stale CSV was deleted there.)
    $sysmonJob = Start-Job -ScriptBlock {
        param($outPath, $samples)
        $counters = @(
            '\Processor Information(_Total)\% Processor Time',
            '\Memory\Available MBytes',
            '\Memory\Pages/sec',
            '\PhysicalDisk(_Total)\Avg. Disk Queue Length',
            '\Process(Tractus.HtmlToNdi)\% Processor Time',
            '\Process(Tractus.HtmlToNdi)\Page Faults/sec',
            '\Process(Tractus.HtmlToNdi)\Thread Count',
            '\Process(Tractus.HtmlToNdi)\Working Set'
        )
        $rows = New-Object System.Collections.Generic.List[object]
        for ($i = 0; $i -lt $samples; $i++) {
            try {
                $set = Get-Counter -Counter $counters -SampleInterval 1 -MaxSamples 1 -ErrorAction SilentlyContinue
                if ($set -and $set.CounterSamples) {
                    $row = [ordered]@{ ts = $set.Timestamp.ToString('o') }
                    foreach ($s in $set.CounterSamples) {
                        $row[$s.Path -replace '^.*\\([^\\]+)\\([^\\]+)$','$1.$2'] = $s.CookedValue
                    }
                    $rows.Add([pscustomobject]$row)
                }
            } catch { }
        }
        $rows | Export-Csv -Path $outPath -NoTypeInformation -Encoding UTF8
    } -ArgumentList $sysmonCsv, $duration

    # Path-valued args get quoted because Start-Process -ArgumentList joins
    # the array into a single command-line string with spaces; an unquoted
    # path that contains spaces would be split apart by CommandLineToArgvW
    # in the child process. ndi-source is alphanumeric/underscore-only via
    # Make-NdiName, so it doesn't strictly need quoting, but quoting is
    # harmless and consistent.
    $receiverArgs = @(
        "--ndi-source=`"$($cfg.ndi_name)`""
        "--duration-seconds=$duration"
        "--find-timeout-ms=8000"
        "--output=`"$receiverJson`""
    )
    $recvStderr = Join-Path $runDir 'receiver.stderr'
    $recvStdout = Join-Path $runDir 'receiver.stdout'
    $null = Start-Process -FilePath $receiverExe -ArgumentList $receiverArgs `
        -RedirectStandardOutput $recvStdout -RedirectStandardError $recvStderr `
        -Wait -NoNewWindow -PassThru

    # Stamp captureEnd IMMEDIATELY after the receiver returns, BEFORE draining
    # the sysmon job (which can block up to 10 s on Wait-Job). Otherwise the
    # sender-log parse window extends past the actual receive window and we
    # compare sender stats from a different interval than receiver stats.
    $captureEnd = Get-Date

    # Drain the sysmon job (it should be near-finished after the receiver returns).
    Wait-Job -Job $sysmonJob -Timeout 10 | Out-Null
    Receive-Job -Job $sysmonJob -ErrorAction SilentlyContinue | Out-Null
    Remove-Job -Job $sysmonJob -Force -ErrorAction SilentlyContinue

    $receiverOk = (Test-Path $receiverJson) -and ((Get-Item $receiverJson).Length -gt 4)

    # Tear down sender.
    try { if (-not $senderProc.HasExited) { $senderProc | Stop-Process -Force -ErrorAction SilentlyContinue } } catch {}
    Start-Sleep -Milliseconds 1500

    if (-not $receiverOk) {
        Write-Host "[$($cfg.run_id)] ERROR: receiver did not produce output" -ForegroundColor Red
        # Sysmon ran in parallel and may have written a usable CSV even though
        # the receiver itself failed. Pass it through so the row records the
        # system metrics that were captured rather than silently dropping them.
        Append-RunRow -cfg $cfg -codeRev $codeRev -senderJsonPath $null -receiverJsonPath $null -sysmonCsvPath $sysmonCsv -pass 'ERROR' -notes 'receiver missing output'
        return
    }

    # Parse sender log over the capture window.
    $startIso = $captureStart.ToUniversalTime().ToString('o')
    $endIso = $captureEnd.ToUniversalTime().ToString('o')
    $parserStdout = Join-Path $runDir 'parser.stdout'
    $parserStderr = Join-Path $runDir 'parser.stderr'
    $parserProc = Start-Process -FilePath 'dotnet' -ArgumentList @(
        "`"$parserDll`""
        "--log=`"$senderLog`""
        "--start-iso=$startIso"
        "--end-iso=$endIso"
        "--output=`"$senderJson`""
    ) -RedirectStandardOutput $parserStdout -RedirectStandardError $parserStderr `
        -Wait -NoNewWindow -PassThru

    # Validate that the sender-side parse actually succeeded. Without this, a
    # parser crash or empty-window result silently leaves senderJson missing/
    # blank and Append-RunRow produces a row with empty s.* columns that the
    # leaderboard still ranks as PASS based on receiver-only metrics. That
    # breaks the protocol's sender-vs-receiver comparison and can mis-rank
    # configs. Treat parser failure as ERROR for the run.
    $parserExit = if ($parserProc) { $parserProc.ExitCode } else { -1 }
    $senderJsonOk = $false
    $senderEmpty = $false
    if ($parserExit -eq 0 -and (Test-Path $senderJson) -and ((Get-Item $senderJson).Length -gt 4)) {
        try {
            $sjProbe = Get-Content $senderJson -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($sjProbe.statsLineCount -gt 0) { $senderJsonOk = $true } else { $senderEmpty = $true }
        } catch {}
    }

    $pass = 'PASS'
    $notes = ''
    if (-not $senderJsonOk) {
        $pass = 'ERROR'
        if ($senderEmpty) {
            $notes = 'sender parser found no pipeline stats lines in window'
        } else {
            $notes = "sender parser failed (exit=$parserExit)"
        }
    } else {
        try {
            $rj = Get-Content $receiverJson -Raw | ConvertFrom-Json
            if ($rj.errors -gt 0) { $pass = 'FAIL'; $notes = "recv.errors=$($rj.errors)" }
            elseif ($rj.veryLateCount -gt 0) { $pass = 'FAIL'; $notes = "recv.veryLateCount=$($rj.veryLateCount)" }
            elseif ($rj.videoFrames -lt ($duration * 0.5 * [double]$cfg.fps_value)) { $pass = 'FAIL'; $notes = "recv.videoFrames=$($rj.videoFrames) (expected ~$($duration * [double]$cfg.fps_value))" }
        } catch {
            $pass = 'ERROR'; $notes = "receiver json parse failed: $_"
        }
    }

    Append-RunRow -cfg $cfg -codeRev $codeRev -senderJsonPath $senderJson -receiverJsonPath $receiverJson -sysmonCsvPath $sysmonCsv -pass $pass -notes $notes
    Write-Host "[$($cfg.run_id)] $pass" -ForegroundColor (@{ PASS='Green'; FAIL='Yellow'; ERROR='Red' }[$pass])
}

function Read-SysmonPeaks {
    param([string]$csvPath)
    $result = [pscustomobject]@{
        cpuPct = $null; pagesSec = $null; diskQ = $null; procPgFlts = $null
    }
    # Guard before Test-Path: under $ErrorActionPreference='Stop', calling
    # Test-Path with $null/empty raises a parameter-binding error that would
    # abort the whole sweep instead of just leaving sysmon columns blank.
    if ([string]::IsNullOrWhiteSpace($csvPath)) { return $result }
    if (-not (Test-Path $csvPath)) { return $result }
    try {
        $rows = Import-Csv -Path $csvPath -Encoding UTF8
        if (-not $rows) { return $result }
        $cpuVals = @()
        $pageVals = @()
        $diskVals = @()
        $pflVals = @()
        foreach ($r in $rows) {
            foreach ($p in $r.PSObject.Properties) {
                $val = $null
                $tmp = 0.0
                if ([double]::TryParse([string]$p.Value, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$tmp)) { $val = $tmp }
                if ($null -eq $val) { continue }
                switch -Regex ($p.Name) {
                    'Processor Information.*% Processor Time' { $cpuVals += $val }
                    'Memory.Pages/sec'                         { $pageVals += $val }
                    'PhysicalDisk.*Avg\. Disk Queue Length'    { $diskVals += $val }
                    'Process.*Page Faults/sec'                 { $pflVals += $val }
                }
            }
        }
        if ($cpuVals.Count -gt 0)  { $result.cpuPct     = ($cpuVals | Measure-Object -Maximum).Maximum }
        if ($pageVals.Count -gt 0) { $result.pagesSec   = ($pageVals | Measure-Object -Maximum).Maximum }
        if ($diskVals.Count -gt 0) { $result.diskQ      = ($diskVals | Measure-Object -Maximum).Maximum }
        if ($pflVals.Count -gt 0)  { $result.procPgFlts = ($pflVals | Measure-Object -Maximum).Maximum }
    } catch {}
    return $result
}

function Append-RunRow {
    param(
        [Parameter(Mandatory)] $cfg,
        [Parameter(Mandatory)] [string]$codeRev,
        [string]$senderJsonPath,
        [string]$receiverJsonPath,
        [string]$sysmonCsvPath,
        [Parameter(Mandatory)] [string]$pass,
        [string]$notes
    )

    # Format numerics with invariant culture WITHOUT round-tripping through a
    # locale-formatted string. ConvertFrom-Json yields .NET numeric primitives
    # (double/long/decimal); converting them via [string] or interpolation uses
    # the current culture, so on comma-decimal locales (sv-SE, de-DE, ...) a
    # value like 22.67 stringifies as "22,67" and Parse(..., InvariantCulture)
    # then rejects the comma or - worse - treats it as a thousands separator,
    # silently corrupting recorded jitter/system metrics. Convert directly
    # from the numeric type instead.
    function Fmt2($v) {
        if ($null -eq $v) { return '' }
        $inv = [System.Globalization.CultureInfo]::InvariantCulture
        if ($v -is [double] -or $v -is [single] -or $v -is [decimal] -or $v -is [long] -or $v -is [int]) {
            return ([double]$v).ToString('0.00', $inv)
        }
        # Fallback for already-stringified inputs: parse with invariant culture.
        $tmp = 0.0
        if ([double]::TryParse([string]$v, [System.Globalization.NumberStyles]::Float, $inv, [ref]$tmp)) {
            return $tmp.ToString('0.00', $inv)
        }
        return ''
    }
    function Asis($v) { if ($null -eq $v) { return '' }; return $v.ToString() }

    $s = $null; $r = $null
    if ($senderJsonPath -and (Test-Path $senderJsonPath)) {
        try { $s = Get-Content $senderJsonPath -Raw | ConvertFrom-Json } catch { $s = $null }
    }
    if ($receiverJsonPath -and (Test-Path $receiverJsonPath)) {
        try { $r = Get-Content $receiverJsonPath -Raw | ConvertFrom-Json } catch { $r = $null }
    }

    $flagsStr = if ($cfg.flags) { ($cfg.flags -join ' ') } else { '' }

    $row = "| $($cfg.run_id) | $($cfg.timestamp_utc) | $codeRev | $($cfg.tier) | $($cfg.hypothesis -replace '\|','/') | $($cfg.page) | $($cfg.fps) | $($cfg.buffer_depth) | $($cfg.pacing_mode) | $(Asis $cfg.paced_inv) | $(Asis $cfg.bp) | $(Asis $cfg.adapt) | $(Asis $cfg.gpu_flags) | $(($cfg.extra_cef_args -replace '\|','/')) | $(($cfg.sys_tweaks -replace '\|','/')) "
    if ($s) {
        $row += "| $(Fmt2 $s.outputJitterRmsMs) | $(Fmt2 $s.outputJitterPkMs) | $(Fmt2 $s.captureCadenceFps) | $(Asis $s.repeated) | $(Asis $s.underruns) | $(Asis $s.droppedOverflow) "
    } else {
        $row += "|  |  |  |  |  |  "
    }
    if ($r) {
        $row += "| $(Fmt2 $r.effectiveFps) | $(Fmt2 $r.jitterRmsMs) | $(Fmt2 $r.jitterPeakMs) | $(Asis $r.lateCount) | $(Asis $r.veryLateCount) "
    } else {
        $row += "|  |  |  |  |  "
    }
    $sys = Read-SysmonPeaks -csvPath $sysmonCsvPath
    $row += "| $(Fmt2 $sys.cpuPct) | $(Fmt2 $sys.pagesSec) | $(Fmt2 $sys.diskQ) | $(Fmt2 $sys.procPgFlts) "
    $row += "| $pass | $($notes -replace '\|','/') |"

    # Insert under the matching tier section. We find the line index
    # of "## <tier>" and append after the next non-blank narrative line so
    # rows accumulate under each tier rather than at end-of-file.
    $tierAnchor = "## $($cfg.tier)"
    $existingLines = [System.Collections.Generic.List[string]](Get-Content $ResultsPath -Encoding UTF8)
    $tierIdx = -1
    for ($i = 0; $i -lt $existingLines.Count; $i++) {
        if ($existingLines[$i] -eq $tierAnchor) { $tierIdx = $i; break }
    }
    if ($tierIdx -lt 0) {
        # Tier header missing — append at EOF with a header.
        $existingLines.Add('')
        $existingLines.Add($tierAnchor)
        $existingLines.Add('')
        $existingLines.Add($row)
    } else {
        # Walk forward to the start of the next ## section, appending the row
        # just before that next section so all rows for this tier cluster
        # together. If no next section exists, append at end.
        $insertAt = $existingLines.Count
        for ($j = $tierIdx + 1; $j -lt $existingLines.Count; $j++) {
            if ($existingLines[$j] -match '^##\s') { $insertAt = $j; break }
        }
        # Trim trailing blank lines just before the next-section line so
        # consecutive rows stay adjacent.
        while ($insertAt -gt $tierIdx + 1 -and [string]::IsNullOrWhiteSpace($existingLines[$insertAt - 1])) {
            $insertAt--
        }
        $existingLines.Insert($insertAt, $row)
        # Ensure there's a blank line before the next section header.
        if ($insertAt + 1 -lt $existingLines.Count -and $existingLines[$insertAt + 1] -match '^##\s') {
            $existingLines.Insert($insertAt + 1, '')
        }
    }
    Set-Content -Path $ResultsPath -Value $existingLines -Encoding utf8
}

# Main entry.
$completed = Get-CompletedRunIds
$codeRev = Get-CodeRev
Write-Host "code rev: $codeRev" -ForegroundColor DarkGray
Write-Host "completed run_ids in results doc: $($completed.Count)" -ForegroundColor DarkGray

Start-PageServer
try {
    $configsRaw = Get-Content $ConfigsPath
    $runCount = 0
    foreach ($line in $configsRaw) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $cfg = $line | ConvertFrom-Json
        if ($Filter -and -not ($cfg.run_id -match $Filter)) { continue }
        if ($completed.ContainsKey($cfg.run_id)) { continue }
        if ($runCount -ge $MaxRuns) { break }

        # Stamp timestamp now (per actual run).
        $cfg | Add-Member -NotePropertyName timestamp_utc -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force
        Run-OneConfig -cfg $cfg -codeRev $codeRev
        $runCount++
    }
    Write-Host "ran $runCount config(s) this invocation" -ForegroundColor Green
} finally {
    Stop-PageServer
    Stop-AnyRunningSender
}
