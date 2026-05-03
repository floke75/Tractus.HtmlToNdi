<#
.SYNOPSIS
    Emits the per-tier sweep configs to configs.jsonl.

.DESCRIPTION
    Each line of the output is a single JSON object describing one run:
      {
        "run_id":     "T1_buffer_3_29.97_motion",
        "tier":       "T1",
        "hypothesis": "buffer-depth=3 sensitivity at 29.97p on motion fixture",
        "page":       "motion",
        "url":        "http://127.0.0.1:18080/motion.html?chaos=1",
        "ndi_name":   "T1_buffer_3_29_97_motion",
        "fps":        "30000/1001",
        "fps_value":  29.97,
        "width":      1920,
        "height":     1080,
        "buffer_depth": 3,
        "pacing_mode":  "Latency",
        "paced_inv":    "on",
        "bp":           "off",
        "adapt":        "on",
        "gpu_flags":    "preset",
        "extra_cef_args": "",
        "sys_tweaks":     "",
        "duration_seconds": 60,
        "flags": [ "--enable-output-buffer", "--buffer-depth=3", ... ]
      }

    Layer-3 pipeline-code flags (cadence-adapt-gain, buffer-overflow-policy,
    latency-expansion-strategy, paced-warmup-frames, pacer-thread-priority)
    are accepted by the CLI but currently no-ops; they are NOT included in
    sweep configs until they're wired through (Phase A2).
#>

[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'configs.jsonl')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$pageHttpPort = 18080
$flowicsUrl = 'https://viz.flowics.com/public/a91c5063c2e6387efe69049f8e50141d/6902022a0adf4ed6120f7f3c/live'
$motionUrl = "http://127.0.0.1:$pageHttpPort/motion.html?chaos=1"

$fpsMatrix = @(
    @{ name='29.97'; cli='30000/1001'; value=29.97; windowless=30 },
    @{ name='60';    cli='60';         value=60;    windowless=60 },
    @{ name='30';    cli='30';         value=30;    windowless=30 },
    @{ name='50';    cli='50';         value=50;    windowless=50 },
    @{ name='25';    cli='25';         value=25;    windowless=25 }
)

$pageMatrix = @(
    @{ name='motion';  url=$motionUrl },
    @{ name='flowics'; url=$flowicsUrl }
)

# Golden combo as the baseline for T0/T1/T4 marginal tests.
function Get-GoldenFlags {
    param($fpsRow)
    $flags = @(
        '--pacing-mode=Latency',
        '--enable-output-buffer',
        '--buffer-depth=6',
        '--enable-paced-invalidation',
        '--enable-pump-cadence-adaptation',
        "--windowless-frame-rate=$($fpsRow.windowless)",
        '--ndi-send-async',
        '--enable-gpu-rasterization',
        '--enable-zero-copy',
        '--enable-oop-rasterization',
        '--disable-background-throttling',
        '--disable-gpu-vsync',
        '--disable-audio'
    )
    return ,$flags
}

function Make-NdiName($runId) { return ($runId -replace '[^A-Za-z0-9_]','_') }

function Make-Config {
    param(
        [string]$tier,
        [string]$shortId,
        $fpsRow,
        $pageRow,
        [string]$hypothesis,
        [string[]]$flags,
        [string]$gpuFlags = 'preset',
        [string]$extraCefArgs = '',
        [string]$sysTweaks = '',
        [int]$bufferDepth = 6,
        [string]$pacingMode = 'Latency',
        [string]$pacedInv = 'on',
        [string]$bp = 'off',
        [string]$adapt = 'on',
        [int]$durationSeconds = 60
    )
    $runId = "${tier}_${shortId}_$($fpsRow.name)_$($pageRow.name)"
    $obj = [ordered]@{
        run_id       = $runId
        tier         = $tier
        hypothesis   = $hypothesis
        page         = $pageRow.name
        url          = $pageRow.url
        ndi_name     = Make-NdiName $runId
        fps          = $fpsRow.cli
        fps_value    = $fpsRow.value
        width        = 1920
        height       = 1080
        buffer_depth = $bufferDepth
        pacing_mode  = $pacingMode
        paced_inv    = $pacedInv
        bp           = $bp
        adapt        = $adapt
        gpu_flags    = $gpuFlags
        extra_cef_args = $extraCefArgs
        sys_tweaks   = $sysTweaks
        duration_seconds = $durationSeconds
        flags        = $flags
    }
    return ($obj | ConvertTo-Json -Compress -Depth 5)
}

$lines = New-Object System.Collections.Generic.List[string]

# ---- T0: reproducibility (5 repeats of golden at 29.97 on motion + flowics) ----
foreach ($pageRow in $pageMatrix) {
    foreach ($i in 1..5) {
        $fpsRow = $fpsMatrix[0]
        $flags = Get-GoldenFlags $fpsRow
        $lines.Add( (Make-Config -tier 'T0' -shortId "rep$i" -fpsRow $fpsRow -pageRow $pageRow -hypothesis "Reproducibility repeat #$i - establishes variance floor" -flags $flags) )
    }
}

# ---- T1: one-at-a-time sensitivity, applied at all five FPS, both pages ----
# Each variation is a (label, mutator) pair on the golden flag list.
$variations = @(
    @{ id='buffer_1';     label='buffer-depth=1';     mutator={ param($f) ($f -replace '--buffer-depth=\d+','--buffer-depth=1') } ; bd=1 },
    @{ id='buffer_3';     label='buffer-depth=3';     mutator={ param($f) ($f -replace '--buffer-depth=\d+','--buffer-depth=3') } ; bd=3 },
    @{ id='buffer_10';    label='buffer-depth=10';    mutator={ param($f) ($f -replace '--buffer-depth=\d+','--buffer-depth=10') } ; bd=10 },
    @{ id='buffer_30';    label='buffer-depth=30';    mutator={ param($f) ($f -replace '--buffer-depth=\d+','--buffer-depth=30') } ; bd=30 },
    @{ id='no_pacedinv';  label='paced invalidation off'; mutator={ param($f) (($f | Where-Object { $_ -ne '--enable-paced-invalidation' }) + '--disable-paced-invalidation') } ; pi='off' },
    @{ id='no_adapt';     label='cadence adaptation off';  mutator={ param($f) ($f | Where-Object { $_ -ne '--enable-pump-cadence-adaptation' }) } ; ad='off' },
    @{ id='no_async';     label='sync NDI send';            mutator={ param($f) ($f | Where-Object { $_ -ne '--ndi-send-async' }) } },
    @{ id='no_gpu';       label='no GPU acceleration flags'; mutator={ param($f) ($f | Where-Object { $_ -notin @('--enable-gpu-rasterization','--enable-zero-copy','--enable-oop-rasterization') }) } ; gp='none' },
    @{ id='vsync_on';     label='gpu vsync enabled';         mutator={ param($f) ($f | Where-Object { $_ -ne '--disable-gpu-vsync' }) } },
    @{ id='audio_on';     label='audio rendering enabled';   mutator={ param($f) ($f | Where-Object { $_ -ne '--disable-audio' }) } },
    @{ id='allow_lat';    label='allow latency expansion';   mutator={ param($f) (@($f) + '--allow-latency-expansion') } },
    @{ id='bg_throttle';  label='background throttling default (not disabled)'; mutator={ param($f) ($f | Where-Object { $_ -ne '--disable-background-throttling' }) } },
    @{ id='no_align';     label='capture alignment disabled'; mutator={ param($f) (@($f) + '--disable-capture-alignment') } },
    @{ id='compositor';   label='compositor capture (experimental)'; mutator={ param($f) (@($f) + '--enable-compositor-capture') -replace '--enable-paced-invalidation','--disable-paced-invalidation' } ; pi='off' }
)

foreach ($fpsRow in $fpsMatrix) {
    foreach ($pageRow in $pageMatrix) {
        foreach ($var in $variations) {
            $base = Get-GoldenFlags $fpsRow
            $newFlags = & $var.mutator $base
            $bd = if ($var.ContainsKey('bd')) { $var.bd } else { 6 }
            $pi = if ($var.ContainsKey('pi')) { $var.pi } else { 'on' }
            $ad = if ($var.ContainsKey('ad')) { $var.ad } else { 'on' }
            $gp = if ($var.ContainsKey('gp')) { $var.gp } else { 'preset' }
            $lines.Add( (Make-Config -tier 'T1' -shortId $var.id -fpsRow $fpsRow -pageRow $pageRow -hypothesis "Sensitivity: $($var.label)" -flags $newFlags -bufferDepth $bd -pacedInv $pi -adapt $ad -gpuFlags $gp) )
        }
    }
}

# ---- T2: documented recipes at all 5 FPS, both pages ----
$recipes = @(
    @{ id='lowlat';     label='Low latency / interactive (no buffer)'; build = { param($fpsRow) ,@(
        '--pacing-mode=Latency','--disable-paced-invalidation','--preset-high-performance','--disable-audio',
        "--windowless-frame-rate=$($fpsRow.windowless)") }; bd=0; pi='off'; ad='off'; gp='preset' },
    @{ id='broadcast';  label='Broadcast smooth (Smoothness mode)'; build = { param($fpsRow) ,@(
        '--pacing-mode=Smoothness','--preset-high-performance','--ndi-send-async','--disable-audio',
        "--windowless-frame-rate=$($fpsRow.windowless)") }; bd=300; pi='off'; ad='off'; gp='preset' },
    @{ id='deepbuf';    label='Deep buffer + latency expansion'; build = { param($fpsRow) ,@(
        '--pacing-mode=Latency','--enable-output-buffer','--buffer-depth=120','--enable-paced-invalidation',
        '--enable-pump-cadence-adaptation','--allow-latency-expansion','--ndi-send-async',
        '--enable-gpu-rasterization','--enable-zero-copy','--enable-oop-rasterization',
        '--disable-background-throttling','--disable-gpu-vsync','--disable-audio',
        "--windowless-frame-rate=$($fpsRow.windowless)") }; bd=120; pi='on'; ad='on'; gp='preset' },
    @{ id='locked';     label='Locked cadence shallow (golden)'; build = { param($fpsRow) Get-GoldenFlags $fpsRow }; bd=6; pi='on'; ad='on'; gp='preset' },
    @{ id='compositor'; label='Compositor capture experimental'; build = { param($fpsRow) ,@(
        '--pacing-mode=Latency','--enable-output-buffer','--buffer-depth=1','--enable-compositor-capture',
        '--ndi-send-async','--disable-cadence-telemetry','--disable-audio',
        "--windowless-frame-rate=$($fpsRow.windowless)") }; bd=1; pi='off'; ad='off'; gp='none' }
)

foreach ($fpsRow in $fpsMatrix) {
    foreach ($pageRow in $pageMatrix) {
        foreach ($r in $recipes) {
            $flags = & $r.build $fpsRow
            $lines.Add( (Make-Config -tier 'T2' -shortId $r.id -fpsRow $fpsRow -pageRow $pageRow -hypothesis "Recipe: $($r.label)" -flags $flags -bufferDepth $r.bd -pacedInv $r.pi -adapt $r.ad -gpuFlags $r.gp) )
        }
    }
}

# ---- T3: pairwise interaction grids (top-impact pairs) at 29.97 + 60 ----
$gridDepths = @(1, 3, 6, 10, 30)
$gridFps = $fpsMatrix | Where-Object { $_.name -in @('29.97','60') }

# Grid A: buffer × paced-invalidation
foreach ($fpsRow in $gridFps) {
    foreach ($pageRow in $pageMatrix) {
        foreach ($d in $gridDepths) {
            foreach ($pi in @('on','off')) {
                $base = Get-GoldenFlags $fpsRow
                $newFlags = $base -replace '--buffer-depth=\d+',"--buffer-depth=$d"
                if ($pi -eq 'off') {
                    $newFlags = ($newFlags | Where-Object { $_ -ne '--enable-paced-invalidation' }) + '--disable-paced-invalidation'
                }
                $lines.Add( (Make-Config -tier 'T3' -shortId "bufXpi_${d}_$pi" -fpsRow $fpsRow -pageRow $pageRow -hypothesis "Grid A: buffer=$d, paced-inv=$pi" -flags $newFlags -bufferDepth $d -pacedInv $pi) )
            }
        }
    }
}

# Grid B: buffer × cadence-adaptation
foreach ($fpsRow in $gridFps) {
    foreach ($pageRow in $pageMatrix) {
        foreach ($d in $gridDepths) {
            foreach ($adapt in @('on','off')) {
                $base = Get-GoldenFlags $fpsRow
                $newFlags = $base -replace '--buffer-depth=\d+',"--buffer-depth=$d"
                if ($adapt -eq 'off') {
                    $newFlags = $newFlags | Where-Object { $_ -ne '--enable-pump-cadence-adaptation' }
                }
                $lines.Add( (Make-Config -tier 'T3' -shortId "bufXad_${d}_$adapt" -fpsRow $fpsRow -pageRow $pageRow -hypothesis "Grid B: buffer=$d, cadence-adapt=$adapt" -flags $newFlags -bufferDepth $d -adapt $adapt) )
            }
        }
    }
}

# ---- T4: unexposed Chromium flags via --cef-extra-args, golden + one extra ----
$cefFlags = @(
    'num-raster-threads=2',
    'num-raster-threads=4',
    'num-raster-threads=8',
    'use-angle=d3d11',
    'use-angle=d3d11on12',
    'use-angle=gl',
    'in-process-gpu',
    'no-zygote',
    'disable-features=BackForwardCache,CalculateNativeWinOcclusion',
    'enable-low-latency-canvas2d-image-chromium',
    'enable-features=AcceleratedSmallCanvases,RawDraw',
    'gpu-rasterization-msaa-sample-count=0',
    'enable-zero-copy-tab-capture',
    'disable-software-rasterizer'
)

foreach ($fpsRow in ($fpsMatrix | Where-Object { $_.name -in @('29.97','60') })) {
    foreach ($pageRow in $pageMatrix) {
        foreach ($cf in $cefFlags) {
            $flags = (Get-GoldenFlags $fpsRow) + "--cef-extra-args=$cf"
            $shortId = ($cf -replace '[^A-Za-z0-9]','_').Substring(0, [Math]::Min(24, ($cf -replace '[^A-Za-z0-9]','_').Length))
            $lines.Add( (Make-Config -tier 'T4' -shortId $shortId -fpsRow $fpsRow -pageRow $pageRow -hypothesis "Cef: --$cf atop golden" -flags $flags -extraCefArgs $cf) )
        }
    }
}

# ---- T5: system-level tweaks (the wired Layer-3 flags), golden + one extra ----
$sysTweaks = @(
    @{ id='mmtimer1';     desc='--mm-timer-resolution=1';            flag='--mm-timer-resolution=1' },
    @{ id='gcsust';        desc='--gc-latency-mode=sustained-low-latency'; flag='--gc-latency-mode=sustained-low-latency' },
    @{ id='gclow';         desc='--gc-latency-mode=low-latency';      flag='--gc-latency-mode=low-latency' },
    @{ id='prio_high';     desc='--process-priority=high';            flag='--process-priority=high' },
    @{ id='prio_above';    desc='--process-priority=above-normal';    flag='--process-priority=above-normal' },
    @{ id='prio_realtime'; desc='--process-priority=realtime';        flag='--process-priority=realtime' },
    @{ id='aff_first4';    desc='--cpu-affinity=0xF';                  flag='--cpu-affinity=0xF' },
    @{ id='combo_pristine'; desc='mm-timer + gcsust + prio-high';      flag='--mm-timer-resolution=1 --gc-latency-mode=sustained-low-latency --process-priority=high' }
)

foreach ($fpsRow in ($fpsMatrix | Where-Object { $_.name -in @('29.97','60') })) {
    foreach ($pageRow in $pageMatrix) {
        foreach ($t in $sysTweaks) {
            $flags = (Get-GoldenFlags $fpsRow) + ($t.flag -split '\s+')
            $lines.Add( (Make-Config -tier 'T5' -shortId $t.id -fpsRow $fpsRow -pageRow $pageRow -hypothesis "Sys: $($t.desc) atop golden" -flags $flags -sysTweaks $t.desc) )
        }
    }
}

Set-Content -Path $OutputPath -Value $lines -Encoding utf8
Write-Host "wrote $($lines.Count) configs to $OutputPath" -ForegroundColor Green
