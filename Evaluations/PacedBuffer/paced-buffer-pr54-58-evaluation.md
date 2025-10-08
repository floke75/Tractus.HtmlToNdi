# Paced buffer evaluation for PRs #54–#58

## Overall ranking (smoothness first)
1. **PR #55** – Warmup hysteresis and integrator resets minimise repeats while still trimming gently when the buffer genuinely overruns.
2. **PR #58** – Allows the backlog to float deeper, so minor capture jitter rarely drains the queue enough to re-trigger warmup, though recoveries stay long because the integrator remains negative during warmup.
3. **PR #54** – Meets the fixed-latency spec, but the single-tick low-watermark trigger and pre-send trim loop drop straight into warmup, producing repeat runs after even short dips.
4. **PR #57** – Similar single-tick warmup gating, and the post-send trim loop can empty the queue on oversupply, so the next tick still hits warmup debt.
5. **PR #56** – TrimForLatency drops frames until the integrator debt clears regardless of backlog, so oversupply collapses the queue and forces full warmup cycles more often than the others.

## Detailed notes
### PR #55 – `feat(video): Implement broadcast-ready paced buffer`
- Uses a two-tick low-backlog streak before entering warmup, so brief dips keep sending fresh frames instead of freezing immediately.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr55.cs†L109-L130】
- On warmup entry the integrator is reset to zero, meaning recovery ends as soon as the configured depth is rebuilt instead of waiting for extra positive debt.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr55.cs†L63-L77】【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr55.cs†L80-L105】
- Drops for oversupply only fire while both the integrator is high and the queue still holds more than the target depth, avoiding the “trim to empty” spiral seen elsewhere.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr55.cs†L132-L147】
- One telemetry gap remains: `DrainToLatestAndKeep` never clears `overflowSinceLastDequeue`, so the stale-drop counter will under-report right after an underrun.【F:Evaluations/PacedBuffer/FrameRingBuffer.pr55.cs†L119-L142】

### PR #58 – `Implement fixed-latency paced buffer`
- Warmup uses the same single-tick low-watermark guard as PRs #54/#56/#57, but the buffer capacity is `targetDepth + 2` and trimming only occurs once the queue is deeper than the configured depth, so the backlog naturally rides at 4–5 frames and shrugs off one-frame capture stalls.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr58.cs†L45-L109】【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr58.cs†L306-L325】
- During warmup the integrator is clamped to `-targetDepth`, so recovery does not finish until the queue has accumulated a couple of extra frames to repay that debt, lengthening every freeze despite the smoother guard.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr58.cs†L74-L107】【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr58.cs†L295-L304】
- As in PR #55, the trim helper leaves `overflowSinceLastDequeue` untouched, so stale-drop telemetry is skewed after underruns.【F:Evaluations/PacedBuffer/FrameRingBuffer.pr58.cs†L107-L132】

### PR #54 – `Implement fixed-latency paced buffer`
- Immediately jumps back to warmup on the first low-watermark tick and drops into repeat mode that same frame, so even a single late capture results in a multi-frame freeze.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr54.cs†L98-L142】
- The integrator debt is only clamped to zero on entry, so recovery holds repeats until the queue has been full for several ticks, extending freezes further.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr54.cs†L75-L95】【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr54.cs†L280-L309】
- Oversupply trimming happens before the send; when the integrator is large it can push the backlog below the low watermark and immediately re-trigger warmup, dropping cadence instead of riding out the extra frames.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr54.cs†L110-L132】

### PR #57 – `Implement integrator-based paced buffer recovery`
- Shares the one-tick low-watermark trigger with PR #54, so it also freezes instantly when the backlog briefly dips.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr57.cs†L184-L205】
- TrimIfAhead executes after the send and keeps dropping while the integrator is high, even if the queue has already shrunk to zero; the next tick then re-enters warmup and repeats for several frames.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr57.cs†L214-L226】
- Warmup entry still clears the queue to the latest frame and clamps the integrator negative, so recoveries remain long despite the nicer state split.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr57.cs†L302-L321】

### PR #56 – `Implement integrator-driven paced buffering`
- The warmup guard trips on the first low-watermark tick and repeats immediately, just like PR #54.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr56.cs†L84-L107】
- `TrimForLatency` has no backlog guard, so a sustained positive integrator debt drops frames until the queue empties, guaranteeing another warmup cycle even if capture quickly returns to normal.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr56.cs†L416-L428】
- EnterWarmup always discards everything except the latest capture and clamps the integrator negative, stretching every recovery.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr56.cs†L232-L261】

## Cross-PR observations
- Every submission still discards all but the newest capture on underrun, so recovery always consists of several repeated frames while the backlog rebuilds; none of the branches try to keep older frames to trade extra latency for smoother motion.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr54.cs†L278-L283】【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr55.cs†L118-L125】【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr56.cs†L239-L261】【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr57.cs†L302-L321】【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr58.cs†L244-L259】
- Both PR #55 and PR #58 add helpers that trim to the latest frame without resetting the ring buffer’s overflow bookkeeping, so stale-drop telemetry will remain suppressed after an underrun until enough fresh frames are dequeued.【F:Evaluations/PacedBuffer/FrameRingBuffer.pr55.cs†L119-L142】【F:Evaluations/PacedBuffer/FrameRingBuffer.pr58.cs†L107-L132】
