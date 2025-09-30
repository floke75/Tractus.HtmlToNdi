# AGENTS.md — Project briefing for Tractus.HtmlToNdi

> Repository: [`tractusevents/Tractus.HtmlToNdi`](https://github.com/tractusevents/Tractus.HtmlToNdi)
> Purpose: Windows-only utility that renders a Chromium page off-screen and publishes video/audio plus limited KVM over the NDI
protocol, with a minimal HTTP control API.

This document is the ground-truth orientation guide. Treat it as a living spec—update it whenever behaviour changes.

---

## 1. What the current build actually does (net8.0, December 2024 snapshot)

* Entry point: `Program.Main` (`Program.cs`). It sets the working directory, initializes logging via `AppManagement.Initialize`,
  parses CLI flags, starts CefSharp OffScreen inside a dedicated synchronization context, creates the singleton `CefWrapper`, spins up the ASP.NET Core minimal API, and allocates a single NDI sender.
* Chromium lifecycle: `AsyncContext.Run` + `SingleThreadSynchronizationContext` keep CefSharp happy on one STA-like thread. `CefWrapper.InitializeWrapperAsync` waits for the first page load, configures Chromium's windowless frame rate, unmutes audio (CEF starts muted), subscribes to the `Paint` event, starts a watchdog thread that invalidates the view if Chromium goes silent for ≥1 s, and then launches the video pacing thread.
* Video path: each `ChromiumWebBrowser.Paint` callback copies the BGRA pixels out of Chromium's GPU buffer into a managed `BrowserFrame` stored in a single-producer/single-consumer ring buffer. A dedicated pacing thread wakes at the configured cadence (default 29.97 fps, `30000/1001`), pulls the newest frame, drops stale ones, and calls `NDIlib.send_send_video_v2`. When Chromium stalls, the pacer repeats the most recent frame to keep the clock steady.
* Audio path: `CustomAudioHandler` exposes Cef audio, allocates a float buffer sized for one second, copies each planar channel into contiguous blocks inside that buffer, and sends it with `NDIlib.send_send_audio_v2`. (Note: the code claims “interleaved” but still stores channels sequentially; downstream receivers must cope with planar-like layout.)
* Control plane: ASP.NET Core minimal API listens on HTTP (no TLS, no auth). Swagger UI is enabled. All endpoints directly call methods on the static `Program.browserWrapper` instance.
* KVM metadata: the app advertises `<ndi_capabilities ntk_kvm="true" />` and starts a background thread that polls `NDIlib.send_capture` every second. It interprets `<ndi_kvm ...>` metadata frames, caching normalized mouse coordinates on opcode `0x03` and triggering a left click on opcode `0x04`.
* Shutdown: after `app.Run()` returns, the metadata thread is stopped, the browser wrapper is disposed (which stops the pacing thread and tears down the ring buffer), and the temporary Cef cache (`cache/<guid>`) is deleted.

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
| `--fps=<value>` | `--fps=29.97` | Sets the paced NDI output frame rate. Accepts floating-point values (e.g., `29.97`) or ratios (e.g., `30000/1001`). Defaults to 29.97 fps. |
| `--buffer-depth=<int>` | `--buffer-depth=5` | Sets the number of frames stored in the pacing ring buffer before overwriting. Defaults to 5. |
| `--disable-vsync` | `--disable-vsync` | Adds Chromium's `--disable-gpu-vsync` flag when launching CEF. |
| `--disable-frame-rate-limit` | `--disable-frame-rate-limit` | Adds Chromium's `--disable-frame-rate-limit` flag when launching CEF. |
| `-debug` | `-debug` | Raises Serilog minimum level to `Debug`. |
| `-quiet` | `-quiet` | Disables console logging (file logging remains). |

Other configuration surfaces:

* Logging: Serilog writes to console (unless `-quiet`) and to `%USERPROFILE%/Documents/<AppName>_log.txt`. `AppManagement.LoggingLevel` is globally accessible.
* Build target: `Tractus.HtmlToNdi.csproj` targets **.NET 8.0**, `AllowUnsafeBlocks=true`, and forces `PlatformTarget=x64`. Do not assume .NET 6/7.
* Assets copied at runtime: `HtmlToNdi.ico` and `HtmlToNdi.png` are always copied to the output directory.

---

## 3. Repository map (authoritative)

```
/Chromium/
  AsyncContext.cs                     # Single-threaded async pump for CefSharp startup
  CefWrapper.cs                       # Owns ChromiumWebBrowser instance, paint-to-NDI bridge, HTTP input helpers
  CustomAudioHandler.cs               # IAudioHandler implementation, planar float → contiguous buffer → NDI audio
  SingleThreadSynchronizationContext.cs # BlockingCollection-backed synchronization context
/FramePacing/
  BrowserFrame.cs                     # Immutable value type holding BGRA pixel buffers from Chromium
  FrameDeliveryContext.cs             # Metadata describing repeats, drops, latency, backlog
  FramePacer.cs                       # Worker thread that ticks at the target FPS
  FramePacerEngine.cs                 # Core pacing logic (interval tracking, metrics, send invocation)
  FramePacerMetrics.cs                # Snapshot used for shutdown logging/diagnostics
  FrameRate.cs                        # Parsing helpers for decimal and ratio FPS inputs
  FrameRingBuffer.cs                  # Single-producer/single-consumer ring buffer with overwrite semantics
/Models/
  GoToUrlModel.cs                     # DTOs for `/seturl` and `/keystroke`
AppManagement.cs                      # Logging bootstrap, per-app data helpers, CLI flags (-debug/-quiet)
Program.cs                            # Main: CLI parsing, Cef initialization, HTTP API, NDI sender, KVM thread
README.md                             # End-user documentation (keep in sync with API/CLI)
Tractus.HtmlToNdi.csproj              # net8.0 exe, package references (CefSharp OffScreen, Serilog, Swashbuckle, NDILib)
Tractus.HtmlToNdi.http                # Sample HTTP requests for manual testing (update alongside API changes)
/Tests/Tractus.HtmlToNdi.Tests/
  FramePacing/FramePacerEngineTests.cs # Unit tests for ring buffer + pacing engine
  Tractus.HtmlToNdi.Tests.csproj       # xUnit test project (net8.0)
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
 ├─ Prompt/parse CLI flags (see §2)
 ├─ Create BrowserFrame ring buffer + FramePacer according to CLI options
 ├─ AsyncContext.Run(async)
 │    ├─ Configure CefSettings (RootCachePath=cache/<guid>, autoplay-policy override, optional vsync/fps flags)
 │    ├─ Cef.Initialize(settings)
 │    └─ Instantiate CefWrapper(width, height, url, pacing options) and await InitializeWrapperAsync()
 ├─ Build WebApplication (Serilog integration, Swagger, authorization middleware added but unused)
 ├─ Create NDI sender (NDIlib.send_create)
 │    ├─ Advertise `<ndi_capabilities ntk_kvm="true" />`
 │    └─ Launch background thread polling NDI metadata (1 s timeout)
 ├─ Map HTTP routes directly to CefWrapper methods
 ├─ app.Run()   # blocks until shutdown while FramePacer thread keeps emitting video
 ├─ On shutdown: stop metadata thread, dispose CefWrapper (which stops the pacer + ring buffer)
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
* `RenderWatchdog` thread invalidates the view once per second if no `Paint` events arrive, preventing NDI receivers from freezing on static pages.
* `OnBrowserPaint` copies the BGRA surface into a managed buffer, pushes it into the ring buffer, and immediately returns to Chromium; the pacing thread performs all NDI sends.
* `ScrollBy` always uses `(x=0,y=0)` as the mouse location; complex scrolling (e.g., inside scrolled divs) may require additional API work.
* `Click` only supports the left mouse button; drag, double-click, or right-click interactions are not implemented.
* `SendKeystrokes` issues **only** `KeyDown` events with `NativeKeyCode=Convert.ToInt32(char)`. There is no key-up, modifiers, or IME support—uppercase letters require the page to handle them despite missing Shift state.
* `Dispose` detaches the `Paint` handler, stops the pacer, disposes the ring buffer, and then disposes the browser.

### Frame pacing (`FramePacing/*`)
* `FrameRingBuffer<T>` implements a lock-free single-producer/single-consumer buffer with overwrite semantics.
* `FramePacerEngine` tracks render cadence, backlog, and latency; it repeats frames when Chromium is quiet and reports drop counts when bursts occur.
* `FramePacer.GetMetricsSnapshot()` returns aggregated statistics logged once at shutdown.

### CustomAudioHandler (`Chromium/CustomAudioHandler.cs`)
* `ChannelLayoutToChannelCount` covers most layouts but returns 0 for unsupported ones, causing `GetAudioParameters` to fail (muting audio).
* `OnAudioStreamPacket` copies planar floats into a pre-allocated buffer with each channel occupying a contiguous block (pseudo-planar). `channel_stride_in_bytes` is set to a single channel’s byte count; ensure downstream consumers accept that layout.
* Memory is manually allocated/freed via `Marshal.AllocHGlobal` / `FreeHGlobal`. Failing to call `Dispose` will leak unmanaged memory.

### NDI integration (`Program.cs`)
* `Program.NdiSenderPtr` must remain valid for the lifetime of the process; there is currently **no** call to `NDIlib.send_destroy`. Adding explicit teardown requires guarding against `nint.Zero` in pacing callbacks and audio handlers.
* Metadata loop logs every metadata frame at `Warning` level (`Log.Logger.Warning("Got metadata: ...")`), which can flood logs if receivers send frequent updates.
* Only opcodes `0x03` (mouse move) and `0x04` (left click) are handled; `0x07` (mouse up) is ignored intentionally. There is no translation for scroll, keyboard, or multi-button events.

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
6. **CPU copies for video**: Frame pacing requires copying Chromium buffers into managed memory each paint; high resolutions can incur GC pressure.
7. **Resource cleanup**: NDI sender and Cef global state are not explicitly destroyed; rely on process exit. Abrupt termination can leave cache folders under `cache/`.
8. **Logging noise**: All incoming KVM metadata is logged at warning level, which may clutter logs under active control.

---

## 8. Extension wish-list (ordered by likely impact)

1. **Secure the HTTP surface** – add authentication, optional TLS, and better error responses. Consider ASP.NET minimal API filters or reverse proxy guidance.
2. **Runtime configuration endpoints** – `/size`, `/fps`, `/audio` toggles that safely recreate the Chromium instance and reconfigure NDI frames.
3. **Improved input/KVM parity** – handle drag (distinct down/up), right-click, mouse move via HTTP, keyboard modifiers, and translate additional NDI KVM opcodes.
4. **Audio correctness** – produce genuinely interleaved buffers (or update metadata) and expose sample-rate/channel status via diagnostics.
5. **Observability** – add `/stats` endpoint exposing render cadence, last paint timestamp, audio packet counts, cache path, and pacing metrics.
6. **Graceful shutdown** – explicitly destroy the NDI sender, call `Cef.Shutdown()`, and ensure watchdog & pacing threads stop before disposing.

---

## 9. Validation checklist (run manually after significant changes)

* ✅ Verify transparency by loading `https://testpattern.tractusevents.com/` and checking alpha in an NDI receiver.
* ✅ Stress-test animation (e.g., WebGL or CSS animation) and confirm paced NDI cadence matches the configured rate (29.97 fps by default) without sustained dropped frames.
* ✅ Play an audio source with known stereo content and ensure both channels arrive with correct timing.
* ✅ Exercise every HTTP route via `Tractus.HtmlToNdi.http` or Swagger (set URL, scroll, click, type, refresh) and watch logs for errors.
* ✅ Confirm KVM metadata click-from-receiver works (e.g., NewTek Studio Monitor sending mouse move + click).
* ✅ Inspect `%USERPROFILE%/Documents/<AppName>_log.txt` for warnings/errors after a session.

---

## 10. Pointers for future contributors

* Stick to the single-instance assumption unless refactoring `Program` comprehensively; many helpers assume globals.
* Respect the CefSharp threading rules—initialization must continue to use `AsyncContext`/`SingleThreadSynchronizationContext`.
* When touching HTTP routes, update Swagger annotations (minimal API `.WithOpenApi()`), documentation (`README.md`, `Tractus.HtmlToNdi.http`), and this file.
* If modifying audio/video frame structures, review `CustomAudioHandler`, the frame ring buffer, and the pacer together to keep NDI metadata consistent.
* Before merging, ensure Serilog messages remain informative (prefer structured logging over string concatenation where practical).

---

_Last reviewed against repository state in this workspace. Update sections promptly when behaviour changes._
