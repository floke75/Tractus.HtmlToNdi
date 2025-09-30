# AGENTS.md — Project briefing for Tractus.HtmlToNdi

> Repository: [`tractusevents/Tractus.HtmlToNdi`](https://github.com/tractusevents/Tractus.HtmlToNdi)  
> Purpose: Windows-only utility that renders a Chromium page off-screen and publishes video/audio plus limited KVM over the NDI protocol, with a minimal HTTP control API.

This document is the ground-truth orientation guide. Treat it as a living spec—update it whenever behaviour changes.

---

## 1. What the current build actually does (net8.0, December 2024 snapshot)

* Entry point: `Program.Main` (`Program.cs`). It sets the working directory, initializes logging via `AppManagement.Initialize`, parses CLI flags, starts CefSharp OffScreen inside a dedicated synchronization context, creates the singleton `CefWrapper`, spins up the ASP.NET Core minimal API, and allocates a single NDI sender.
* Chromium lifecycle: `AsyncContext.Run` + `SingleThreadSynchronizationContext` keep CefSharp happy on one STA-like thread. `CefWrapper.InitializeWrapperAsync` waits for the first page load, locks the windowless frame rate to 60 fps, unmutes audio (CEF starts muted), subscribes to the `Paint` event, and starts a watchdog thread that invalidates the view if Chromium goes silent for ≥1 s.
* Video path: a `FramePump` invalidates Chromium at the CLI-selected cadence while `NdiVideoPipeline` either forwards paints directly to NDI (zero-copy) or, when buffering is enabled, copies frames into a pooled ring buffer that a paced task drains. The sender advertises the parsed rational frame rate (e.g., 60000/1001) and logs telemetry about drops/repeats while publishing a `<ndi_frame_metrics ... />` metadata element every 10 s.
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
| `--fps=<value>` | `--fps=60000/1001` | Target frame cadence for the NDI sender. Accepts decimals (`59.94`) or rationals (`60000/1001`). Defaults to 60/1. |
| `--buffer-depth=<int>` | `--buffer-depth=3` | Enables the paced output buffer with the specified depth in frames. `0` disables buffering. |
| `--enable-output-buffer` | `--enable-output-buffer` | Convenience switch that enables buffering with a default depth of three frames when `--buffer-depth` is omitted. |
| `--windowless-frame-rate=<int>` | `--windowless-frame-rate=60` | Overrides Chromium's internal windowless frame rate. Defaults to the rounded `--fps` value. |
| `--disable-vsync` | `--disable-vsync` | Adds `--disable-gpu-vsync` to the Chromium command line for environments that prefer uncapped rendering. |
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
/Video/
  BufferedVideoFrame.cs               # Memory-pooled frame copies for the buffered pipeline
  FramePump.cs                        # PeriodicTimer invalidator + watchdog for Chromium
  FrameRate.cs                        # Broadcast-friendly frame-rate parsing and rational mapping
  FrameRingBuffer.cs                  # Drop-oldest frame buffer with disposal semantics
  FrameTimeAverager.cs                # Moving-average FPS calculation for telemetry
  NdiVideoPipeline.cs                 # Direct and buffered NDI send paths plus telemetry metadata
/Models/
  GoToUrlModel.cs                     # DTOs for `/seturl` and `/keystroke`
AppManagement.cs                      # Logging bootstrap, per-app data helpers, CLI flags (-debug/-quiet)
Program.cs                            # Main: CLI parsing (incl. frame/buffer flags), Cef initialization, frame pump, HTTP API, NDI sender, KVM thread
Tractus.HtmlToNdi.csproj              # net8.0 exe, package references (CefSharp OffScreen, Serilog, Swashbuckle, NDILib)
Tractus.HtmlToNdi.http                # Sample HTTP requests for manual testing (update alongside API changes)
README.md                             # End-user documentation (currently missing some routes—keep in sync when editing)
Tractus.HtmlToNdi.Tests/              # xUnit regression tests for frame-rate parsing & ring buffer semantics
  FramePipelineTests.cs               # Validates FrameRingBuffer, FrameTimeAverager, FrameRate parsing
  Tractus.HtmlToNdi.Tests.csproj      # net8.0 test project (xUnit + coverlet)
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
 ├─ Prompt/parse CLI flags (see §2 for frame-rate/buffering options)
 ├─ AsyncContext.Run(async)
 │    ├─ Configure CefSettings (RootCachePath=cache/<guid>, autoplay policy override, optional vsync disable, windowless frame rate)
 │    ├─ Cef.Initialize(settings)
 │    └─ Instantiate CefWrapper(width, height, url) and await InitializeWrapperAsync()
 ├─ Build WebApplication (Serilog integration, Swagger, authorization middleware added but unused)
 ├─ Create NDI sender (NDIlib.send_create)
 │    ├─ Advertise `<ndi_capabilities ntk_kvm="true" />`
 │    ├─ Instantiate FramePump(target FPS) and NdiVideoPipeline(buffer depth) and wire them to CefWrapper
 │    └─ Launch background thread polling NDI metadata (1 s timeout)
 ├─ Map HTTP routes directly to CefWrapper methods
 ├─ app.Run()   # blocks until shutdown
 ├─ On shutdown: stop metadata thread, dispose FramePump/NdiVideoPipeline, then CefWrapper
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
* There is no dedicated watchdog thread anymore; instead `FramePump` (see `/Video/FramePump.cs`) invalidates the view at the CLI-selected cadence and issues an extra poke if no paint has landed within one second.
* `SetFrameHandler` lets `Program` swap in the `NdiVideoPipeline` delegate after the NDI sender is initialized; until then CefWrapper falls back to the legacy direct-send behaviour.
* `InvalidateAsync()` marshals invalidation calls onto the CEF UI thread so the pump stays thread-safe.
* `ScrollBy` always uses `(x=0,y=0)` as the mouse location; complex scrolling (e.g., inside scrolled divs) may require additional API work.
* `Click` only supports the left mouse button; drag, double-click, or right-click interactions are not implemented.
* `SendKeystrokes` issues **only** `KeyDown` events with `NativeKeyCode=Convert.ToInt32(char)`. There is no key-up, modifiers, or IME support—uppercase letters require the page to handle them despite missing Shift state.
* `Dispose` detaches the `Paint` handler and disposes the browser, but still has TODO comments for unmanaged cleanup.

### CustomAudioHandler (`Chromium/CustomAudioHandler.cs`)
* `ChannelLayoutToChannelCount` covers most layouts but returns 0 for unsupported ones, causing `GetAudioParameters` to fail (muting audio).
* `OnAudioStreamPacket` copies planar floats into a pre-allocated buffer with each channel occupying a contiguous block (pseudo-planar). `channel_stride_in_bytes` is set to a single channel’s byte count; ensure downstream consumers accept that layout.
* Memory is manually allocated/freed via `Marshal.AllocHGlobal` / `FreeHGlobal`. Failing to call `Dispose` will leak unmanaged memory.

### Video pipeline (`/Video/*.cs`)
* `FrameRate.Parse` normalises CLI input into well-known broadcast-friendly rational pairs (e.g., `59.94` → `60000/1001`).
* `FramePump` drives Chromium invalidations via `PeriodicTimer` and taps the browser again when no paints arrive within 1 s.
* `NdiVideoPipeline` supports two modes:
  * **Direct** (buffer depth 0): forwards Chromium’s GPU buffer handle straight to NDI with zero copies.
  * **Buffered** (buffer depth >0): copies frames into a pooled `BufferedVideoFrame` queue, drops the oldest frame on overflow, and uses a paced loop to send the freshest frame each tick while repeating the last frame when Chromium stalls.
* Telemetry: `FrameTimeAverager` calculates measured cadence; metrics (target FPS, measured FPS, dropped/repeated counts) are logged every 10 s and emitted as `<ndi_frame_metrics .../>` metadata.
* Dispose order matters: always dispose the pipeline before `CefWrapper` so pooled frames are released cleanly.

### NDI integration (`Program.cs`)
* `Program.NdiSenderPtr` must remain valid for the lifetime of the process; there is currently **no** call to `NDIlib.send_destroy`. Adding explicit teardown requires guarding against `nint.Zero` in paint/audio handlers.
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
* ✅ Stress-test animation (e.g., WebGL or CSS animation) and confirm frame cadence matches the selected `--fps` (e.g., ~16.7 ms for 60 fps) without dropped frames in receiver telemetry.
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
