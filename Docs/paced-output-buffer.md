# Paced output buffer behaviour

## Summary
The paced NDI pipeline now behaves like a true bucket: it warms up until the
ring buffer holds the configured number of frames, drains that buffer in FIFO
order to maintain a constant presentation delay, and only repeats frames when
an underrun occurs. Operators should expect steady-state latency of roughly
`BufferDepth / fps` seconds once the bucket is primed.

## What drives Chromium renders
Chromium repaints are still driven by `FramePump`. The pump wakes on a
`PeriodicTimer` derived from the configured frame rate, invalidates the browser
host, and relies on CefSharp to raise `Paint` callbacks afterwards. A watchdog
issues an extra invalidate if paints stop arriving for more than a second.【F:Video/FramePump.cs†L7-L115】

Each `Paint` event is forwarded to `NdiVideoPipeline.HandleFrame`, which wraps
the buffer and hands it to the pipeline.【F:Chromium/CefWrapper.cs†L43-L123】

## Pipeline behaviour without buffering
When buffering is disabled the pipeline immediately sends each incoming frame.
Timestamps for telemetry are captured at send time and there is no pacing loop,
so the NDI sender runs at whatever cadence Chromium supplies.【F:Video/NdiVideoPipeline.cs†L40-L101】【F:Video/NdiVideoPipeline.cs†L171-L200】

## Pipeline behaviour with buffering enabled
With buffering enabled the following additional steps occur:

1. Every `Paint` copies the pixels into a heap-allocated `NdiVideoFrame`, stamps
   it with `DateTime.UtcNow`, and enqueues it in a `FrameRingBuffer` (dropping
   the oldest frame if the bucket is already full).【F:Video/NdiVideoFrame.cs†L6-L33】【F:Video/FrameRingBuffer.cs†L5-L118】
2. A background pacing task wakes on its own `PeriodicTimer`. While the bucket
   is still filling it simply waits; once the backlog reaches the configured
   depth it marks the buffer as primed, records how long that warm-up took, and
   begins draining the queue.【F:Video/NdiVideoPipeline.cs†L52-L164】【F:Video/NdiVideoPipeline.cs†L283-L339】
3. In the primed state the loop removes frames in FIFO order via
   `TryDequeue`. If the buffer briefly dips below the target depth it keeps
   sending, but if the underfill persists for more than one tick the pipeline
   re-enters warm-up so the backlog can rebuild.【F:Video/FrameRingBuffer.cs†L59-L118】【F:Video/NdiVideoPipeline.cs†L123-L164】
4. When `TryDequeue` finds the bucket empty the pipeline records an `underruns`
   counter, re-sends the most recent frame, and then waits to re-prime before
   transmitting fresh content again.【F:Video/NdiVideoPipeline.cs†L156-L219】
5. Telemetry now includes `primed`, the live backlog (`buffered`), the number of
   `underruns`, the duration of the last warm-up (`warmupMs`), and the existing
   drop counters so operators can see both readiness and error conditions in a
   single log entry.【F:Video/NdiVideoPipeline.cs†L283-L307】

## Warm-up, underruns, and latency
The buffer introduces an intentional delay: with a depth of three frames at
60 fps, the paced output will start roughly 50 ms after capture and continue to
run about three frames behind Chromium. If the queue empties, the pipeline will
repeat the previous frame once, mark an underrun, and then pause transmissions
until the backlog reaches the configured depth again. This behaviour keeps the
output cadence stable at the cost of reintroducing the warm-up latency after
underruns.【F:Video/NdiVideoPipeline.cs†L123-L339】

## Implications
* The paced mode now holds a predictable backlog, so repeated frames only occur
  when producers actually fall behind.
* Operators should size `BufferDepth` with the expected latency in mind—the
  steady-state delay is `BufferDepth / fps` seconds.
* Overflow and stale-drop counters remain available, but telemetry now exposes
  readiness (`primed`) and recovery time (`warmupMs`) to make diagnosing
  underruns easier.【F:Video/NdiVideoPipeline.cs†L283-L339】
