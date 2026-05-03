<#
.SYNOPSIS
    Reads Docs/jitter-test-results.md and rewrites the Leaderboard section.
#>

param(
    [string]$ResultsPath
)
if ([string]::IsNullOrWhiteSpace($ResultsPath)) {
    $ResultsPath = (Join-Path (Join-Path $PSScriptRoot '..\..\Docs') 'jitter-test-results.md')
}

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ResultsPath)) { throw "Results file not found: $ResultsPath" }

function TryD([string]$s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return $null }
    $d = 0.0
    if ([double]::TryParse($s, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d }
    return $null
}

function TryI([string]$s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return $null }
    $i = 0
    if ([int]::TryParse($s, [ref]$i)) { return $i }
    return $null
}

$content = Get-Content $ResultsPath -Raw -Encoding UTF8

$startMarker = '<!-- LEADERBOARD:START -->'
$endMarker = '<!-- LEADERBOARD:END -->'
if ($content -notmatch [regex]::Escape($startMarker) -or $content -notmatch [regex]::Escape($endMarker)) {
    throw "Markers not found in $ResultsPath. Initialize the doc with the leaderboard placeholders first."
}

# Extract every row that begins "| T<n>_..." — the actual data rows.
$rowPattern = '^\| (T[0-9][A-Za-z0-9_\.\-]*) \|'
$lines = ($content -split "`r?`n")
$rows = New-Object System.Collections.Generic.List[object]
foreach ($line in $lines) {
    if ($line -notmatch $rowPattern) { continue }
    $cells = @(($line -split '\|') | ForEach-Object { $_.Trim() })
    # Cells layout (after splitting and trimming, including leading/trailing empties):
    # cells[0]=''     cells[1]=run_id  cells[2]=ts  cells[3]=code_rev cells[4]=tier
    # cells[5]=hyp    cells[6]=page    cells[7]=fps cells[8]=bd       cells[9]=mode
    # cells[10]=pi    cells[11]=bp     cells[12]=adapt  cells[13]=gpu cells[14]=cef
    # cells[15]=sys   cells[16]=s.outJitRms cells[17]=s.outJitPk cells[18]=s.capFps
    # cells[19]=s.repeated cells[20]=s.under cells[21]=s.dropOver
    # cells[22]=r.effFps cells[23]=r.jitRms cells[24]=r.jitPk cells[25]=r.late
    # cells[26]=r.veryLate cells[27]=pass cells[28]=notes cells[29]='' (trailing)
    # Schema after sysmon addition (33+ cells incl leading/trailing empties):
    # cells[1]=run_id, [2]=ts, [3]=code_rev, [4]=tier, [5]=hyp, [6]=page, [7]=fps,
    # [8]=bd, [9]=mode, [10]=pi, [11]=bp, [12]=adapt, [13]=gpu, [14]=cef, [15]=sys,
    # [16]=s.outJitRms, [17]=s.outJitPk, [18]=s.capFps, [19]=s.repeated, [20]=s.under, [21]=s.dropOver,
    # [22]=r.effFps, [23]=r.jitRms, [24]=r.jitPk, [25]=r.late, [26]=r.veryLate,
    # [27]=sys.cpuPct, [28]=sys.pagesSec, [29]=sys.diskQ, [30]=proc.pgFltsSec,
    # [31]=pass, [32]=notes
    if ($cells.Count -lt 33) { continue }

    $rows.Add([pscustomobject]@{
        run_id    = $cells[1]
        tier      = $cells[4]
        hypothesis= $cells[5]
        page      = $cells[6]
        fps       = $cells[7]
        bd        = $cells[8]
        mode      = $cells[9]
        s_jitRms  = (TryD $cells[16])
        s_jitPk   = (TryD $cells[17])
        r_effFps  = (TryD $cells[22])
        r_jitRms  = (TryD $cells[23])
        r_jitPk   = (TryD $cells[24])
        r_late    = (TryI $cells[25])
        r_veryLate= (TryI $cells[26])
        sys_cpu   = (TryD $cells[27])
        sys_pages = (TryD $cells[28])
        sys_diskQ = (TryD $cells[29])
        proc_pgFlt= (TryD $cells[30])
        pass      = $cells[31]
        notes     = $cells[32]
    })
}

$pass = @($rows | Where-Object { $_.pass -eq 'PASS' -and $null -ne $_.r_jitRms })

if ($pass.Count -eq 0) {
    $leaderboard = "(No PASS rows yet to rank.)"
} else {
    $top5JitRms = @($pass | Sort-Object -Property r_jitRms | Select-Object -First 5)
    $passWithPk = @($pass | Where-Object { $null -ne $_.r_jitPk })
    $top5JitPk  = @($passWithPk | Sort-Object -Property r_jitPk | Select-Object -First 5)
    $passWithLate = @($pass | Where-Object { $null -ne $_.r_late })
    $top5LateLow = @($passWithLate | Sort-Object -Property r_late, r_jitPk | Select-Object -First 5)

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('### Lowest receiver jitter RMS (PASS only)')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('| run_id | page | fps | recv jitterRMS ms | recv jitterPk ms | recv late | hypothesis |')
    [void]$sb.AppendLine('|---|---|---|---|---|---|---|')
    foreach ($r in $top5JitRms) {
        [void]$sb.AppendLine("| $($r.run_id) | $($r.page) | $($r.fps) | $($r.r_jitRms) | $($r.r_jitPk) | $($r.r_late) | $($r.hypothesis) |")
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('### Lowest receiver jitter peak (PASS only)')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('| run_id | page | fps | recv jitterPk ms | recv jitterRMS ms | recv late | hypothesis |')
    [void]$sb.AppendLine('|---|---|---|---|---|---|---|')
    foreach ($r in $top5JitPk) {
        [void]$sb.AppendLine("| $($r.run_id) | $($r.page) | $($r.fps) | $($r.r_jitPk) | $($r.r_jitRms) | $($r.r_late) | $($r.hypothesis) |")
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('### Lowest late-frame count, peak as tiebreaker (PASS only)')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('| run_id | page | fps | recv late | recv jitterPk ms | recv jitterRMS ms | hypothesis |')
    [void]$sb.AppendLine('|---|---|---|---|---|---|---|')
    foreach ($r in $top5LateLow) {
        [void]$sb.AppendLine("| $($r.run_id) | $($r.page) | $($r.fps) | $($r.r_late) | $($r.r_jitPk) | $($r.r_jitRms) | $($r.hypothesis) |")
    }
    $leaderboard = $sb.ToString()
}

$pattern = "(?s)$([regex]::Escape($startMarker)).*?$([regex]::Escape($endMarker))"
$replacement = "$startMarker`r`n`r`n$leaderboard`r`n$endMarker"
$updated = [regex]::Replace($content, $pattern, $replacement)
Set-Content -Path $ResultsPath -Value $updated -Encoding UTF8
$total = $rows.Count
$passCount = $pass.Count
Write-Host "leaderboard updated ($total total rows; $passCount PASS rows considered)" -ForegroundColor Green
