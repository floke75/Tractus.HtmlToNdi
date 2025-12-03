# Cadence stability checklist

Use these settings when you need the sender to hold a rock-steady 30p cadence. They keep Chromium's paint loop aligned to 30 Hz, prevent it from racing ahead of the paced sender, and ensure the sender only pulls one capture per pacing slot.

## Command-line recipe

```
Tractus.HtmlToNdi.exe \
  --fps=30 \
  --windowless-frame-rate=30 \
  --pacing-mode=Latency \
  --enable-output-buffer \
  --buffer-depth=3 \
  --enable-paced-invalidation \
  --enable-capture-backpressure \
  --align-with-capture-timestamps \
  --enable-cadence-telemetry
```

## Launcher equivalents

* **Frame rate**: `30` (and set **Windowless frame rate override** to `30`).
* **Pacing mode**: `Latency`.
* **Enable output buffer**: on, depth `3`.
* **Paced invalidation**: on.
* **Capture backpressure**: on.
* **Align with capture timestamps**: on.
* **Cadence telemetry**: on (so you can verify cadence after warm-up).

## Rationale

* `--windowless-frame-rate=30` keeps Chromium's repaint scheduler from running faster than the paced sender, eliminating the ~0.2–0.8% overshoot seen when the renderer is allowed to free-run at a higher rate.
* The paced buffer with **paced invalidation** and **capture backpressure** guarantees at most one capture is requested per send slot and pauses capture whenever backlog is ahead, keeping output intervals tied to the high-resolution waitable timer rather than Chromium's own cadence.
* `Latency` mode with a shallow buffer (`--buffer-depth=3`) minimizes feedback adjustments so the pacer does not stretch/trim slots in response to deep-buffer drift.
* Keeping **align with capture timestamps** on lets the pacer nudge sub-millisecond phase error back toward zero without altering the advertised frame-rate fraction (which is always locked to the configured 30/1).
* Leaving **cadence telemetry** on lets you confirm stability after the 30-second warm-up window (look for `captureCadenceFps` ≈ `30.00`, `captureCadenceShortfallPercent=0`, and `repeated=0`).

## Avoid these when chasing exact 30p

* The **High performance** preset (or the individual Chromium flags `--disable-gpu-vsync` / `--disable-frame-rate-limit`) lets Chromium paint slightly faster than 30 Hz; the paced sender still outputs exactly 30/1, but capture cadence telemetry will show ~30.2 fps and NDI receivers may see microscopic phase drift when frames arrive early.
* **Smoothness** pacing mode deliberately runs a deep buffer with aggressive capture to protect motion continuity; it will re-shape the deadline schedule and is not intended for locked-cadence delivery.
