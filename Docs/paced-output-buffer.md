# Paced output buffer behaviour

## Summary
This note explains how the video pipeline behaves when the paced output buffer is enabled versus disabled, and why the paced mode currently introduces visible stutter even though it is meant to smooth the stream.

## What drives Chromium renders
Chromium repaints are driven by `FramePump`. The pump wakes on a `PeriodicTimer` whose interval is derived from the configured frame rate, calls `Invalidate` on the browser host, and relies on CefSharp to raise `Paint` callbacks afterwards. A watchdog issues an extra invalidate if paints stop arriving for more than a second.【F:Video/FramePump.cs†L21-L74】

Each `Paint` event is forwarded to `NdiVideoPipeline.HandleFrame`, which wraps the buffer and hands it to the pipeline.【F:Chromium/CefWrapper.cs†L48-L82】

## Pipeline behaviour without buffering
When buffering is disabled the pipeline immediately sends each incoming frame. The timestamp for telemetry is taken at send time, and there is no additional pacing loop. As a result, the NDI sender runs at whatever cadence Chromium supplies, which is ultimately controlled by the single `FramePump` timer plus Chromium’s own scheduling.【F:Video/NdiVideoPipeline.cs†L48-L87】【F:Video/NdiVideoPipeline.cs†L93-L118】

## Pipeline behaviour with buffering enabled
With buffering enabled the following additional steps occur:

1. Every `Paint` copies the pixels into a heap-allocated `NdiVideoFrame`, stamps it with `DateTime.UtcNow`, and enqueues it in a `FrameRingBuffer` (dropping the oldest frame if the buffer is full).【F:Video/NdiVideoFrame.cs†L6-L33】【F:Video/FrameRingBuffer.cs†L4-L58】
2. A background pacing task wakes on its own `PeriodicTimer`, which also uses the configured frame duration. On each tick it dequeues the newest buffered frame (disposing any older ones) and sends it. If the buffer is empty, it re-sends the most recently transmitted frame to hold the cadence.【F:Video/NdiVideoPipeline.cs†L62-L112】【F:Video/FrameRingBuffer.cs†L60-L85】
3. Telemetry shows the counts of repeated frames, overflow drops, and stale drops so the operator can see how often the paced loop failed to obtain a fresh frame.【F:Video/NdiVideoPipeline.cs†L200-L234】

## Why paced mode currently stutters
The render pump and the paced output loop both run off independent `PeriodicTimer` instances that are configured with exactly the same interval (the requested frame duration) but have no phase coordination. When the pacing loop wakes before a new `Paint` has been enqueued, `DequeueLatest` returns `null`, so the pipeline repeats the previous frame to maintain cadence. That repeated frame increments the `repeated` counter seen in telemetry. Because the two timers continually drift relative to one another, this situation recurs regularly, producing the pattern of “captured slightly ahead of sent” with dozens of repeats in the supplied log (e.g., 47 repeats after ~1,400 frames).【F:Video/FramePump.cs†L21-L47】【F:Video/NdiVideoPipeline.cs†L62-L112】【F:Video/NdiVideoPipeline.cs†L139-L198】

In contrast, when buffering is disabled there is only one timing source (Chromium’s paint cadence), so each frame is sent immediately after it is produced and no repeats are generated.

The current design therefore trades the direct path’s minimal latency for a paced loop that repeatedly falls back to `RepeatLastFrame`. Instead of smoothing, this manifests as visible stutter because viewers receive duplicate frames at roughly the same rate that the two timers fall out of phase.

## Implications
* The paced mode can still absorb short-term spikes if Chromium briefly outruns the sender, but it does not actively smooth cadence when both sides are running at the same nominal rate.
* Any improvement needs either tighter coordination (e.g., pacing off the capture timestamps, or waiting for a new frame before transmitting) or a decoupled pump cadence so the buffer regularly holds multiple frames when the paced loop fires.

