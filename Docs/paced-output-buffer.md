# Paced output buffer behaviour

## Summary
This note explains how the video pipeline behaves when the paced output buffer is enabled versus disabled. The paced path now behaves like a bucket: it fills to the configured depth before releasing frames, drains in FIFO order to maintain a fixed presentation delay, and re-enters a warm-up phase whenever the queue underruns.

## What drives Chromium renders
Chromium repaints are driven by `FramePump`. The pump wakes on a `PeriodicTimer` whose interval is derived from the configured frame rate, calls `Invalidate` on the browser host, and relies on CefSharp to raise `Paint` callbacks afterwards. A watchdog issues an extra invalidate if paints stop arriving for more than a second.【F:Video/FramePump.cs†L21-L74】

Each `Paint` event is forwarded to `NdiVideoPipeline.HandleFrame`, which wraps the buffer and hands it to the pipeline.【F:Chromium/CefWrapper.cs†L48-L82】

## Pipeline behaviour without buffering
When buffering is disabled the pipeline immediately sends each incoming frame. The timestamp for telemetry is taken at send time, and there is no additional pacing loop. As a result, the NDI sender runs at whatever cadence Chromium supplies, which is ultimately controlled by the single `FramePump` timer plus Chromium’s own scheduling.【F:Video/NdiVideoPipeline.cs†L48-L87】【F:Video/NdiVideoPipeline.cs†L93-L118】

## Pipeline behaviour with buffering enabled
With buffering enabled the following additional steps occur:

1. Every `Paint` copies the pixels into a heap-allocated `NdiVideoFrame`, stamps it with `DateTime.UtcNow`, and enqueues it in a `FrameRingBuffer` (dropping the oldest frame if the buffer is full).【F:Video/NdiVideoFrame.cs†L6-L33】【F:Video/FrameRingBuffer.cs†L4-L85】
2. The pacing task waits until the queue reaches `BufferDepth` before it begins transmission. Once primed, each timer tick dequeues the oldest buffered frame (FIFO), keeping the backlog close to the configured depth so downstream receivers observe a stable delay of roughly `BufferDepth / fps` seconds. If the backlog dips below the threshold for consecutive ticks or the queue drains completely, the loop repeats the previous frame, flags an underrun, and re-enters warm-up until enough new frames arrive.【F:Video/NdiVideoPipeline.cs†L62-L187】
3. Telemetry now includes `primed`, `bufferedBacklog`, `underruns`, `droppedOverflow`, `droppedStale`, and `warmupSeconds` so operators can see how much latency is buffered and how often the sender had to rely on repeats.【F:Video/NdiVideoPipeline.cs†L189-L215】

## Latency expectations
While buffering is enabled the output intentionally lags capture by `BufferDepth / fps` seconds after the bucket fills. This trades a small steady delay for resilience: short gaps in Chromium renders are absorbed by the backlog instead of causing visible jitter, and underruns are surfaced through the `underruns` counter and the accumulated `warmupSeconds` value.【F:Video/NdiVideoPipeline.cs†L62-L187】【F:Video/NdiVideoPipeline.cs†L189-L215】

