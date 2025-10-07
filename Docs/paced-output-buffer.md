# Paced output buffer behaviour

## Summary
This note explains how the video pipeline behaves when the paced output buffer is enabled versus disabled, and how the revised "bucket" design warms up before transmission to provide a consistent presentation delay.

## What drives Chromium renders
Chromium repaints are driven by `FramePump`. The pump wakes on a `PeriodicTimer` whose interval is derived from the configured frame rate, calls `Invalidate` on the browser host, and relies on CefSharp to raise `Paint` callbacks afterwards. A watchdog issues an extra invalidate if paints stop arriving for more than a second.【F:Video/FramePump.cs†L21-L74】

Each `Paint` event is forwarded to `NdiVideoPipeline.HandleFrame`, which wraps the buffer and hands it to the pipeline.【F:Chromium/CefWrapper.cs†L48-L82】

## Pipeline behaviour without buffering
When buffering is disabled the pipeline immediately sends each incoming frame. The timestamp for telemetry is taken at send time, and there is no additional pacing loop. As a result, the NDI sender runs at whatever cadence Chromium supplies, which is ultimately controlled by the single `FramePump` timer plus Chromium’s own scheduling.【F:Video/NdiVideoPipeline.cs†L48-L87】【F:Video/NdiVideoPipeline.cs†L93-L118】

## Pipeline behaviour with buffering enabled
With buffering enabled the following additional steps occur:

1. Every `Paint` copies the pixels into a heap-allocated `NdiVideoFrame`, stamps it with `DateTime.UtcNow`, and enqueues it in a `FrameRingBuffer` (dropping the oldest frame if the buffer is full).【F:Video/NdiVideoFrame.cs†L6-L33】【F:Video/FrameRingBuffer.cs†L4-L77】
2. The buffer warms up: the pacing task holds transmission until the queue reaches the configured depth. Once primed, each tick consumes the oldest frame in FIFO order so the output is delayed by roughly `BufferDepth / fps` seconds. If the queue depth falls below the threshold for more than one tick the pipeline re-enters warm-up before resuming sends.【F:Video/NdiVideoPipeline.cs†L24-L214】
3. While primed, the pacing task dequeues one frame per tick. If it unexpectedly runs dry it repeats the last delivered frame, records an underrun, and forces another warm-up cycle before using fresh frames again.【F:Video/NdiVideoPipeline.cs†L126-L214】
4. Telemetry now reports the live backlog (`buffered`), overflow drops, stale drops, underruns, the number of warm-up cycles, and the most recent warm-up duration so operators can track how the bucket behaves.【F:Video/NdiVideoPipeline.cs†L200-L236】

## Warm-up latency and underruns
Because the paced loop waits for the queue to fill, enabling buffering introduces a deliberate latency of roughly `BufferDepth / fps` seconds before the first frame is transmitted (and again after any underrun that forces re-warm). Operators should budget for that delay and only enable the feature when additional latency is acceptable.

The buffer still absorbs short-term spikes. When Chromium briefly outruns the sender the backlog grows, but transmission continues smoothly because the queue remains primed. Only when the backlog collapses for multiple ticks does the pipeline fall back to repeating the previous frame, increment the `underruns` counter, and re-enter the warm-up phase before resuming FIFO playback.

