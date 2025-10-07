# Paced output buffer behaviour

## Summary
This note explains how the video pipeline behaves when the paced output buffer is enabled versus disabled, and why the paced mode now introduces a deliberate latency “bucket” instead of immediately repeating frames on every empty tick. The warm-up behaviour means operators should expect approximately `BufferDepth / fps` of presentation delay once the buffer is primed.

## What drives Chromium renders
Chromium repaints are driven by `FramePump`. The pump wakes on a `PeriodicTimer` whose interval is derived from the configured frame rate, calls `Invalidate` on the browser host, and relies on CefSharp to raise `Paint` callbacks afterwards. A watchdog issues an extra invalidate if paints stop arriving for more than a second.【F:Video/FramePump.cs†L21-L74】

Each `Paint` event is forwarded to `NdiVideoPipeline.HandleFrame`, which wraps the buffer and hands it to the pipeline.【F:Chromium/CefWrapper.cs†L48-L82】

## Pipeline behaviour without buffering
When buffering is disabled the pipeline immediately sends each incoming frame. The timestamp for telemetry is taken at send time, and there is no additional pacing loop. As a result, the NDI sender runs at whatever cadence Chromium supplies, which is ultimately controlled by the single `FramePump` timer plus Chromium’s own scheduling.【F:Video/NdiVideoPipeline.cs†L48-L87】【F:Video/NdiVideoPipeline.cs†L93-L118】

## Pipeline behaviour with buffering enabled
With buffering enabled the following additional steps occur:

1. Every `Paint` copies the pixels into a heap-allocated `NdiVideoFrame`, stamps it with `DateTime.UtcNow`, and enqueues it in a `FrameRingBuffer` (dropping the oldest frame if the buffer is full).【F:Video/NdiVideoFrame.cs†L6-L33】【F:Video/FrameRingBuffer.cs†L4-L82】
2. The pacing task now waits until the ring buffer reaches its configured capacity before sending anything. During this warm-up the sender remains idle, allowing the buffer to accumulate `BufferDepth` frames (and therefore `BufferDepth / fps` of latency). Once primed, the loop drains frames in FIFO order via `TryDequeue`, preserving the capture cadence so long as Chromium continues to feed the queue.【F:Video/FrameRingBuffer.cs†L38-L82】【F:Video/NdiVideoPipeline.cs†L62-L188】
3. If the queue momentarily empties after priming, the pipeline emits a single repeat frame, records an `underruns` counter, and then re-enters warm-up until the backlog refills on two consecutive ticks. Telemetry now logs `primed`, `backlog`, `underruns`, `warmups`, and `lastWarmupMs` so operators can tell whether the bucket is healthy.【F:Video/NdiVideoPipeline.cs†L139-L234】

## Behaviour without buffering
When buffering is disabled the pipeline continues to send each incoming frame immediately using the direct path, with no additional warm-up delay or FIFO queueing.【F:Video/NdiVideoPipeline.cs†L48-L118】

## Implications
* Enabling the paced buffer intentionally adds latency equal to the configured depth. CLI help and operational docs should call this out so operators can trade latency for smoothness consciously.【F:Video/NdiVideoPipeline.cs†L139-L188】
* Telemetry consumers should switch to the new fields: `primed` reflects whether the bucket is currently transmitting, `backlog` is the number of queued frames waiting to send, `underruns` counts repeat events caused by empty buffers, and `warmups`/`lastWarmupMs` show how often and how long the pipeline had to re-prime.【F:Video/NdiVideoPipeline.cs†L200-L234】

