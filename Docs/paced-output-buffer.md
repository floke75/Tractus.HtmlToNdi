# Paced output buffer behaviour

## Summary
This note explains how the video pipeline behaves when the paced output buffer is enabled versus disabled, and documents the warm-up and underrun rules that keep latency predictable while smoothing cadence.

## What drives Chromium renders
Chromium repaints are driven by `FramePump`. The pump wakes on a `PeriodicTimer` whose interval is derived from the configured frame rate, calls `Invalidate` on the browser host, and relies on CefSharp to raise `Paint` callbacks afterwards. A watchdog issues an extra invalidate if paints stop arriving for more than a second.【F:Video/FramePump.cs†L21-L74】

Each `Paint` event is forwarded to `NdiVideoPipeline.HandleFrame`, which wraps the buffer and hands it to the pipeline.【F:Chromium/CefWrapper.cs†L48-L82】

## Pipeline behaviour without buffering
When buffering is disabled the pipeline immediately sends each incoming frame. The timestamp for telemetry is taken at send time, and there is no additional pacing loop. As a result, the NDI sender runs at whatever cadence Chromium supplies, which is ultimately controlled by the single `FramePump` timer plus Chromium’s own scheduling.【F:Video/NdiVideoPipeline.cs†L53-L94】【F:Video/NdiVideoPipeline.cs†L100-L143】

## Pipeline behaviour with buffering enabled
With buffering enabled the following additional steps occur:

1. Every `Paint` copies the pixels into a heap-allocated `NdiVideoFrame`, stamps it with `DateTime.UtcNow`, and enqueues it in a `FrameRingBuffer` (dropping the oldest frame if the buffer is full).【F:Video/NdiVideoFrame.cs†L6-L33】【F:Video/FrameRingBuffer.cs†L4-L70】
2. A background pacing task wakes on its own `PeriodicTimer`, which also uses the configured frame duration. The loop waits until the buffer holds `BufferDepth` frames before it sends anything; this “priming” step establishes a fixed latency of roughly `BufferDepth / fps` before the first paced frame appears.【F:Video/NdiVideoPipeline.cs†L96-L158】
3. Once primed, each pacing tick dequeues exactly one frame from the buffer and transmits it. If the backlog ever falls below the configured depth, the loop repeats the previously transmitted frame and re-enters warm-up, refusing to drain the queue until enough fresh frames accumulate to restore the latency bucket.【F:Video/NdiVideoPipeline.cs†L158-L200】
4. Telemetry shows whether the buffer is currently primed, how many frames are queued, how many frames were dropped due to overflow/staleness, how often underruns occurred, and how long the last warm-up took. This makes it easy to correlate on-air stutter with buffer health.【F:Video/NdiVideoPipeline.cs†L218-L245】

## Implications
* Operators should expect approximately `BufferDepth / fps` of added latency when enabling the paced buffer. That delay remains constant unless the capture side falls behind, in which case the output repeats the last frame until the backlog is healthy again.
* Because the pacing loop refuses to send partially warmed buffers, jitter in Chromium’s paint cadence is absorbed by repeating the previous frame rather than delivering new frames early. This keeps presentation cadence smooth even if it occasionally requires repeats.
* Telemetry consumers can monitor `primed`, `backlog`, `underruns`, and `warmups` to determine whether the buffer configuration is sufficient for the rendered workload.【F:Video/NdiVideoPipeline.cs†L218-L245】

