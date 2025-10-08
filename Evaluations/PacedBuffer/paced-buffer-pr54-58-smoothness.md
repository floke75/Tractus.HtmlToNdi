# Paced buffer smoothness evaluation for PRs #54–#58

## Evaluation goal
The previous review cycles focused on holding a fixed latency bucket. For this pass the priority shifts to the smoothest possible motion: buffering logic should avoid visible stutter, keep cadence steady, and only fall back to frame repeats when the capture stream genuinely starves. Variable latency is acceptable if it improves motion quality.

The code for each pull request was inspected at its final merge commit:

| PR | Commit | Notes |
| --- | --- | --- |
| #54 | `ab83350ac1e57c57e311a09ecf94075d53eda829` | Implements strict warm-up gating with an integrator-driven drop loop. |
| #55 | `acceb7ef07a2c27b66eb3ac40bd72be833a0860a` | Adds two-tick hysteresis before repeating and a conservative drop guard. |
| #56 | `3eeef79f8d2b37d9b45d4531eecc07fb96d9ff55` | Drops after each send while the integrator is positive. |
| #57 | `fbb6b4b5d4342a565df4d45034f404bfc9f296be` | Similar to #56, with a split warm-up/primed state machine. |
| #58 | `59451d8fee865237c935fa09088d72b68c238eb6` | Uses an explicit warm-up state flag and post-send integrator updates. |

## Ranking (best → worst)

1. **PR #55 – “feat(video): Implement broadcast-ready paced buffer”**
   * Two consecutive low-backlog ticks are required before the sender repeats the last frame. Short jitter bursts therefore continue to drain fresh captures instead of freezing immediately, which keeps motion smooth while the producer catches up.
   * The integrator-driven drop only fires when the queue actually holds more than the configured depth, so it never trims the backlog below the safe threshold in the same tick. That avoids the oscillation that would otherwise cause an every-other-frame repeat cycle.
   * Warm-up resets the integrator debt to zero and immediately clears stale frames, letting the queue refill without carrying a large negative error that would prolong the repeat streak.

2. **PR #58 – “Implement fixed-latency paced buffer”**
   * Warm-up still triggers on the first low-backlog tick, so very small dips do repeat immediately, but backlog trimming is gentle: the integrator is updated *after* the send, which keeps it slightly negative in steady state and prevents the drop loop from firing unless the queue has genuinely over-filled.
   * Because the drop loop runs after the send and insists on `Count > targetDepthFrames`, it will only shed a single frame per tick, avoiding the deep drains that cause long repeat runs.
   * The drawback is that telemetry drifts—the post-send integrator bookkeeping reports persistent negative debt—but the motion stays relatively steady.

3. **PR #54 – “Implement fixed-latency paced buffer”**
   * This version repeats as soon as the backlog slips below `targetDepth - 0.5`, even if several frames remain in the queue. That guarantees cadence but produces visible freezes during brief producer jitter because fresh frames are withheld until the backlog fully rebuilds.
   * Its drop loop executes *before* the send and keeps running until the integrator falls below one. When the integrator has accumulated more than roughly two frames of credit, the loop drains the queue to the low-water mark and immediately re-enters warm-up, leaving the output stuck on repeats for multiple ticks.

4. **PR #57 – “Implement integrator-based paced buffer recovery”**
   * The warm-up and primed states are cleanly separated, yet the trimming policy mirrors PR #56: stale frames are discarded **after** each send while the integrator is above one. A sustained backlog of `target + 1` frames therefore builds a multi-frame positive error that is paid back by draining the queue down to one or zero entries.
   * Each payback forces a fresh warm-up cycle on the next tick, yielding an alternating pattern of new frame → repeat → repeat while the buffer refills—precisely the stutter pattern we are trying to avoid.

5. **PR #56 – “Implement integrator-driven paced buffering”**
   * Trimming is even more aggressive: `TrimForLatency` runs after every send and keeps discarding frames until the integrator falls below one, without checking whether the backlog has fallen under the low-water mark. Any oversupply of more than a couple of frames leaves the queue empty and re-triggers warm-up immediately.
   * The output then alternates between a single fresh frame and a long repeat streak while the buffer rebuilds, leading to pronounced judder under normal capture jitter.

## Shared gaps

Two submissions—PR #55 and PR #58—introduced helpers that trim the ring buffer back to the newest capture after an underrun but forgot to reset `overflowSinceLastDequeue`. The stale-drop telemetry therefore under-counts the frames that were discarded as part of the recovery cycle. The fix is to zero the overflow counter whenever the backlog is flushed to the latest frame.

## Follow-up implementation

To maximise smoothness while retaining the most helpful diagnostics, the in-repo pipeline now combines PR #55’s two-tick underrun hysteresis and conservative drop guard with the richer telemetry and stale-drop accounting from the stricter variants. The updated `Video/NdiVideoPipeline.cs` keeps motion steady under bursty capture while still surfacing underrun/drop metrics, and `Video/FrameRingBuffer.cs` now resets the overflow counter when trimming to the latest frame so stale-drop statistics remain accurate.
