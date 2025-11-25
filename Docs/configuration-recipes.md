# Configuration Recipes

This guide provides recommended setting combinations ("recipes") for common use cases. Use these as a starting point and tune based on your specific hardware and network environment.

## 1. Low Latency / Interactive (KVM)
**Goal:** Minimal delay between mouse/keyboard input and NDI output. Best for remote control or real-time dashboards.

*   **Buffering:** Disabled (`--buffer-depth=0`)
*   **Paced Invalidation:** Disabled
*   **Latency Expansion:** N/A
*   **Cadence Adaptation:** Disabled

**Command Line:**
```bash
--buffer-depth=0 --disable-paced-invalidation
```

**Notes:**
*   Frames are sent immediately upon capture.
*   Jitter is determined entirely by Chromium's render timing and system load.
*   Stutter may occur if the browser load spikes, but latency remains minimal.

---

## 2. Broadcast Standard (Smooth Motion)
**Goal:** Locked 60fps (or 59.94) output with no visual stutter. Best for tickers, lower thirds, and animated graphics.

*   **Buffering:** Enabled (`--buffer-depth=3`)
*   **Paced Invalidation:** Enabled (`--enable-paced-invalidation`)
*   **Cadence Adaptation:** Enabled (`--enable-pump-cadence-adaptation`)
*   **Latency Expansion:** Disabled (default)

**Command Line:**
```bash
--enable-output-buffer --buffer-depth=3 --enable-paced-invalidation --enable-pump-cadence-adaptation
```

**Notes:**
*   **Paced Invalidation** ensures Chromium renders exactly when the NDI sender needs a frame, preventing beat-frequency stutter.
*   **Cadence Adaptation** micro-adjusts the invalidation timing to keep the browser and NDI clocks aligned.
*   **Latency Expansion** is disabled to ensure that if a stall occurs, the output "jumps" to the latest frame immediately (maintaining strict sync with the live data).

---

## 3. High Resilience / Deep Buffer
**Goal:** Maximum smoothness even with unreliable rendering or network conditions. Best for signage, complex WebGL, or heavy DOM pages where latency (seconds) is acceptable.

*   **Buffering:** Deep (`--buffer-depth=60` to `300`+)
*   **Paced Invalidation:** Enabled
*   **Cadence Adaptation:** Enabled
*   **Latency Expansion:** Enabled (`--allow-latency-expansion`)

**Command Line:**
```bash
--buffer-depth=120 --enable-paced-invalidation --enable-pump-cadence-adaptation --allow-latency-expansion
```

**Notes:**
*   **Deep Buffer:** A depth of 120 frames (at 60fps) provides a 2-second safety margin.
*   **Latency Expansion:** Critical for this mode. If the buffer drains, this flag ensures the remaining frames play out smoothly instead of jumping/trimming. The pipeline will slowly rebuild the buffer over time rather than force-flushing it.
*   **Hysteresis:** The system automatically uses a 10% hysteresis window for large buffers to prevent rapid oscillation between filling and draining states.

---

## 4. Experimental / High Performance
**Goal:** Attempt zero-copy capture for reduced CPU usage. Experimental.

*   **Capture Mode:** Compositor (`--enable-compositor-capture`)
*   **Buffering:** Enabled (`--buffer-depth=3`)

**Command Line:**
```bash
--enable-compositor-capture --enable-output-buffer
```

**Notes:**
*   Bypasses the standard "paint" loop.
*   Requires compatible GPU drivers and may fall back if textures are not CPU-accessible.
*   Does not support `Paced Invalidation` or `Backpressure` as the compositor drives its own cadence.

---

## Summary Table

| Scenario | Buffer Depth | Paced Invalidation | Cadence Adaptation | Latency Expansion |
| :--- | :--- | :--- | :--- | :--- |
| **Interactive** | `0` | Off | Off | N/A |
| **Broadcast** | `3` | **On** | **On** | Off |
| **Resilience** | `60`+ | **On** | **On** | **On** |
| **Legacy/Simple** | `0` | Off | Off | N/A |
