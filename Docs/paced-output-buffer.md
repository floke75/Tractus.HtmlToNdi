# Paced output buffer behaviour

## Summary
The paced video pipeline introduces a predictable presentation delay so receivers
see evenly spaced frames even if Chromium paints arrive slightly early or late.
With buffering disabled the pipeline sends each `Paint` immediately. When
buffering is enabled the capture thread copies the pixels into a ring buffer and
waits for the queue to reach the configured depth before presenting anything to
NDI. Once primed, the pacing loop drains the buffer in FIFO order, maintaining a
constant latency of roughly `BufferDepth / fps` seconds. If the queue ever
drains faster than Chromium can refill it the pipeline repeats the most recent
frame, records an underrun, and re-enters the warm-up phase before allowing new
frames through.

## What drives Chromium renders
Chromium repaints are driven by `FramePump`. The pump wakes on a
`PeriodicTimer` whose interval is derived from the configured frame rate, calls
`Invalidate` on the browser host, and relies on CefSharp to raise `Paint`
callbacks afterwards. A watchdog issues an extra invalidate if paints stop
arriving for more than a second.【F:Video/FramePump.cs†L21-L74】

Each `Paint` event is forwarded to `NdiVideoPipeline.HandleFrame`, which wraps
the buffer and hands it to the pipeline.【F:Chromium/CefWrapper.cs†L48-L82】

## Pipeline behaviour without buffering
When buffering is disabled the pipeline immediately sends each incoming frame.
The timestamp for telemetry is taken at send time, and there is no additional
pacing loop. As a result, the NDI sender runs at whatever cadence Chromium
supplies, which is ultimately controlled by the single `FramePump` timer plus
Chromium’s own scheduling.【F:Video/NdiVideoPipeline.cs†L48-L87】【F:Video/NdiVideoPipeline.cs†L93-L118】

## Pipeline behaviour with buffering enabled
With buffering enabled the following additional steps occur:

1. Every `Paint` copies the pixels into a heap-allocated `NdiVideoFrame`, stamps
   it with `DateTime.UtcNow`, and enqueues it in a `FrameRingBuffer` (dropping
   the oldest frame if the buffer is full).【F:Video/NdiVideoFrame.cs†L6-L33】【F:Video/FrameRingBuffer.cs†L4-L88】
2. The pacing loop waits for the queue to reach the configured depth before
   sending anything. Once primed, each tick consumes the oldest frame so the
   output delay remains close to `BufferDepth / fps` seconds.【F:Video/NdiVideoPipeline.cs†L104-L170】
3. If the backlog drops below the threshold for consecutive ticks or the buffer
   is empty, the loop repeats the last transmitted frame, increments the
   underrun counter once, and requires the queue to warm up again before using
   fresh frames.【F:Video/NdiVideoPipeline.cs†L138-L170】【F:Video/NdiVideoPipeline.cs†L188-L215】
4. Telemetry now reports whether the buffer is primed, the live backlog, how
   many frames were dropped to overflow, underrun counts, the number of warm-up
   cycles, and the duration of the most recent warm-up so operators can confirm
   the bucket is behaving as expected.【F:Video/NdiVideoPipeline.cs†L212-L246】

## Warm-up latency and underruns
Because the paced loop waits for the queue to fill, enabling buffering
introduces a deliberate latency of roughly `BufferDepth / fps` seconds before
the first frame is transmitted (and again after any underrun that forces a
re-warm). While warming the pipeline repeats the last delivered frame so NDI
receivers continue to see a steady cadence. Operators should budget for that
added delay and size the buffer deep enough that Chromium can stay ahead of the
pacer without constantly re-priming.
