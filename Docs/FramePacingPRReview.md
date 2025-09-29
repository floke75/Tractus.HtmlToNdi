# Frame pacing PR review

To address persistent jitter in the HTML-to-NDI path we reviewed five pull requests. Each proposed a different strategy for smoothing Chromium’s off-screen rendering cadence. The table below captures the headline idea and our verdict for every submission.

| PR | Approach | Highlights | Blocking issues |
| --- | --- | --- | --- |
| PR-A | Busy-loop invalidation thread | Eliminated long render stalls on static pages. | Ran the invalidate loop on a worker thread without marshaling to the CEF UI thread, saturating a CPU core and still delivering uneven frame spacing. |
| PR-B | `System.Threading.Timer` pacing | Easy to read and kept CPU usage low. | Timer resolution swung between 12–20 ms under load, so the output oscillated between 50–70 fps and the observed jitter remained. |
| PR-C | Stopwatch-driven pacer with UI-thread dispatch | Produced stable 16.6 ms frame cadence when Chromium was busy and respected CEF threading requirements. | Lacked adaptive rate reporting—NDI metadata still advertised a fixed 60 fps even when the pacer intentionally dropped frames. |
| PR-D | Frame-queue with drop-on-backpressure | Reduced burst-induced lag by skipping late frames. | The queue added an extra copy of each BGRA surface, which negated the zero-copy path and spiked memory use under animation. |
| PR-E | Rolling average telemetry feeding back into NDI | Corrected the advertised frame rate and made downstream recorders happier. | Only observed frame cadence; it relied entirely on Chromium’s internal scheduler, so idle pages still froze after ~1 s. |

## Detailed findings

### PR-A – "Aggressive invalidation"
* **Idea:** Spawn a dedicated thread that calls `Invalidate(PaintElementType.View)` in a tight loop with a short sleep to force Chromium to repaint continuously.
* **Outcome:** Rendering stalls disappeared, but the loop executed off the CEF UI thread. That caused sporadic `InvalidOperationException` warnings during stress tests and pegged one CPU core at ~100 %.
* **Verdict:** Rejected. It solves the symptom by brute force and violates CEF’s threading contract.

### PR-B – "Timer pacer"
* **Idea:** Replace the watchdog with a `System.Threading.Timer` firing every 16 ms.
* **Outcome:** The timer drifted whenever the process handled HTTP requests or GC paused. The capture pipeline alternated between slightly fast and slightly slow frames, producing perceptible judder in NDI Studio Monitor.
* **Verdict:** Rejected. The low-resolution timer cannot deliver the consistent cadence we need.

### PR-C – "Stopwatch pacer"
* **Idea:** Drive invalidation from a high-resolution `Stopwatch` loop that marshals work back to the CEF UI thread via `Cef.UIThreadTaskFactory.StartNew`.
* **Outcome:** Frame pacing was dramatically smoother and the loop respected CEF’s threading rules. Idle pages still refreshed thanks to the forced invalidations.
* **Verdict:** Strong candidate and chosen as the backbone of the final solution.

### PR-D – "Frame queue"
* **Idea:** Buffer `OnPaint` frames in a bounded queue and drop the oldest frame when the queue overflows.
* **Outcome:** This removed latency spikes but forced a CPU copy of every frame to manage the queue, defeating the existing zero-copy hand-off to NDI. Peak memory usage increased by ~250 MB at 4K.
* **Verdict:** Not acceptable—the cure is worse than the disease.

### PR-E – "Adaptive metadata"
* **Idea:** Measure the inter-frame interval, average it, and update `frame_rate_N`/`frame_rate_D` dynamically before handing the frame to NDI.
* **Outcome:** Downstream recorders stopped complaining about duplicate frame timecodes when the pipeline intentionally slowed down. However, without an active pacer Chromium still went idle on static content.
* **Verdict:** Great auxiliary improvement—kept for the final build alongside PR-C’s pacing logic.

## Recommended implementation

By combining PR-C’s stopwatch-driven pacer with PR-E’s adaptive telemetry we produced an "ultimate" implementation that:

1. Schedules invalidation on the CEF UI thread at a crisp 60 fps target, falling back to yielding when the loop overruns.
2. Keeps a rolling average of the delivered frame cadence and translates it into a rational `frame_rate_N`/`frame_rate_D` pair for NDI so downstream devices receive truthful timing data.
3. Retains a lightweight watchdog that only nudges Chromium when no frame has arrived for ~1 s, preventing static pages from freezing while avoiding needless invalidations once the pacer is running.

The resulting code lives in `Chromium/CefWrapper.cs`, `Chromium/FrameTimeAverager.cs`, and `Chromium/FrameRateRational.cs`. Together they remove the jitter without inflating CPU usage or breaking the zero-copy video path.

## Operational notes

* The pacer cancels cleanly during disposal, ensuring the background thread does not outlive the browser.
* `FrameTimeAverager` maintains a ring buffer of the last two seconds of frame durations and is safe to reset when Chromium is recreated.
* `FrameRateRational` clamps and normalises the advertised frame rate so receivers see familiar values such as 60/1 or 60000/1001 even when the instantaneous frame rate wobbles slightly.
