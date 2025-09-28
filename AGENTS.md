# AGENTS.md — Code Navigation & Current Architecture

> Base repo: `https://github.com/tractusevents/Tractus.HtmlToNdi`
> Purpose: help coding agents quickly understand and extend **Tractus.HtmlToNdi**, a Windows-focused headless Chromium → NDI bridge with HTTP control and limited KVM.

---

## 0) Ground truth — what ships today

* `Program.cs` bootstraps Serilog logging (`AppManagement.Initialize`), parses CLI flags (`--ndiname`, `--port`, `--url`, `--w`, `--h`), and spins up three subsystems: CefSharp OffScreen browser, NDI sender, and ASP.NET Core minimal API server.
* CefSharp is launched off-thread via `AsyncContext.Run`. The browser is created windowless with audio enabled, fixed viewport, and a watchdog thread that invalidates frames if Chromium goes idle.
* Video frames are forwarded straight from the BGRA paint buffer to NDI using `NDIlib.send_send_video_v2`. Audio frames are captured through a custom `IAudioHandler` and forwarded via `NDIlib.send_send_audio_v2`.
* HTTP API (Swagger enabled) exposes `/seturl`, `/scroll/{increment}`, `/click/{x}/{y}`, `/keystroke`, `/type/{text}`, and `/refresh` for remote control. All routes operate on the single global `CefWrapper` instance.
* KVM metadata from NDI receivers is polled in a background thread. Mouse move (`opcode 0x03`) updates the cached normalized coordinates; mouse down (`opcode 0x04`) triggers a left-click at the cached position.
* Default behavior: 1920×1080 @ 60 fps progressive frames, transparent backgrounds preserved, audio passthrough.

---

## 1) Repository map (fast navigation)

```
/Chromium/                 # CefSharp OffScreen helpers (browser wrapper, audio handler, sync context)
/Models/                   # HTTP DTOs shared by minimal API
AppManagement.cs           # Logging bootstrap, filesystem helpers
Program.cs                 # Entry point: CLI → Chromium + NDI + HTTP API + KVM
Tractus.HtmlToNdi.csproj   # Target framework/net6.0-windows, NuGet packages (CefSharp, NDI)
Tractus.HtmlToNdi.http     # Ready-to-send HTTP API examples
appsettings*.json          # ASP.NET Core configuration defaults
README.md                  # Usage, CLI parameters, known limitations
```

Tip: the runtime state (browser + NDI) is controlled exclusively through `Program.browserWrapper`; there is no dependency injection beyond minimal API static capture.

---

## 2) High-level flow

```
[Main]
  ├─ Call AppManagement.Initialize(args) → Serilog + file logging
  ├─ Prompt/parse CLI → ndiName, port, url, width, height
  ├─ AsyncContext.Run → Cef.Initialize + create CefWrapper(width, height, url)
  │      └─ CefWrapper.InitializeWrapperAsync() registers paint callback & audio handler
  ├─ Build WebApplication → map HTTP routes → enable Swagger
  ├─ Create NDI sender (NDIlib.send_create)
  │      └─ Advertise KVM capability via metadata
  ├─ Start KVM metadata polling thread (NDIlib.send_capture)
  └─ app.Run() → blocks until shutdown, then dispose browser and delete cache dir
```

The browser → NDI path is synchronous on the Cef paint callback: every frame triggers `send_send_video_v2`. Audio is pulled on Cef's audio thread and interleaved before hitting NDI.

---

## 3) Chromium wrapper specifics

* `CefWrapper` stores width/height/url and owns the `ChromiumWebBrowser` instance. Audio is always on; `ToggleAudioMute` is called after initial load to unmute.
* `InitializeWrapperAsync` waits for the initial load, caps `WindowlessFrameRate` to 60, subscribes to `Paint`, and starts a watchdog thread that invalidates once per second to avoid stalls.
* `OnBrowserPaint` sets `frame_rate_N=60`/`frame_rate_D=1`, `FourCC=BGRA`, and forwards Chromium's GPU-buffer handle directly to NDI. No pixel swizzle occurs—Chromium already exposes BGRA and the NDI sender accepts BGRA frames.
* Input helpers: `ScrollBy` issues a mouse wheel event, `Click` performs left-button down/up with a 100 ms pause, `SendKeystrokes` iterates characters sending `KeyDown` events only, and `RefreshPage` reloads the current tab.

---

## 4) HTTP API surface (2024-10 build)

Route | Method | Payload | Effect
---|---|---|---
`/seturl` | POST | `{ "url": "https://..." }` | Calls `CefWrapper.SetUrl`
`/scroll/{increment}` | GET | path int | Sends a wheel event (positive=scroll down)
`/click/{x}/{y}` | GET | path ints | Issues a left click at pixel coordinates
`/keystroke` | POST | `{ "toSend": "..." }` | Iterates characters via `SendKeystrokes`
`/type/{toType}` | GET | path string | Convenience wrapper around `/keystroke`
`/refresh` | GET | none | Reloads the page

Swagger UI is available at `/swagger` on the configured port (default 9999). All endpoints are unauthenticated and operate on the singleton browser.

---

## 5) NDI pipeline

* Creation: `Program.NdiSenderPtr` is initialized once with `NDIlib.send_create` using the CLI-specified source name.
* Video: `CefWrapper.OnBrowserPaint` constructs an `NDIlib.video_frame_v2_t` (BGRA progressive, stride = width × 4) and sends with `NDIlib.send_send_video_v2`.
* Audio: `CustomAudioHandler` allocates an interleaved float buffer sized for one second of audio. For each audio packet, channel planes are copied into the interleaved buffer and transmitted via `NDIlib.send_send_audio_v2`.
* Metadata/KVM: After creation, the app publishes `<ndi_capabilities ntk_kvm="true" />`. A dedicated thread blocks on `NDIlib.send_capture`, inspecting metadata frames for `<ndi_kvm ...>` commands.

---

## 6) KVM handling details

* Only mouse metadata opcodes are processed. `0x03` updates `x`/`y` normalized floats (0–1). `0x04` triggers a left-click using the cached coordinates scaled by the configured width/height. `0x07` (left-up) is recognized but intentionally left empty—`Click` handles both down/up.
* There is no handling for drag, right/middle buttons, keyboard injection, or scroll via KVM metadata; the HTTP API must be used for those interactions.
* The metadata polling thread runs until shutdown; `running` flag flips after `app.Run()` completes, and the thread is joined before exiting.

---

## 7) Known constraints & quirks

1. **Codec support**: Chromium build lacks proprietary codecs (H.264), so DRM/YouTube playback fails.
2. **Frame pacing**: Browser refresh rate and NDI `frame_rate_N/D` are hard-coded to 60/1; no dynamic adjustment or fractional frame rates.
3. **Single instance**: Global statics (`Program.browserWrapper`, `Program.NdiSenderPtr`) assume one browser/sender per process.
4. **Input gaps**: No keyboard key-up events, no text composition, and no error handling for invalid coordinates.
5. **Resource cleanup**: Temporary Cef cache folder is deleted on shutdown, but abrupt termination may leave residue.
6. **Security**: HTTP endpoints have no authentication/authorization; exposure to untrusted networks is unsafe.

---

## 8) Prioritized extension roadmap

1. **Add authentication & TLS to HTTP API** — prevent remote misuse and enable deployment beyond trusted LANs.
2. **Expose dynamic frame rate & resolution controls** — allow runtime `/size` or `/fps` adjustments with safe reinitialization of Cef/NDI.
3. **Improve KVM fidelity** — handle drag (mouse down/up separation), right-click, keyboard metadata, and pointer smoothing.
4. **Robust input API** — add key-up events, text composition, and error responses for invalid requests.
5. **Observability** — implement `/stats` endpoint reporting render cadence, dropped frames, audio state, and cache health.
6. **Codec/WebGL enhancements** — optional Chromium flags for WebGL2/WebGPU and exploring BGRA → NDI zero-copy improvements.

---

## 9) Validation checklist (post-change)

* ✅ Transparent test page → verify alpha in NDI receiver.
* ✅ Motion stress (WebGL animation) → monitor frame cadence ~16.6 ms for 60 fps.
* ✅ Audio playback (stereo) → confirm levels in receiver, no drift.
* ✅ HTTP API → exercise every route via `Tractus.HtmlToNdi.http` or Swagger.
* ✅ KVM metadata → confirm pointer click arrives when interacting from NDI Studio Monitor.

---

## 10) Appendices

### /Chromium index

* `CefWrapper.cs` — owns `ChromiumWebBrowser`; handles initialization, render watchdog, paint-to-NDI forwarding, and user input helpers (`SetUrl`, `ScrollBy`, `Click`, `SendKeystrokes`, `RefreshPage`).
* `CustomAudioHandler.cs` — `IAudioHandler` implementation converting CefSharp planar float buffers into interleaved floats for NDI audio frames.
* `AsyncContext.cs` — helper to run async Cef initialization on a dedicated single-threaded synchronization context.
* `SingleThreadSynchronizationContext.cs` — blocking queue-based context used by `AsyncContext` to keep CefSharp thread affinity.

### /Models index

* `GoToUrlModel.cs` — DTO with `string Url`; consumed by `/seturl` POST.
* `SendKeystrokeModel` (same file) — DTO with `string ToSend`; consumed by `/keystroke` POST and indirectly by `/type/{toType}`.

### NDI path summary

* **Creation**: `Program.NdiSenderPtr = NDIlib.send_create` using UTF-8 encoded name.
* **Video send**: `CefWrapper.OnBrowserPaint` builds `NDIlib.video_frame_v2_t` with BGRA buffer handle and invokes `NDIlib.send_send_video_v2`.
* **Audio send**: `CustomAudioHandler.OnAudioStreamPacket` copies samples into `audio_frame_v2_t` and calls `NDIlib.send_send_audio_v2`.
* **Metadata**: Capabilities advertised via `NDIlib.send_add_connection_metadata`; KVM commands retrieved through `NDIlib.send_capture` loop in `Program`.

### Known TODOs in code

* `CefWrapper.Dispose` still contains commented TODOs regarding unmanaged cleanup; currently acceptable but consider auditing.
* KVM handler ignores opcode `0x07` (mouse up) and lacks drag/scroll implementations.
* Keyboard injection uses only key-down events—no key-up or modifier support, which may break complex input.

---

## 11) Notes for future agents

* Keep edits focused; touch only relevant files and respect the single-instance design unless intentionally refactoring.
* Prefer enhancing existing helpers (`CefWrapper`, `Program`) rather than introducing new globals.
* When adjusting frame rate or resolution, ensure both CefSharp (`WindowlessFrameRate`, browser size) and NDI (`frame_rate_N/D`, `xres`, `yres`) stay in sync.
* Document new HTTP endpoints in both README and `Tractus.HtmlToNdi.http`.

---

## 12) Provenance / references

* Source: repo inspection (2024-10 snapshot) of Program.cs, Chromium/*, Models/*, README.
* Public releases & documentation: [https://github.com/tractusevents/Tractus.HtmlToNdi](https://github.com/tractusevents/Tractus.HtmlToNdi).

**End of AGENTS.md**
