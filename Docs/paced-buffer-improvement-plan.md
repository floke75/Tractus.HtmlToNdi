# Paced buffer improvement plan

## Context

The paced-buffer pull requests already reviewed in [`Docs/paced-buffer-pr-evaluation.md`](paced-buffer-pr-evaluation.md) explore
different compromises between latency stability and smooth output cadence. The report
highlights how each revision behaves under underruns and during warm-up, revealing
recurring trade-offs:

- Some revisions (PR41, PR47 rev2) preserve cadence by draining or clearing the
  backlog, but they let the effective latency collapse whenever the queue runs
  low.【F:Docs/paced-buffer-pr-evaluation.md†L8-L43】【F:Docs/paced-buffer-pr-evaluation.md†L59-L83】
- Others (PR43–PR45) try to hold the latency bucket, yet they still leak fresh or
  stale frames during warm-up so the actual delay drifts, and their telemetry can
  over-count underruns.【F:Docs/paced-buffer-pr-evaluation.md†L24-L38】【F:Docs/paced-buffer-pr-evaluation.md†L44-L58】
- PR44 provides the best smoothness so far by allowing a single low-backlog tick
  before re-priming, but it still lets latency shrink by at least one frame and
  replays stale captures after recovery.【F:Docs/paced-buffer-pr-evaluation.md†L40-L58】

The current best compromise therefore maintains cadence but cannot guarantee a
stable, predictable delay during every recovery sequence. Live broadcast graphics
benefit most from an unwavering cadence, yet engineers also need to know exactly
how much latency they are paying for that smoothness. There is still room to
improve both sides simultaneously.

## Goals for a broadcast-ready pacer

To combine fixed-latency delivery with optimal smoothness, the buffering layer
needs to enforce four invariants at the same time:

1. **Strict latency guard** – Once the application leaves warm-up, every frame
   must wait at least the configured buffer depth before it is transmitted. No
   underrun may reduce the effective delay unless the operator explicitly
   relaxes the depth.
2. **Cadence continuity** – Output cadence must never stall. When capture
   jitter drains the queue the sender repeats the last delivered frame instead
   of emitting gaps.
3. **Fresh recovery** – After an underrun, stale captures from before the stall
   must be discarded so that recovery resumes with the latest material at the
   target delay. This prevents latency from creeping upwards.
4. **Visible telemetry** – Operators need underrun counters that tick once per
   recovery event, plus insight into how long the pacer stayed in the frozen
   state.

The evaluated PRs each satisfy only a subset of these rules; a complete solution
can merge their strengths.

## Proposed algorithm

The following design keeps latency fixed while smoothing cadence with a small
hysteresis window and an error integrator that avoids repeated warm-up toggles. It also includes an optional "latency expansion" mode for smoother recovery.

1. **State tracking**
   - `targetDepth` – configured backlog, expressed in frames.
   - `highWatermark = targetDepth + 1` – upper limit that triggers frame drops
     when the producer outruns the pacer for prolonged periods.
   - `lowWatermark = targetDepth - 0.5` – lower tolerance that keeps the pacer
     out of warm-up until the backlog is genuinely shallow.
   - `warmup` flag – true until the queue reaches `targetDepth` and after any
     underrun.
   - `latencyError` accumulator – integrates `(queueCount - targetDepth)` each
     tick to smooth decisions about repeats and drops.
   - `enableLatencyExpansion` flag - determines the recovery strategy on underrun.
   - `isLatencyExpansionActive` flag - true when the pacer is in the latency expansion sub-state.

2. **Normal pacing**
   - When `warmup` is false and the queue count is above `lowWatermark`, dequeue
     the oldest frame, send it, and update `latencyError` by adding the depth
     delta for this tick. A positive `latencyError` larger than `1` indicates
     that the pacer has been too far ahead; drop queued frames (without
     sending them) until the error is corrected to resynchronise the
     latency without bursting output.
   - If the queue count falls to or below `lowWatermark`, trigger an underrun.

3. **Warm-up / underrun handling**
   - On entry, increment the underrun counter once and record the timestamp.
   - The recovery strategy depends on the `EnableLatencyExpansion` setting:
     - **Default (Strict Latency):** Immediately drain the queue down to a single latest frame, discarding older captures so recovery starts fresh. Reset the `latencyError` integrator to zero to ensure rapid recovery. Continue emitting the last transmitted frame every tick until the buffer is re-primed.
     - **Latency Expansion Mode:** If the buffer contains frames, preserve them. Enter a "latency expansion" state where the pacer continues to send the remaining buffered frames. This provides smoother motion during recovery at the cost of a temporary increase in latency. If the buffer is empty, fall back to repeating the last transmitted frame.
   - Exit warm-up only after the queue reaches `targetDepth` **and** the
     integrator is non-negative; this ensures we have both the required backlog
     and enough accumulated slack to resume without an instant re-trigger.

4. **Telemetry**
   - Log underrun entries with the warm-up duration and the number of repeated
     or expansion-mode frames.
   - Expose warm-up cycle counts, latency error, and latency expansion ticks.
   - Ensure `FrameRingBuffer` correctly resets its internal counters after a drain operation to keep stale drop counts accurate.

This hybrid uses PR47’s decisive queue clearing, PR43/PR44’s cadence-preserving
repeats, and PR41’s insistence on regaining the full backlog before resuming
fresh frames. The small hysteresis and integrator suppress the chatter seen in
PR41 while ensuring the latency bucket never collapses below the chosen depth. The optional latency expansion mode provides a new trade-off for users who prioritize smooth motion over fixed latency during recovery.

## Implementation notes

- The `FrameRingBuffer` has been updated with a `DrainToLatestAndKeep` method that correctly resets its internal telemetry counters.
- The pacer loop maintains the `latencyError` as a `double` to capture
  fractional drift between producer and consumer cadence. Because the error is
  additive, it can also drive adaptive logging that estimates the effective
  latency in milliseconds for dashboards.
- The high-watermark check has been improved to a `while` loop to correct latency more decisively.
- Tests should simulate producer jitter bursts (dropouts, speed-ups, and long
  over-production spurts) to verify that the output cadence remains constant
  and that both recovery modes (strict and latency expansion) behave as expected.
- When buffering is disabled the existing zero-copy path remains untouched, as
  required by the prior evaluation.【F:Docs/paced-buffer-pr-evaluation.md†L65-L83】

## Conclusion

Yes—there is room to build a buffering implementation that achieves both stable
latency and smooth pacing. By combining rigorous warm-up gating, aggressive
queue hygiene, and a gentle hysteresis/integrator controller, the pacer can
produce the broadcast-grade, judder-free output required for live tickers and
lower thirds.

With the addition of a configurable **latency expansion mode**, operators can choose their preferred trade-off during network instability:
- **Strict Latency (Default):** Guarantees a fixed, predictable delay by draining the buffer on underrun, which is ideal for synchronized multi-source productions.
- **Smooth Recovery:** Prioritizes fluid motion by playing out the remaining buffer, which is better for single-source graphics where a temporary latency increase is acceptable.

This dual-mode approach provides the flexibility needed for a wider range of broadcast scenarios.
