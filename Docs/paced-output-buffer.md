# Paced output buffer behaviour

## Summary
This note explains how the video pipeline behaves when the paced output buffer is enabled versus disabled. When pacing is active the pipeline queues captured frames until the backlog reaches the configured depth, then drains one frame per timer tick. That “bucket” introduces a predictable latency of roughly `BufferDepth / fps` seconds while ensuring the NDI sender presents frames at an even cadence. If the queue dips below the target depth the sender repeats the last frame and re-warms before transmitting fresh video again.

## What drives Chromium renders
Chromium repaints are driven by `FramePump`. The pump wakes on a `PeriodicTimer` whose interval is derived from the configured frame rate, calls `Invalidate` on the browser host, and relies on CefSharp to raise `Paint` callbacks afterwards. A watchdog issues an extra invalidate if paints stop arriving for more than a second.【F:Video/FramePump.cs†L7-L115】

Each `Paint` event is forwarded to `NdiVideoPipeline.HandleFrame`, which wraps the buffer and hands it to the pipeline.【F:Chromium/CefWrapper.cs†L43-L123】

## Pipeline behaviour without buffering
When buffering is disabled the pipeline immediately sends each incoming frame. The timestamp for telemetry is taken at send time, and there is no additional pacing loop. As a result, the NDI sender runs at whatever cadence Chromium supplies, which is ultimately controlled by the single `FramePump` timer plus Chromium’s own scheduling.【F:Video/NdiVideoPipeline.cs†L43-L114】

## Pipeline behaviour with buffering enabled
With buffering enabled the following additional steps occur:

1. Every `Paint` copies the pixels into a heap-allocated `NdiVideoFrame`, stamps it with `DateTime.UtcNow`, and enqueues it in a `FrameRingBuffer` (dropping the oldest frame if the bucket is full).【F:Video/NdiVideoFrame.cs†L6-L34】【F:Video/FrameRingBuffer.cs†L5-L77】
2. The pacing task wakes on its own `PeriodicTimer`. While the bucket is filling it does not transmit new frames; once the backlog reaches `BufferDepth` it marks the buffer as primed and sends one frame per tick in FIFO order.【F:Video/NdiVideoPipeline.cs†L116-L181】
3. If the backlog stays below the requested depth for consecutive ticks or the queue is empty, the sender repeats the most recently transmitted frame, increments an underrun counter, and re-enters warm-up until enough fresh frames arrive to restore the desired latency.【F:Video/NdiVideoPipeline.cs†L118-L181】
4. Telemetry now surfaces the primed state, live backlog, overflow/stale drops, underruns, warm-up cycles, and the duration of the most recent warm-up so operators can see how healthy the buffer is at a glance.【F:Video/NdiVideoPipeline.cs†L218-L241】

## Latency expectations
Enabling the paced buffer intentionally lags capture by `BufferDepth / fps` seconds. That latency appears when the application starts and after every underrun because the sender waits for the queue to refill before resuming normal transmission. During those warm-up periods the pipeline keeps the NDI cadence steady by repeating the last frame, so downstream receivers never lose the clock even though no fresh video is available.【F:Video/NdiVideoPipeline.cs†L116-L181】

## Implications
* Enabling pacing is a conscious trade-off: operators should size `BufferDepth` to balance acceptable latency against resilience to jitter.【F:Video/NdiVideoPipeline.cs†L116-L181】
* Telemetry consumers should look for `primed`, `buffered`, `underruns`, `warmups`, and `lastWarmupMs` in the log output to understand how the buffer is behaving.【F:Video/NdiVideoPipeline.cs†L218-L241】
* When buffering is disabled the legacy direct path remains untouched—frames are forwarded immediately with effectively zero additional latency.【F:Video/NdiVideoPipeline.cs†L43-L114】
