# AGENTS.md — Orientation & extension brief for Tractus.HtmlToNdi

> Repository: [`tractusevents/Tractus.HtmlToNdi`](https://github.com/tractusevents/Tractus.HtmlToNdi)
> Scope: Windows-only utility that renders a Chromium page off-screen and publishes the resulting video/audio (plus limited KVM metadata) as an NDI source with a minimal HTTP control plane.

This document is the authoritative onboarding guide for coding agents. Keep it synchronized with the source whenever behaviour or public surfaces change.

---

## 0. Ground truth (current net8.0 build)

* **Core function**: wraps CefSharp OffScreen and the NewTek NDI .NET wrapper to broadcast a single web page as an RGBA-capable NDI 6 source (video + optional audio). Default viewport is **1920×1080 @ 60 fps**, and alpha survives end-to-end when the content is transparent.
* **Process topology**: a single `Program.Main` instance hosts everything—Chromium browser, NDI sender, ASP.NET Core minimal API, and an optional NDI KVM polling thread. Only one browser/NDI sender pair exists per process.
* **Transport**: frames are delivered straight from CefSharp’s BGRA buffer handle to `NDIlib.send_send_video_v2` (no CPU colour swizzle). Audio is captured via `CustomAudioHandler` and forwarded to NDI as planar-like float buffers.
* **Control surfaces**: HTTP API on HTTP (default port `9999`), CLI switches for bootstrap configuration, and inbound `<ndi_kvm …>` metadata for mouse move + left-click (move stored, click executed).
* **Limits today**: 60 fps cap (Chromium windowless frame rate + hard-coded NDI frame_rate_N/D), fixed resolution once launched, one Chromium instance, no TLS/auth on HTTP, Chromium build without proprietary codecs (H.264/DRM content fails), and the audio buffer layout does not perfectly match true interleaved expectations.

---

## 1. Repository map (fast navigation)

```
/Chromium/
  AsyncContext.cs                       # Single-thread async pump used during Cef initialization
  CefWrapper.cs                         # Owns ChromiumWebBrowser, paint → NDI video bridge, HTTP/KVM helpers
  CustomAudioHandler.cs                 # IAudioHandler implementation forwarding audio frames to NDI
  SingleThreadSynchronizationContext.cs # BlockingCollection-backed context used by AsyncContext
/Models/
  GoToUrlModel.cs                       # DTO { Url } for POST /seturl
  SendKeystrokeModel.cs                 # DTO { ToSend } for POST /keystroke
AppManagement.cs                        # Logging bootstrap, -debug/-quiet parsing, user Documents log sink
Program.cs                              # Main: CLI parsing, Cef setup, HTTP API, NDI sender + KVM metadata loop
Properties/                             # Assembly metadata/resources
Tractus.HtmlToNdi.csproj                # net8.0 exe, NuGet refs (CefSharp OffScreen, Swashbuckle, Serilog, NDI)
Tractus.HtmlToNdi.http                  # (Outdated) HTTP file; replace weather template with real routes when updating API
README.md                               # End-user docs (note: CLI flag names here partially stale)
```

There are no nested `AGENTS.md` files—this guide governs the full repository.

---

## 2. Execution flow & data paths

```
Program.Main
 ├─ Set current directory to the exe folder
 ├─ AppManagement.Initialize(args)           # configure Serilog, honour -debug/-quiet, hook AppDomain handler
 ├─ Parse CLI (--ndiname/--port/--url/--w/--h) or prompt interactively for name/port when missing
 ├─ AsyncContext.Run(async)
 │    ├─ Configure CefSettings (cache/<guid>, autoplay-policy, EnableAudio)
 │    ├─ Cef.Initialize(settings)
 │    └─ Instantiate CefWrapper(width, height, startUrl) → await InitializeWrapperAsync()
 │         ├─ Wait for initial load, set WindowlessFrameRate=60, unmute audio
 │         └─ Subscribe to Paint + start watchdog thread that invalidates on >1 s silence
 ├─ Build WebApplication (Serilog integration, Swagger, authorization middleware stub)
 ├─ Create NDI sender (NDIlib.send_create → NdiSenderPtr)
 │    ├─ Advertise `<ndi_capabilities ntk_kvm="true" />`
 │    └─ Launch metadata polling thread (NDIlib.send_capture, 1 s timeout)
 ├─ Map HTTP routes to Program.browserWrapper helpers
 ├─ app.Run() blocks until shutdown
 ├─ On shutdown: stop metadata thread, dispose CefWrapper
 └─ Delete temporary Cef cache directory (`cache/<guid>`, best-effort)
```

Video flow: `CefWrapper.OnBrowserPaint` receives BGRA buffers (`OnPaintEventArgs.BufferHandle`) and pushes them directly into `NDIlib.video_frame_v2_t` (BGRA FourCC, progressive, frame_rate_N=60, frame_rate_D=1). Audio flow: `CustomAudioHandler.OnAudioStreamPacket` copies planar float channels into a pre-allocated buffer (channel-contiguous) and calls `NDIlib.send_send_audio_v2`.

---

## 3. CLI & configuration contract

`Program.Main` recognises these switches (double hyphen required unless noted):

| Flag | Example | Behaviour / Default |
| --- | --- | --- |
| `--ndiname=<value>` | `--ndiname="Studio Browser"` | Sets NDI source name. Absent flag triggers interactive prompt until non-empty. Initial default is `"HTML5"` before prompting. |
| `--port=<int>` | `--port=9999` | HTTP listener port. Missing flag triggers interactive prompt for numeric input. |
| `--url=<https://…>` | `--url=https://testpattern.tractusevents.com/` | Startup page URL. Defaults to Tractus test pattern. |
| `--w=<int>` | `--w=1920` | Browser width in pixels. Defaults to 1920. (README still lists `--width`; update docs if code changes.) |
| `--h=<int>` | `--h=1080` | Browser height in pixels. Defaults to 1080. (README still lists `--height`.) |
| `-debug` | `-debug` | Raises Serilog minimum level to `Debug`. |
| `-quiet` | `-quiet` | Disables console logging while retaining file logging. |

Other configuration surfaces:

* **Logging sinks**: Serilog writes to console (unless `-quiet`) and `%USERPROFILE%/Documents/<AppName>_log.txt` (rolling daily).
* **Build target**: `Tractus.HtmlToNdi.csproj` targets **.NET 8.0**, forces `PlatformTarget=x64`, and allows unsafe blocks for the NDI interop.
* **Runtime assets**: `HtmlToNdi.ico` and `HtmlToNdi.png` are copied to the output directory for branding/metadata.

---

## 4. HTTP API surface (unauthenticated, served by Program.cs)

| Route | Method | Payload | Effect |
| --- | --- | --- | --- |
| `/seturl` | POST | JSON `{ "url": "https://…" }` (`GoToUrlModel`) | Calls `CefWrapper.SetUrl`, loading the requested page immediately. |
| `/scroll/{increment}` | GET | Path `increment` (int) | Issues a vertical scroll via `SendMouseWheelEvent(0,0,0,increment)`. Origin fixed at (0,0). |
| `/click/{x}/{y}` | GET | Path `x`, `y` (pixels) | Sends a left mouse click (down → 100 ms sleep → up) at supplied coordinates. |
| `/keystroke` | POST | JSON `{ "toSend": "..." }` (`SendKeystrokeModel`) | Iterates characters, emitting `KeyDown` events with `NativeKeyCode` per char; no key-up/modifiers. |
| `/type/{toType}` | GET | Path string | Convenience wrapper around `/keystroke`. |
| `/refresh` | GET | none | Calls `ChromiumWebBrowser.Reload()`. |

Swagger/OpenAPI UI is exposed at `/swagger`. No TLS or auth is provided; place behind a trusted proxy when deploying.

_When editing or adding endpoints: update this table, refresh `Tractus.HtmlToNdi.http` with working samples, and sync `README.md`._

---

## 5. Subsystem specifics

### Program.cs (orchestration)
* Maintains global `Program.NdiSenderPtr` and `Program.browserWrapper`. Assume **single-instance** semantics throughout the repo.
* Metadata polling thread caches normalized mouse coordinates (opcode `0x03`) and triggers `browserWrapper.Click` on opcode `0x04`. Opcode `0x07` is parsed but ignored.
* Shutdown path sets a `running` flag, joins the metadata thread, disposes the browser wrapper, and deletes the per-launch cache folder; there is still no `NDIlib.send_destroy` call.

### AppManagement.cs (bootstrap/helpers)
* Ensures base data directory exists, wires `AppDomain.CurrentDomain.UnhandledException`, and configures Serilog according to `-debug`/`-quiet`.
* Logs to `%USERPROFILE%/Documents/<AppName>_log.txt`. Exposes `InstanceName` formatted as `<os>_<arch>_<MachineName>` for potential telemetry.

### Chromium/CefWrapper.cs (Chromium host)
* Constructs a windowless `ChromiumWebBrowser` with `AudioHandler = CustomAudioHandler` and fixed `Size(width,height)`.
* `InitializeWrapperAsync()` waits for the initial load, sets `WindowlessFrameRate = 60`, unmutes audio, registers the `Paint` handler, and launches `RenderWatchdog` (invalidates view if >1 s without paint).
* Paint handler builds an `NDIlib.video_frame_v2_t` using `OnPaintEventArgs.BufferHandle`; no CPU copy occurs. Aspect ratio is derived from the current frame.
* Input helpers: `SetUrl`, `ScrollBy` (always at origin), `Click` (left button only, synthetic 100 ms hold), `SendKeystrokes` (KeyDown only, per character), `RefreshPage`.
* `Dispose` detaches the paint handler and disposes the browser. TODO comments remain for unmanaged cleanup.

### Chromium/CustomAudioHandler.cs (audio bridge)
* Supports a wide set of CEF channel layouts; unsupported layouts return `false` to mute the stream.
* Allocates a buffer large enough for one second of float samples (`sampleRate * channelCount * sizeof(float)`), storing channels sequentially (pseudo-planar) despite `channel_stride_in_bytes` advertising interleaved stride.
* Uses unsafe `Buffer.MemoryCopy` per channel. Memory is freed in `Dispose()`; ensure this is called to avoid leaks.

### Async helpers (`AsyncContext`, `SingleThreadSynchronizationContext`)
* Provide a simple single-threaded async loop so CefSharp initialization runs on a deterministic thread, mimicking STA behaviour without WinForms/WPF dependencies.

### Models
* `GoToUrlModel` – DTO `{ string Url }`, used by `/seturl`.
* `SendKeystrokeModel` – DTO `{ string ToSend }`, used by `/keystroke` and `/type`.

---

## 6. NDI KVM support (current state)

* Advertises `<ndi_capabilities ntk_kvm="true" />` when creating the sender.
* A background thread polls `NDIlib.send_capture` (timeout 1000 ms). Metadata payload is expected as `<ndi_kvm u="…"/>` with base64 content.
* Supported opcodes:
  * `0x03` – Mouse move: cache normalized `x`, `y` (0–1 range) until a click arrives.
  * `0x04` – Mouse left down: scales cached coords to current `width`/`height` and calls `CefWrapper.Click` (sends down + up internally).
  * `0x07` – Mouse left up: parsed but intentionally ignored (Click already generates up).
* Additional KVM features (right-click, scroll, keyboard) are **not implemented**; extend by decoding more opcodes and piping into CefSharp input APIs.

---

## 7. Known constraints & facts

1. **Single-instance design**: Globals (`Program.browserWrapper`, `Program.NdiSenderPtr`) assume exactly one Chromium/NDI pipeline.
2. **Transport assumptions**: NDI video frames are BGRA with `frame_rate_N/D` pinned to 60/1. Changing FPS or format requires updating both Cef (`WindowlessFrameRate`) and the NDI frame metadata.
3. **Codec coverage**: Shipping Chromium build lacks proprietary codecs (H.264/DRM). Expect YouTube/Netflix-style sites to fail.
4. **Audio layout**: Audio buffer is stored channel-by-channel rather than fully interleaved, despite stride metadata; receivers must tolerate this layout.
5. **Input gaps**: `/scroll` always scrolls from `(0,0)`; `/click` only supports single left clicks; `/keystroke` lacks key-up, modifiers, IME, or special keys.
6. **HTTP exposure**: No authentication or TLS. Never expose directly to untrusted networks without upstream protection.
7. **Resource cleanup**: `NDIlib.send_destroy` and `Cef.Shutdown()` are not called explicitly; process exit handles teardown. Cache folders may linger if deletion fails.
8. **Documentation drift**: `README.md` still advertises `--width/--height`; keep README and this guide aligned when flags change.

---

## 8. Quick grep map

Use these strings to jump to relevant logic without a full tree walk:

* **Chromium setup** – `ChromiumWebBrowser(`, `WindowlessFrameRate`, `Invalidate(PaintElementType.View)`
* **Frame capture** – `OnBrowserPaint`, `BufferHandle`, `NDIlib.video_frame_v2_t`
* **NDI video/audio** – `NDIlib.send_send_video_v2`, `NDIlib.send_send_audio_v2`, `FourCC_type_BGRA`, `frame_rate_N`
* **HTTP API** – `MapPost("/seturl"`, `MapGet("/click"`, `WithOpenApi`
* **KVM metadata** – `ndi_kvm`, `NDIlib.send_capture`, `opcode == 0x04`

---

## 9. Extension backlog (keep changes additive when possible)

A. **Stable fractional frame pacing**
   * Add CLI like `--fps-n/--fps-d` (e.g., `30000/1001`) and propagate to Cef (`WindowlessFrameRate`) plus the NDI frame metadata.
   * Introduce a FramePacer (single-producer/single-consumer ring). Producer = Chromium paint events; Consumer = high-resolution timer sending freshest frame on cadence. Repeat last frame when idle.

B. **Multi-instance / multi-output support**
   * Refactor Program into session objects (`BrowserSession`, `NdiSession`) and manage a collection keyed by identifiers. Consider supervisor → worker processes for isolation if multiple Cef instances prove fragile.

C. **Runtime resizing**
   * Provide `/size { width, height }` endpoint that recreates Chromium + NDI resources safely. Expect a short freeze; document this in logs and API responses.

D. **WebGL2/WebGPU toggles**
   * Add CLI flags (`--webgl2`, `--webgpu`) to inject Cef command-line switches (`--enable-webgl2`, GPU sandbox tweaks) during initialization. Keep WebGPU opt-in experimental.

E. **Pixel formats & HDR (NDI 6)**
   * Allow `--pixel=bgra|rgba` (or HDR toggles) when the NDI wrapper supports direct BGRA submission vs. RGBA swizzle. Investigate HDR/10-bit path gated behind `--hdr`.

F. **HTTP/API growth**
   * Add `/refresh` (already GET) parity for POST, `/size`, `/fps`, `/eval`, `/stats`, `/screenshot`. Update DTOs in `/Models` and expand Swagger/README samples accordingly.

G. **Observability & safety**
   * Secure HTTP with minimal auth or document reverse-proxy expectations; expose frame/audio counters via `/stats`; reduce warning-level log spam from frequent KVM metadata.

---

## 10. Validation checklist (after impactful changes)

* ✅ Confirm alpha by loading `https://testpattern.tractusevents.com/` and checking transparency in an NDI receiver.
* ✅ Stress-test with heavy WebGL/WebGPU or CSS animation for ≥10 min; confirm ~16.6 ms (60 fps) cadence without drops.
* ✅ Play known stereo audio and verify both channels arrive without distortion or channel swaps.
* ✅ Exercise `/seturl`, `/scroll`, `/click`, `/keystroke`, `/type`, `/refresh` via Swagger or `Tractus.HtmlToNdi.http`; watch logs for errors.
* ✅ Validate inbound KVM click from a receiver (e.g., NewTek Studio Monitor) results in an on-page click.
* ✅ Inspect `%USERPROFILE%/Documents/<AppName>_log.txt` for warnings/errors post-session.

---

## 11. Appendices for quick recall

### /Chromium folder index
* `AsyncContext.cs` — Provides `AsyncContext.Run(Func<Task>)`; spins a dedicated thread with `SingleThreadSynchronizationContext` to keep Cef initialization thread-affine.
* `CefWrapper.cs` — Manages Chromium browser lifecycle, paint handler → NDI video submission, watchdog invalidations, and HTTP/KVM-facing helpers (`SetUrl`, `ScrollBy`, `Click`, `SendKeystrokes`, `RefreshPage`).
* `CustomAudioHandler.cs` — Implements `IAudioHandler`; negotiates audio parameters, allocates float buffer, copies planar audio, forwards via `NDIlib.send_send_audio_v2`.
* `SingleThreadSynchronizationContext.cs` — Blocking queue-backed synchronization context consumed by `AsyncContext`.

### /Models folder index
* `GoToUrlModel` — `{ string Url }`; consumed by `/seturl`.
* `SendKeystrokeModel` — `{ string ToSend }`; consumed by `/keystroke` and indirectly `/type`.

### NDI path summary
* **Creation**: `Program.cs` (`var settings = new NDIlib.send_create_t { p_ndi_name = UTF.StringToUtf8(ndiName) }; Program.NdiSenderPtr = NDIlib.send_create(ref settings);`).
* **Video send**: `Chromium/CefWrapper.cs` `OnBrowserPaint` builds `NDIlib.video_frame_v2_t` (BGRA, `frame_rate_N=60`, `frame_rate_D=1`, `line_stride_in_bytes = width*4`) and calls `NDIlib.send_send_video_v2`.
* **Audio send**: `Chromium/CustomAudioHandler.cs` `OnAudioStreamPacket` builds `NDIlib.audio_frame_v2_t` and calls `NDIlib.send_send_audio_v2`.
* **Metadata send/receive**: Program adds connection metadata announcing KVM and polls `NDIlib.send_capture` for incoming control messages.

### Known TODOs / loose ends in code
* `CefWrapper.Dispose` contains TODO comments about freeing unmanaged resources—explicitly shut down Cef and destroy the NDI sender when refactoring cleanup.
* `CustomAudioHandler`’s channel copying currently mislabels the layout as interleaved; either change metadata or truly interleave.
* `Tractus.HtmlToNdi.http` still contains the default weatherforecast template—replace with real route samples alongside API updates.

---

## 12. Notes for contributors

* Preserve the single-instance assumptions unless you are ready to refactor Program and related static state.
* Respect CefSharp threading rules: keep initialization inside `AsyncContext` and avoid touching Cef objects from arbitrary threads.
* When adding HTTP endpoints, update Swagger metadata (`WithOpenApi()`), refresh `README.md`, `Tractus.HtmlToNdi.http`, and this guide in the same change.
* If modifying audio/video pipeline, review both `CefWrapper` and `CustomAudioHandler` to keep NDI metadata consistent with buffer layout.
* Prefer structured Serilog logging for new diagnostics (avoid concatenation when parameters would help).

_Last reviewed against repository state in this workspace. Update promptly when behaviour shifts._
