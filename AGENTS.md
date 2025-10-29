# AGENTS.md — Project briefing for Tractus.HtmlToNdi

> Repository: [`tractusevents/Tractus.HtmlToNdi`](https://github.com/tractusevents/Tractus.HtmlToNdi)  
> Purpose: Windows-only utility that renders a Chromium page off-screen and publishes video/audio plus limited KVM over the NDI protocol, with a minimal HTTP control API.

This document is the ground-truth orientation guide. Treat it as a living spec—update it whenever behaviour changes.

For the current paced-buffer design direction and recommendations, review `Docs/paced-buffer-improvement-plan.md` alongside the historical evaluation in `Docs/paced-buffer-pr-evaluation.md`.

---

## 1. What the current build actually does (net8.0, December 2024 snapshot)

* Entry point: `Program.Main` (`Program.cs`). It sets the working directory, initializes logging via `AppManagement.Initialize`, parses CLI flags (including `--fps`, buffering, and telemetry settings), allocates the NDI sender up front, constructs an `NdiVideoPipeline`, then starts CefSharp OffScreen inside a dedicated synchronization context. After the browser bootstraps the app spins up the ASP.NET Core minimal API.
* Chromium lifecycle: `AsyncContext.Run` + `SingleThreadSynchronizationContext` keep CefSharp happy on one STA-like thread. `CefWrapper.InitializeWrapperAsync` waits for the first page load, unmutes audio (CEF starts muted), subscribes to the `Paint` event, and starts a `FramePump` that invalidates Chromium at the requested cadence while a watchdog keeps the UI thread alive.
* Video path: `ChromiumWebBrowser.Paint` forwards frames to the `NdiVideoPipeline`. In zero-copy mode the pipeline sends the GPU buffer directly; when buffering is enabled it copies into a pooled ring buffer, waits until `BufferDepth` frames are queued, then drains the backlog FIFO while repeating the latest frame on underruns so cadence stays constant. A pacing-aware scheduler requests Chromium invalidations one send slot at a time, supports cadence adaptation, and can pause capture altogether while the paced buffer sheds backlog. Frame-rate metadata is advertised using either the configured target cadence or the measured average.
* Audio path: `CustomAudioHandler` exposes Cef audio, allocates a float buffer sized for one second, copies each planar channel into contiguous blocks inside that buffer, and sends it with `NDIlib.send_send_audio_v2`. (Note: the code claims “interleaved” but still stores channels sequentially; downstream receivers must cope with planar-like layout.)
* Control plane: ASP.NET Core minimal API listens on HTTP (no TLS, no auth). Swagger UI is enabled. All endpoints directly call methods on the static `Program.browserWrapper` instance.
* KVM metadata: the app advertises `<ndi_capabilities ntk_kvm="true" />` and starts a background thread that polls `NDIlib.send_capture` every second. It interprets `<ndi_kvm ...>` metadata frames, caching normalized mouse coordinates on opcode `0x03` and triggering a left click on opcode `0x04`.
* Shutdown: after `app.Run()` returns, the metadata thread is stopped, the browser wrapper is disposed, and the temporary Cef cache (`cache/<guid>`) is deleted.

---

## 2. Command-line contract & configuration defaults

`Program.Main` recognises the following switches (case-sensitive, leading double-hyphen required):

| Flag | Example | Behaviour / Default |
| --- | --- | --- |
| `--ndiname=<value>` | `--ndiname="Studio Browser"` | Sets the NDI source name. If omitted, the app prompts on STDIN until the user types a non-empty name. Initial default is "HTML5" before prompting. |
| `--port=<int>` | `--port=9999` | Sets the HTTP listener port. If omitted, prompts interactively until a valid integer is entered. |
| `--url=<https://...>` | `--url=https://testpattern.tractusevents.com/` | Sets the startup page. Defaults to `https://testpattern.tractusevents.com/`. |
| `--w=<int>` | `--w=1920` | Sets browser width in pixels. Defaults to 1920. |
| `--h=<int>` | `--h=1080` | Sets browser height in pixels. Defaults to 1080. |
| `--fps=<double|fraction>` | `--fps=59.94` | Target NDI frame cadence. Accepts decimal or rational values (e.g. `60000/1001`). Defaults to 60 fps. |
| `--buffer-depth=<int>` | `--buffer-depth=3` | Enables the paced output buffer with the specified capacity. When enabled the sender waits for `depth` frames before transmitting, adding roughly `depth / fps` seconds of intentional latency. `0` keeps the legacy zero-copy mode. |
| `--enable-output-buffer` | `--enable-output-buffer` | Convenience flag to enable paced buffering with the default depth (3 frames, ≈`3 / fps` seconds of latency once primed). |
| `--allow-latency-expansion` | `--allow-latency-expansion` | Lets the paced buffer keep draining any queued frames before repeating the last send after an underrun. This smooths recovery at the cost of temporary extra latency. |
| `--disable-capture-alignment` | `--disable-capture-alignment` | Turns off capture timestamp alignment when pacing is enabled. Use `--align-with-capture-timestamps` to explicitly re-enable it. |
| `--disable-cadence-telemetry` | `--disable-cadence-telemetry` | Suppresses cadence jitter telemetry in logs. Use `--enable-cadence-telemetry` to force-enable it. |
| `--enable-paced-invalidation` / `--disable-paced-invalidation` | `--enable-paced-invalidation` | Couples Chromium invalidation with the paced sender. Each send slot triggers at most one capture, even when the ring buffer is disabled. |
| `--enable-capture-backpressure` / `--disable-capture-backpressure` | `--enable-capture-backpressure` | Pauses Chromium invalidation while the paced buffer sits above its high-water mark. Requires paced invalidation; when pacing is disabled the backpressure flag is ignored. |
| `--enable-pump-cadence-adaptation` / `--disable-pump-cadence-adaptation` | `--enable-pump-cadence-adaptation` | Allows the pacing scheduler to stretch or delay invalidations based on capture/output drift telemetry. |
| `--telemetry-interval=<seconds>` | `--telemetry-interval=10` | Seconds between video pipeline telemetry log entries. Defaults to 10. |
| `--windowless-frame-rate=<double>` | `--windowless-frame-rate=60` | Overrides Chromium's internal repaint cadence. Defaults to the rounded value of `--fps`. |
| `--disable-gpu-vsync` | `--disable-gpu-vsync` | Passes `--disable-gpu-vsync` to Chromium to remove GPU vsync throttling. |
| `--disable-frame-rate-limit` | `--disable-frame-rate-limit` | Passes `--disable-frame-rate-limit` to Chromium for maximum redraw throughput. |
| `-debug` | `-debug` | Raises Serilog minimum level to `Debug`. |
| `-quiet` | `-quiet` | Disables console logging (file logging remains). |

Other configuration surfaces:

* Logging: Serilog writes to console (unless `-quiet`) and to `%USERPROFILE%/Documents/<AppName>_log.txt`. `AppManagement.LoggingLevel` is globally accessible.
* Build target: `Tractus.HtmlToNdi.csproj` targets **.NET 8.0**, `AllowUnsafeBlocks=true`, and forces `PlatformTarget=x64`. Do not assume .NET 6/7.
* The app project excludes everything under `Tests/` from its default `Compile/None/Content` globs so solution builds do not accidentally compile the xUnit sources or their generated `obj` artifacts. Keep test-only assets inside `Tests/` (or a sibling directory) to preserve this separation.
* Assets copied at runtime: `HtmlToNdi.ico` and `HtmlToNdi.png` are always copied to the output directory.

---

## 3. Repository map (authoritative)

```
/Chromium/
  AsyncContext.cs                     # Single-threaded async pump for CefSharp startup
  CefWrapper.cs                       # Owns ChromiumWebBrowser instance, paint-to-NDI bridge, HTTP input helpers
  CustomAudioHandler.cs               # IAudioHandler implementation, planar float → contiguous buffer → NDI audio
  SingleThreadSynchronizationContext.cs # BlockingCollection-backed synchronization context
/Video/
  CapturedFrame.cs                    # Lightweight struct describing pixels passed from Chromium to the pipeline
  FramePump.cs                        # Periodic Chromium invalidator with watchdog
  FrameRate.cs                        # Frame-rate parsing helpers (decimal/fraction) and metadata conversion
  FrameRingBuffer.cs                  # Drop-oldest ring buffer used by the paced pipeline
  FrameTimeAverager.cs                # Sliding-window FPS estimator for telemetry metadata
  INdiVideoSender.cs                  # Abstraction for sending frames to NDI (native + test doubles)
  NdiVideoFrame.cs                    # Unmanaged frame copies used by the paced buffer
  NdiVideoPipeline.cs                 # Direct + buffered NDI pipeline with telemetry
  NdiVideoPipelineOptions.cs          # Options for configuring buffering/telemetry
/Models/
  GoToUrlModel.cs                     # DTOs for `/seturl` and `/keystroke`
AppManagement.cs                      # Logging bootstrap, per-app data helpers, CLI flags (-debug/-quiet)
Program.cs                            # Main: CLI parsing, Cef initialization, HTTP API, NDI sender, KVM thread
Tractus.HtmlToNdi.csproj              # net8.0 exe, package references (CefSharp OffScreen, Serilog, Swashbuckle, NDILib)
Tests/Tractus.HtmlToNdi.Tests/        # xUnit test project covering frame pacing primitives
Tractus.HtmlToNdi.http                # Sample HTTP requests for manual testing (update alongside API changes)
README.md                             # End-user documentation (currently missing some routes—keep in sync when editing)
```

There are no nested `AGENTS.md` files; this document covers the entire repository.

---

## 4. Execution flow in detail

```
Main
 ├─ AppManagement.Initialize(args)
 │    ├─ Ensure data directory exists (base directory)
 │    ├─ Hook AppDomain.UnhandledException for Serilog logging
 │    ├─ Configure Serilog sinks (console + Documents/<AppName>_log.txt)
 │    └─ Respect -debug / -quiet flags
 ├─ Parse CLI flags (see §2) for dimensions, URL, fps, buffering and telemetry
 ├─ Create NDI sender (NDIlib.send_create) and wrap it with `NdiVideoPipeline`
 ├─ AsyncContext.Run(async)
 │    ├─ Configure CefSettings (RootCachePath=cache/<guid>, autoplay override, fps/vsync flags, EnableAudio)
 │    ├─ Cef.Initialize(settings)
 │    └─ Instantiate CefWrapper(width, height, url, pipeline) and await InitializeWrapperAsync()
 ├─ Build WebApplication (Serilog integration, Swagger, authorization middleware added but unused)
 ├─ Advertise `<ndi_capabilities ntk_kvm="true" />`
 │    └─ Launch background thread polling NDI metadata (1 s timeout)
 ├─ Map HTTP routes directly to CefWrapper methods
 ├─ app.Run()   # blocks until shutdown
 ├─ On shutdown: stop metadata thread, dispose CefWrapper
 └─ Delete temporary Cef cache directory (best-effort)
```

Global state (`Program.NdiSenderPtr`, `Program.browserWrapper`) means only **one** browser/NDI sender per process. Changes that introduce additional instances must restructure the program.

---

## 5. HTTP API surface (current, unauthenticated)

All routes live in `Program.cs` and act on the singleton `browserWrapper`.

| Route | Method | Payload | Effect |
| --- | --- | --- | --- |
| `/seturl` | POST | JSON `{ "url": "https://..." }` (`GoToUrlModel`) | Calls `CefWrapper.SetUrl`, immediately loading the page. |
| `/scroll/{increment}` | GET | Path `increment` (int) | Sends `SendMouseWheelEvent(0,0,0,increment)` – positive values scroll down. Origin is always `(0,0)`; no viewport-relative targeting. |
| `/click/{x}/{y}` | GET | Path `x`, `y` (int pixels) | Sends a left mouse click (down, 100 ms sleep, up) at the specified coordinates. No bounds checking. |
| `/keystroke` | POST | JSON `{ "toSend": "..." }` (`SendKeystrokeModel`) | Iterates characters, firing `KeyDown` events for each Unicode code point (no key-up, modifiers, or special keys). |
| `/type/{toType}` | GET | Path string | Convenience wrapper around `/keystroke` (same limitations). |
| `/refresh` | GET | none | Calls `ChromiumWebBrowser.Reload()`. |

Swagger/OpenAPI UI is exposed at `/swagger`. There is no authentication, encryption, or rate limiting; never expose this service to untrusted networks without an upstream proxy.

When adding routes, update **both** this table and `Tractus.HtmlToNdi.http` samples.

---

## 6. Subsystem specifics & quirks

### CefWrapper (`Chromium/CefWrapper.cs`)
* `ChromiumWebBrowser` is constructed with `AudioHandler = new CustomAudioHandler()` and a fixed `System.Drawing.Size(width,height)`.
* A pacing-aware `FramePump` invalidates Chromium on the cadence derived from `--fps` (or `--windowless-frame-rate`). When paced invalidation is enabled the pump switches to on-demand mode and waits for `NdiVideoPipeline` to request each capture slot; otherwise it free-runs on the configured interval. A watchdog issues recovery invalidations if paints stall.
* `ScrollBy` always uses `(x=0,y=0)` as the mouse location; complex scrolling (e.g., inside scrolled divs) may require additional API work.
* `Click` only supports the left mouse button; drag, double-click, or right-click interactions are not implemented.
* `SendKeystrokes` issues **only** `KeyDown` events with `NativeKeyCode=Convert.ToInt32(char)`. There is no key-up, modifiers, or IME support—uppercase letters require the page to handle them despite missing Shift state.
* `Dispose` detaches the `Paint` handler, disposes the browser, tears down the `FramePump`, and releases the `NdiVideoPipeline`.

### CustomAudioHandler (`Chromium/CustomAudioHandler.cs`)
* `ChannelLayoutToChannelCount` covers most layouts but returns 0 for unsupported ones, causing `GetAudioParameters` to fail (muting audio).
* `OnAudioStreamPacket` copies planar floats into a pre-allocated buffer with each channel occupying a contiguous block (pseudo-planar). `channel_stride_in_bytes` is set to a single channel’s byte count; ensure downstream consumers accept that layout.
* Memory is manually allocated/freed via `Marshal.AllocHGlobal` / `FreeHGlobal`. Failing to call `Dispose` will leak unmanaged memory.

### NDI integration (`Program.cs`)
* `EnsureNdiNativeLibraryLoaded` scans common NDI install paths (for example, `C:\Program Files\NDI\NDI 6 Runtime\v6` and `...\NDI 6 SDK\Bin\x64`) plus overrides such as `NDILIB_REDIST_FOLDER`, then wires a `DllImportResolver` so the managed wrapper can locate `Processing.NDI.Lib.*`. If the runtime is missing, the app surfaces a descriptive error that lists every path it probed.
* `Program.NdiSenderPtr` is created before Chromium bootstraps so the `NdiVideoPipeline` can send immediately; there is currently **no** call to `NDIlib.send_destroy`. Adding explicit teardown requires guarding against `nint.Zero` in paint/audio handlers.
* Metadata loop logs every metadata frame at `Warning` level (`Log.Logger.Warning("Got metadata: ...")`), which can flood logs if receivers send frequent updates.
* Only opcodes `0x03` (mouse move) and `0x04` (left click) are handled; `0x07` (mouse up) is ignored intentionally. There is no translation for scroll, keyboard, or multi-button events.

### Frame buffering and pacing (`Video/NdiVideoPipeline.cs`)
* `FrameRingBuffer<T>` drops the oldest frame when capacity is exceeded and surfaces it via the `out` parameter. That action increments `DroppedFromOverflow` but does **not** dispose the frame; the caller must do so.
* `NdiVideoPipeline` now owns a pacing scheduler that throttles Chromium, measures drift, and optionally pauses capture altogether when backlog exceeds the configured depth. In direct-send mode the scheduler still requests the next capture immediately after each transmission so Chromium cannot outrun the paced cadence. Backpressure counters (`captureGatePauses` / `captureGateResumes`) reset alongside the pacing state machine to keep telemetry accurate.
* `TryDequeue` returns the oldest frame without disturbing the rest of the queue so the paced sender can preserve FIFO ordering. `DequeueLatest` remains available for scenarios that only care about the newest frame; it disposes older entries before returning the most recent one and avoids double-counting stale drops when overflow was already recorded.

### Logging & diagnostics (`AppManagement.cs`)
* `AppManagement.InstanceName` composes `<os>_<arch>_<machinename>` for telemetry or metadata (not currently used elsewhere).
* On fatal `AppDomain` exceptions, details are logged but the process is not explicitly terminated beyond .NET’s default behaviour.

---

## 7. Known limitations (validated against source)

1. **Single-instance design**: Global static `browserWrapper`/`NdiSenderPtr` make multiple concurrent browsers impossible without a refactor.
2. **Authentication void**: HTTP API is wide open. Use reverse proxies or add middleware before shipping to production.
3. **Input fidelity gaps**: No key-up events, modifiers, IME, or right/middle mouse buttons. `/scroll` ignores pointer position. Drag-and-drop is unsupported.
4. **Codec constraints**: Standard Chromium build without proprietary codecs—DRM/H.264/YouTube may fail.
5. **Audio layout**: Output buffer is not truly interleaved despite metadata suggesting so, which may confuse strict NDI consumers.
6. **Resource cleanup**: NDI sender and Cef global state are not explicitly disposed/destroyed; rely on process exit. Abrupt termination can leave cache folders under `cache/`.
7. **Logging noise**: All incoming KVM metadata is logged at warning level, which may clutter logs under active control.

---

## 8. Extension wish-list (ordered by likely impact)

1. **Secure the HTTP surface** – add authentication, optional TLS, and better error responses. Consider ASP.NET minimal API filters or reverse proxy guidance.
2. **Runtime configuration endpoints** – `/size`, `/fps`, `/audio` toggles that safely recreate the Chromium instance and reconfigure NDI frames.
3. **Improved input/KVM parity** – handle drag (distinct down/up), right-click, mouse move via HTTP, keyboard modifiers, and translate additional NDI KVM opcodes.
4. **Audio correctness** – produce genuinely interleaved buffers (or update metadata) and expose sample-rate/channel status via diagnostics.
5. **Observability** – add `/stats` endpoint exposing render cadence, last paint timestamp, audio packet counts, and cache path.
6. **Graceful shutdown** – explicitly destroy the NDI sender, call `Cef.Shutdown()`, and ensure watchdog thread stops before disposing.

---

## 9. Validation checklist (run manually after significant changes)

* ✅ Verify transparency by loading `https://testpattern.tractusevents.com/` and checking alpha in an NDI receiver.
* ✅ Stress-test animation (e.g., WebGL or CSS animation) and confirm frame cadence ~16.6 ms (60 fps) without dropped frames.
* ✅ Play an audio source with known stereo content and ensure both channels arrive with correct timing.
* ✅ Exercise every HTTP route via `Tractus.HtmlToNdi.http` or Swagger (set URL, scroll, click, type, refresh) and watch logs for errors.
* ✅ Confirm KVM metadata click-from-receiver works (e.g., NewTek Studio Monitor sending mouse move + click).
* ✅ Inspect `%USERPROFILE%/Documents/<AppName>_log.txt` for warnings/errors after a session.

---

## 10. Pointers for future contributors

* Stick to the single-instance assumption unless refactoring `Program` comprehensively; many helpers assume globals.
* Respect the CefSharp threading rules—initialization must continue to use `AsyncContext`/`SingleThreadSynchronizationContext`.
* When touching HTTP routes, update Swagger annotations (minimal API `.WithOpenApi()`) and documentation (`README.md`, `Tractus.HtmlToNdi.http`, and this file).
* If modifying audio/video frame structures, review `CustomAudioHandler` and `CefWrapper.OnBrowserPaint` together to keep NDI metadata consistent.
* Before merging, ensure Serilog messages remain informative (prefer structured logging over string concatenation where practical).

---

_Last reviewed against repository state in this workspace. Update sections promptly when behaviour changes._
