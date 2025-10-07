# Paced buffer PR evaluation (PR41–PR47)

This report compares the paced-buffer proposals and highlights the gaps that
prevent each submission from delivering the promised “constant-latency, smooth
cadence” behaviour. The final section revisits the analysis under a relaxed
goal where consistent cadence is worth a little extra latency drift.

## PR41 – `Stabilize paced buffering with constant-latency warmup`
*Warm-up gates new output until the backlog reaches the target depth, but the
underrun handler drains the queue, swaps to the newest frame, and immediately
replays it.* During an underrun the loop removes every queued frame, assigns the
freshest capture to `lastSentFrame`, and calls `RepeatLastFrame` right away, so
viewers see brand-new content while the backlog is still empty.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr41.cs†L139-L217】
That behaviour collapses the intended fixed latency instead of holding a steady
delay until the buffer refills.

## PR42 – `Adaptive paced buffering with semaphore`
Warm-up simply `continue`s when the backlog is shallow, so the sender emits no
frames—not even repeats—while the queue is refilling, creating visible gaps in
the output.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr42.cs†L158-L171】 Once
the buffer is primed the loop transmits any time the semaphore reports one
available frame, letting the backlog (and therefore latency) shrink well below
the configured depth.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr42.cs†L175-L197】
Latency therefore varies with producer jitter.

## PR43 – `Refine paced buffer warm-up and FIFO behaviour`
The first tick that falls below the threshold still dequeues and sends a fresh
frame before warm-up begins, so the latency bucket always loses at least one
frame.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr43.cs†L125-L161】 When the
loop finally re-enters warm-up it only toggles flags and repeats the last
transmitted frame—the backlog is left intact—so any older captures in the queue
are replayed after recovery, stretching latency beyond the configured
depth.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr43.cs†L214-L231】

## PR44 – `Improve paced buffer warm-up and telemetry`
`TrySendBufferedFrame` again allows one low-backlog tick to drain a new frame
before warm-up kicks in, reducing the delay envelope by at least a single
frame.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr44.cs†L48-L90】 The warm-up
transition increments counters but never discards residual queue contents, so
stale frames captured before the underrun are still aired once the bucket is
primed again, which makes real latency wander upwards.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr44.cs†L232-L242】

## PR45 – `Implement FIFO paced video buffer`
The pacing logic mirrors PR43’s one-extra-send behaviour, so latency drops on
the first low-backlog tick.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr45.cs†L123-L167】
It also increments the underrun counter on every repeat while the buffer is
warming up, inflating telemetry with per-tick counts instead of logging the
underrun event once.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr45.cs†L169-L173】
The backlog is never cleared during warm-up, so stale frames still leak
through.

## PR47 (rev2) – `Hybrid paced buffering`
The updated branch now repeats the previously transmitted frame while the
backlog is refilling and clears the queue when an underrun occurs, so recovery
starts with fresh captures instead of stale ones.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr47.v2.cs†L133-L178】 However,
once primed it dequeues and sends frames any time a single item is available.
That allows the backlog—and the effective latency bucket—to shrink below the
configured depth until the queue is empty, so fixed delay is still not
maintained.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr47.v2.cs†L141-L157】

### Comparison with PR44
PR44 re-enters warm-up after the first low-backlog tick, which keeps latency
close to the configured depth at the cost of repeating frames more eagerly.
PR47’s revised pacing waits until the queue is completely empty before
triggering warm-up again, which can deliver marginally smoother motion during
minor producer hiccups but sacrifices the tighter latency envelope that PR44
preserves.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr44.cs†L48-L118】【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr47.v2.cs†L141-L178】

## Recommended direction (fixed-latency goal)
None of the submissions maintain both invariants simultaneously: a fixed
latency bucket and uninterrupted cadence. To achieve both we need to:

1. **Freeze on the last delivered frame immediately when the backlog dips.**
   Do not send fresh captures until the queue has refilled to the configured
depth; this keeps latency constant instead of shrinking on underruns.
2. **Discard stale queue entries during warm-up.** When re-priming, clear or
   fast-forward the backlog so recovery resumes with frames captured after the
   stall, preventing the latency from ballooning.
3. **Count underruns per transition.** Increment telemetry once when entering
   warm-up, not on every repeat tick, so diagnostics stay meaningful.
4. **Preserve existing direct-send behaviour.** The zero-buffer path must remain
   untouched to avoid regression in the low-latency mode.

Implementing those rules combines PR41’s queue-draining insight with the
stronger warm-up gating from PR43/PR44, yielding a paced loop that really does
trade a fixed, predictable delay for smoother presentation.

## Alternate ranking when smoothness outranks fixed latency

If we soften the requirement for a rigid latency bucket and instead focus on
avoiding visible judder, the ordering shifts:

1. **PR44** becomes the strongest candidate. Allowing a single low-backlog tick
   before re-warming greatly reduces how often the pacer has to repeat frames,
   which keeps motion fluid even when the producer jitters. The loop still
   repeats the last frame during warm-up, so output cadence never stops, and it
   pairs the behaviour with thorough telemetry and tests.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr44.cs†L18-L210】
2. **PR43** follows closely: it shares the same “one free dip” strategy but
   lacks the improved instrumentation and still leaves warm-up bookkeeping a bit
   implicit.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr43.cs†L118-L233】
3. **PR41** slips to third because it immediately re-enters warm-up whenever the
   backlog falls below the configured depth, which makes repeats (and therefore
   perceptible stutter) more common during minor producer hiccups, and its
   aggressive queue drain drops frames that could otherwise have smoothed the
   catch-up.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr41.cs†L143-L217】
4. **PR45** inherits PR43’s pacing but inflates underrun telemetry by counting
   every repeat tick, so observability degrades even though output smoothness is
   similar.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr45.cs†L123-L173】
5. **PR47 (rev2)** keeps cadence through warm-up, but every underrun clears the
   backlog and demands a full refill before new content can appear. That yields
   long stretches of repeated frames whenever capture hiccups persist, even
   though the steady-state latency collapses toward zero once primed.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr47.v2.cs†L133-L178】
6. **PR42** still skips output during warm-up and resumes as soon as a single
   frame is queued, so playback contains visible gaps and the delay swings with
   producer jitter.【F:Evaluations/PacedBuffer/NdiVideoPipeline.pr42.cs†L158-L197】

Under this relaxed objective PR44’s blend of smooth cadence, gentle latency
adaptation, and rich telemetry makes it the best drop-in improvement.

