# NDI Jitter Test Protocol

> **For the executor agent (Claude Sonnet, Codex CLI, or any other coding agent):** see [the boxed instructions](#executor-instructions) before running anything. You are forbidden from editing C# code, adding configs not present in `configs.jsonl`, or making judgment calls about which configs to skip. Your job is mechanical: run the sweep, report failures, run it again until done, then regenerate the leaderboard.

## 1. Goals

- Map jitter, drop, late-frame, and drift behavior across the full settings surface at multiple FPS targets.
- Capture **both** sender pipeline telemetry and receiver-side ground truth per run, joined into a single record.
- Produce a long-lived markdown results table (sortable for any goal).
- Be extensible: new knobs are added by editing `generate-configs.ps1` + regenerating `configs.jsonl`; never by hand-editing C# during execution.

A protocol run "succeeds" when:
1. T0 reproducibility runs (same config × 5 per page) show receiver `jitterRmsMs` variance below 0.5 ms — confirms the environment is stable enough that we can attribute differences to knobs.
2. Sender JSON and receiver JSON agree on `effectiveFps` within 0.001 fps and `jitterRmsMs` within 0.5 ms.
3. The runner can be killed mid-sweep and restarted — picks up at the next un-run config without redoing completed ones.
4. After all 368 configs run, the leaderboard surfaces a clear winner per goal and the per-tier tables show coherent trends.

---

## 2. Executor instructions

**You are running [Tools/JitterSweep/run-sweep.ps1](../Tools/JitterSweep/run-sweep.ps1). Read these rules carefully before starting.**

### Allowed (and recommended pattern)

The recommended invocation is **always chunked + backgrounded**:

```bash
# One chunk = ~30-40 minutes. Repeat until configs.jsonl is exhausted.
pwsh ./Tools/JitterSweep/run-sweep.ps1 -MaxRuns 30
```

Launch this command in the background using whatever your harness provides (`run_in_background: true` in Claude Code's Bash tool; equivalent backgrounding in Codex CLI or other agents) so your context isn't tied up waiting. After the chunk completes (or your context loop fires), re-read `Docs/jitter-test-results.md` to confirm progress, then launch the next chunk.

Other allowed operations:
- Re-run after a crash or after the user pauses you. The runner is resumable; already-completed `run_id`s in `Docs/jitter-test-results.md` are skipped automatically.
- Use the `-Filter` parameter to restrict to a tier or pattern (e.g. `-Filter '^T0_'`) when targeted re-runs are needed.
- After all 368 configs are completed, run `pwsh ./Tools/JitterSweep/generate-leaderboard.ps1` to refresh the leaderboard section.
- Report progress and any rows with `pass=ERROR` or `pass=FAIL` to the user when they ask.

### Forbidden
- ❌ Do **not** edit any `.cs` file. The runtime flags in `configs.jsonl` are the only knobs available.
- ❌ Do **not** edit `configs.jsonl`. If the user wants new combinations tested, route them back to a new Opus session that updates `generate-configs.ps1` and regenerates the file.
- ❌ Do **not** decide which buffer depths or pacing modes are interesting to try beyond what's already in `configs.jsonl`.
- ❌ Do **not** modify any markdown file in `Docs/` by hand. The runner appends rows; the leaderboard script updates the leaderboard. Both write the changes you need.
- ❌ Do **not** skip a config because "it looks like it'll fail" — the runner's pass/fail logic decides. Crashes turn into `pass=ERROR` rows automatically.
- ❌ Do **not** commit results, modified docs, or any other change without asking the user.

### Failure handling
- Individual config failures (`pass=ERROR` or `FAIL`) are normal and expected. They produce a row, and the runner continues.
- If the runner itself crashes, just relaunch it. It resumes.
- If the receiver consistently fails to find the source ("No NDI source matching"), check that the sender's NDI name in the spawned process matches `cfg.ndi_name`. Report to the user; do not modify the runner.
- If a tier's rows are systematically failing in a way that looks like a runner bug (not config-specific), stop and report to the user.

### Compaction-safe execution (mandatory)

Your context window can compact between or during sessions. The data layer is durable - each config writes 4 files to `Tools/JitterSweep/runs/<run_id>/` and appends one row to `Docs/jitter-test-results.md` *before* moving on. But your *operational view* of progress can be lost if you don't follow these rules:

1. **Always launch the sweep with `run_in_background: true`.** Never block your own context on a multi-hour foreground Bash. The runner is a separate OS process; it does not need you to stay alive.

2. **Always cap each invocation with `-MaxRuns 30`** (30 configs ≈ 35-40 minutes including overhead). After each chunk completes, you regain control of your context cleanly with all results already on disk. Without `-MaxRuns`, a single launch could run for 8+ hours and your context will compact mid-sweep, leaving you uncertain about the runner's actual state.

3. **Track progress by reading `Docs/jitter-test-results.md`, not by remembering.** After each chunk, count the rows per tier with a one-line bash to know what's left:
   ```bash
   grep -cE '^\| T[0-9]_' Docs/jitter-test-results.md
   ```
   That number, compared against `wc -l Tools/JitterSweep/configs.jsonl` (368), tells you how far you are. The runner's resume logic uses the same source of truth, so this can never disagree with reality.

4. **After any session boundary, re-read this protocol document before doing anything.** Don't trust your memory of the rules - they live here, not in your context.

5. **If a run row says `pass=ERROR`, do not retry it manually.** The resume logic will leave it in the doc, but you can re-run it by deleting that one row from the results doc and relaunching the runner. Confirm with the user before doing this; some `ERROR` rows may be expected (e.g. a config that exercises a known-bad combination).

6. **End every session with a status report to the user**: total rows, rows per tier, count of `pass=PASS`, `pass=FAIL`, `pass=ERROR`, and the last-completed `run_id`. This survives compaction because it's written to the chat. Re-derive these numbers from the file each time, never from memory.

---

## 3. What we measure

### Sender side (`Tools/SenderTelemetryParser`)
The sender's existing Serilog `pipeline stats` lines (emitted every `--telemetry-interval` seconds, default 10 s) are parsed in-window. Source: [`Video/NdiVideoPipeline.cs`](../Video/NdiVideoPipeline.cs).

| Field | Meaning |
|---|---|
| `captured`, `sent`, `repeated` | cumulative since process start |
| `buffered` | latest buffer fill |
| `droppedOverflow`, `droppedStale`, `underruns`, `resyncDrops` | counters |
| `outputJitterRmsMs`, `outputJitterPkMs` | RMS / peak deviation of send-deadline timing in current window |
| `captureJitterRmsMs`, `captureJitterPkMs` | RMS / peak of capture-arrival timing (Chromium paint cadence) |
| `captureCadenceFps` | observed Chromium paint rate |
| `pacedLatencyMs` | end-to-end paced latency |
| `latencyError` | pacer drift in frames |
| `pacedInvalidation`, `cadenceAdaptation`, `pacedHighResTimer` | mode flags |

### Receiver side (`Tools/NdiTelemetryReceiver`)
A standalone NDI consumer that times every video frame's arrival on its own `Stopwatch` clock. Source: [`Tools/NdiTelemetryReceiver/Program.cs`](../Tools/NdiTelemetryReceiver/Program.cs).

| Field | Meaning |
|---|---|
| `videoFrames` | total received in the capture window |
| `effectiveFps` | videoFrames / windowSeconds |
| `meanIntervalMs` | average inter-arrival time |
| `minGapMs`, `maxGapMs` | extremes |
| `jitterRmsMs`, `jitterPeakMs` | RMS / peak deviation from `targetIntervalMs` |
| `lateCount` | intervals > 1.5 × target |
| `veryLateCount` | intervals > 2.5 × target (likely missed frame) |
| `errors` | NDI-reported error frames |

The two sides should agree within a tight margin - that's a verification that the wire stage isn't introducing extra jitter.

### System side (`Get-Counter` polled at 1 Hz, parallel to receiver)
While the receiver captures, the runner spawns a PowerShell background job that polls Windows performance counters every second and saves the full per-second timeline as CSV at `Tools/JitterSweep/runs/<run_id>/sysmon.csv`. The runner extracts peak values into the results-doc row.

| Counter | Surfaced as | Why it matters |
|---|---|---|
| `\Processor Information(_Total)\% Processor Time` | `sys.cpuPct` | global CPU pressure — competing work that could preempt the pacer thread |
| `\Memory\Pages/sec` | `sys.pagesSec` | hard page faults, which suspend threads while disk reads complete |
| `\PhysicalDisk(_Total)\Avg. Disk Queue Length` | `sys.diskQ` | disk congestion that can stall any thread doing IO |
| `\Process(Tractus.HtmlToNdi)\Page Faults/sec` | `proc.pgFltsSec` | the sender's own paging behavior — a hot spot for capture stalls |

The CSV also records `Process` CPU%, working set, and thread count for deeper forensic dives. Counter names are English even on non-English Windows because `Get-Counter` translates internally.

---

## 4. Pass / fail criteria (per FPS)

A row is marked `PASS` when **all** are true (computed by [run-sweep.ps1](../Tools/JitterSweep/run-sweep.ps1) at append time):

| Criterion | 29.97p | 60p | 50p | 30p | 25p |
|---|---|---|---|---|---|
| `recv.errors == 0` | ✓ | ✓ | ✓ | ✓ | ✓ |
| `recv.veryLateCount == 0` (no missed frames) | ✓ | ✓ | ✓ | ✓ | ✓ |
| `recv.videoFrames >= 0.5 × duration × fps` (sanity check) | ✓ | ✓ | ✓ | ✓ | ✓ |

Soft thresholds (not hard fails — captured in notes for ranking):
- `recv.jitterPeakMs < 1.5 × frame_interval` is the broadcast-acceptable target.
- `recv.lateCount < 1 per minute` is the smoothness target for typical receivers.
- `recv.effectiveFps within ±0.05% of target` confirms cadence lock.

---

## 5. Environment baseline

Filled in once per sweep campaign; captured in the results doc.

- **Host**: machine specs, OS build (`winver`)
- **NDI runtime**: emitted by sender at startup (e.g. `NDI SDK WIN64 16:08:06 Feb 17 2026 6.3.1.0`)
- **Monitor refresh**: relevant for vsync interactions
- **Network**: loopback by default (sender + receiver on same host); LAN can be tested by running the receiver on another machine and adjusting the runner accordingly
- **Code rev**: `git rev-parse --short HEAD` automatically captured per row

---

## 6. Knob registry

### Layer 1 — surfaced via launcher CLI (already wired)
The 28-setting surface from the audit. All accepted by `LaunchParameters.TryFromArgs`. See [Launcher/LaunchParameters.cs](../Launcher/LaunchParameters.cs).

### Layer 2 — Chromium flags via `--cef-extra-args` (wired)
`--cef-extra-args="key1=val1;key2=val2;flag3"` splits on `;`, parses `key=value` or bare flag, and appends to `CefSettings.CefCommandLineArgs`. T4 covers an initial 14 flags; add more by editing `generate-configs.ps1`.

### Layer 3 — system-level (wired)
- `--mm-timer-resolution=N` — Windows multimedia timer resolution via `winmm.dll` `timeBeginPeriod`. Auto-reverted at exit.
- `--gc-latency-mode=interactive|low-latency|sustained-low-latency|batch` — `GCSettings.LatencyMode`.
- `--process-priority=high|above-normal|normal|below-normal|idle|realtime` — `Process.PriorityClass`.
- `--cpu-affinity=auto|0xFF|...` — `Process.ProcessorAffinity` bitmask.

### Layer 3 — pipeline-code (CLI accepted, NOT yet plumbed)
These flags parse cleanly and the sender logs a warning, but the underlying code paths still use their hardcoded defaults. They're in `LaunchParameters` so a future Opus session can wire them through without changing the CLI surface.

| Flag | Purpose | Plumbing target |
|---|---|---|
| `--pacer-thread-priority=...` | thread priority on the paced sender thread | `NdiVideoPipeline` thread creation |
| `--cadence-adapt-gain=<float>` | gain on cadence-adaptation correction | `Chromium/FramePump.cs:42` constant |
| `--buffer-overflow-policy=...` | drop-oldest/drop-newest/adaptive | `Video/FrameRingBuffer.cs` `Enqueue` |
| `--latency-expansion-strategy=...` | replaces boolean with 3-way enum | `NdiVideoPipeline` underrun recovery |
| `--paced-warmup-frames=<int>` | override warmup threshold | `NdiVideoPipeline` warmup logic |

These are **not** included in any `configs.jsonl` row today. T5 covers only the wired Layer-3 flags. A "Phase A2" follow-up will wire them through and add a T5b tier.

---

## 7. Sweep tiers

Generated by [Tools/JitterSweep/generate-configs.ps1](../Tools/JitterSweep/generate-configs.ps1). Run-id format: `T<n>_<shortId>_<fps>_<page>`.

| Tier | Description | Count | Per-config seconds |
|---|---|---|---|
| T0 | Reproducibility — current golden combo, 5 repeats | 10 | 60 |
| T1 | One-at-a-time sensitivity around current golden, per FPS (29.97, 60, 30, 50, 25) | 140 | 60 |
| T2 | 5 documented recipes at all 5 FPS, both pages | 50 | 60 |
| T3 | Pairwise interaction grids: buffer × paced-inv, buffer × cadence-adapt | 80 | 60 |
| T4 | Layer-2 Chromium flags (14) atop golden, 29.97 + 60, both pages | 56 | 60 |
| T5 | Layer-3 wired system tweaks (8) atop golden, 29.97 + 60, both pages | 32 | 60 |
| **Total** |  | **368** | ~7.5 h pure capture + ~1 h overhead |

---

## 8. Run lifecycle

Per config row in `configs.jsonl`, the runner does:

1. **Stop** any previously-running `Tractus.HtmlToNdi.exe` (kills lingering processes).
2. **Spawn** the sender with the config's CLI flags, redirecting stdout/stderr to `Tools/JitterSweep/runs/<run_id>/sender.log`.
3. **Poll** the log every 250 ms, up to 15 s, for the line `Application started`. If not found in time → mark row `pass=ERROR`.
4. **Settle** 1.5 s — gives the buffer time to prime.
5. **Capture** for `duration_seconds` (default 60). Runs `NdiTelemetryReceiver` with `--ndi-source=<run_id-derived>` and writes `runs/<run_id>/receiver.json`.
6. **Tear down** the sender and wait 1.5 s.
7. **Parse** the sender log over the receiver's capture window via `SenderTelemetryParser`, write `runs/<run_id>/sender.json`.
8. **Append** a row to `Docs/jitter-test-results.md` under the matching tier section. Include both sender and receiver fields plus `pass` and `notes`.

The deterministic motion fixture is served from a tiny HTTP server (`python -m http.server` if available, else PowerShell `HttpListener`) on `127.0.0.1:18080` for the duration of the sweep. It auto-starts at sweep startup and shuts down on exit.

---

## 9. Resumability

The runner reads `Docs/jitter-test-results.md` at startup and builds a hashset of every `run_id` already present (matching `^\| (?<runId>[A-Za-z0-9_\-]+) \|`). Any config whose `run_id` is in that set is skipped.

To force a re-run of a row, the user can manually delete its row from the results doc.

---

## 10. How to add new knobs (Opus future-work checklist)

When wiring a new flag (Layer 3 pipeline-code or other):
1. Add the field to `LaunchParameters` (constructor, property, parsing in `TryFromArgs`, default in `FromSettings`).
2. Apply the field in the appropriate runtime location (Program.cs, FramePump.cs, FrameRingBuffer.cs, NdiVideoPipeline.cs, etc.).
3. Update `generate-configs.ps1` to include rows that exercise the new flag (typically a new T5x or T6 tier).
4. Regenerate `configs.jsonl`: `pwsh ./Tools/JitterSweep/generate-configs.ps1`.
5. The new run_ids won't be in the existing results doc, so the next runner invocation will pick them up.
