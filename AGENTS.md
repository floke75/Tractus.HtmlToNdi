# AGENTS.md — Code Navigation & Improve Plan (for LLMs only)

> Base repo: `https://github.com/tractusevents/Tractus.HtmlToNdi`
> Purpose: help coding agents quickly navigate, modify, and extend **Tractus.HtmlToNdi** into a headless, multi-feed, smooth-pacing WebGL2/WebGPU → **NDI 6+** renderer on Windows with alpha.
> Source of truth for this doc: the user-supplied architecture brief summarizing the repo, reconciled with the current codebase (July 2024 snapshot reflected below).

---

## 0) Ground truth (what the app does today)

* Boots a **CefSharp OffScreen** browser, sized from CLI (`--w`, `--h`) or defaults to **1920×1080**.
* Captures Chromium paint callbacks as **BGRA** buffers and pushes them directly to NDI using `NDIlib.send_send_video_v2` at a fixed **60 fps** numerator/denominator (no intermediate swizzle).
* Wires Chromium audio through `CustomAudioHandler`, interleaving PCM float samples into `NDIlib.audio_frame_v2_t` and forwarding them to the same sender.
* Hosts an **ASP.NET Core minimal API** (Swagger enabled) on `http://*:9999` unless overridden with `--port`, providing URL navigation, scrolling, clicking, keystroke, and refresh endpoints.
* Advertises **NDI KVM** metadata and listens for basic mouse move/down opcodes; left-up is parsed but currently ignored.
* CLI surface: `--ndiname=<name>` (prompts if omitted), `--port=<int>` (prompts if omitted), `--url=<uri>` (defaults to `https://testpattern.tractusevents.com/`), `--w=<int>`, `--h=<int>`.
* Logging and configuration routed through `AppManagement` and **Serilog** (console unless `-quiet`, debug logging when `-debug`).

---

## 1) Repository map (fast navigation)

```
/Chromium/                      # CefSharp OffScreen host, audio handler, sync-context helpers
    AsyncContext.cs             # Single-threaded runner for async Cef initialization
    CefWrapper.cs               # Browser lifecycle, video capture, input helpers, watchdog thread
    CustomAudioHandler.cs       # IAudioHandler implementation → NDI audio frames
    SingleThreadSynchronizationContext.cs
/Models/                        # Minimal HTTP DTOs (GoToUrlModel, SendKeystrokeModel)
AppManagement.cs                # Process bootstrap, logging, global settings
Program.cs                      # Entry point: CLI parsing → Cef init → NDI sender → HTTP API → shutdown
Tractus.HtmlToNdi.csproj        # Target framework/net8.0, package references (CefSharp, Serilog, NDI wrapper, Swagger)
Tractus.HtmlToNdi.http          # REST client scratch file (currently WeatherForecast placeholder)
Properties/launchSettings.json  # (If present) local debugging profiles
README.md                       # User-facing instructions (note: still mentions --width/--height)
```

> Tip: open `Program.cs` first to understand orchestration, then `Chromium/CefWrapper.cs` for the video path.

---

## 2) High-level architecture & data flow

```
[Program.Main]
  ├─ Initialize logging/AppManagement (console + rolling file)
  ├─ Parse CLI (ndiname/port/url/w/h) with interactive fallbacks
  ├─ Spin up CefSharp OffScreen inside AsyncContext → CefWrapper (size, initial URL)
  ├─ Create NDI sender (`NDIlib.send_create`) and advertise `<ndi_capabilities ntk_kvm="true" />`
  ├─ Launch metadata thread for KVM events (mouse move/down)
  ├─ Build ASP.NET Core minimal API + Swagger for control endpoints
  └─ Run host until shutdown, then dispose browser, stop KVM thread, delete temp cache
         │
         ▼
[CefWrapper]
  ├─ Owns ChromiumWebBrowser (windowless, transparent, muted by default)
  ├─ OnPaint → wraps `NDIlib.video_frame_v2_t` (BGRA, 60/1 fps) → `NDIlib.send_send_video_v2`
  ├─ Render watchdog thread invalidates view if no paint in >1 s to keep frames flowing
  ├─ Input helpers: `SetUrl`, `ScrollBy`, `Click` (synthesizes mouse down/up with 100 ms gap), `SendKeystrokes`, `RefreshPage`
  └─ Audio handler instance bridges PCM packets to NDI
         │
         ▼
[CustomAudioHandler]
  ├─ Negotiates channel layout → buffer allocation
  ├─ Copies planar float channels from CefSharp into an interleaved block
  └─ Sends audio via `NDIlib.send_send_audio_v2`
```

---

## 3) File-by-file guidance (extend safely)

### 3.1 `Program.cs` — orchestration & hosting

**Responsibilities**

* CLI parsing with interactive prompts when `--ndiname`/`--port` omitted; note width/height flags are currently `--w`/`--h` (README mismatch).
* Boots Cef (via `AsyncContext`) and instantiates the singleton `CefWrapper` bound to `Program.browserWrapper`.
* Creates NDI sender (`Program.NdiSenderPtr`) and injects KVM metadata.
* Spawns a metadata polling thread translating `ndi_kvm` opcodes (`0x03` move, `0x04` left down, `0x07` left up TODO) into browser actions.
* Sets up the minimal API (`/seturl`, `/scroll/{increment}`, `/click/{x}/{y}`, `/keystroke`, `/type/{string}`, `/refresh`) with Swagger UI.
* Handles graceful shutdown: stops metadata thread, disposes browser, deletes per-run cache directory.

**Extend here**

* Normalize CLI surface (`--width`/`--height` aliases) and add validation/logging before bootstrapping Cef.
* Gate KVM handling behind a flag and expand opcode coverage (0x07 mouse up, keyboard, scroll).
* Insert frame pacing/metrics instrumentation around `NDIlib.send_send_video_v2` if pacing moves out of `CefWrapper`.
* Expand minimal API (e.g., `/fps`, `/size`, `/stats`, `/eval`) and expose structured responses instead of bare `bool/void`.

### 3.2 `AppManagement.cs` — bootstrap/helpers

* Sets up Serilog (console optional, daily rolling file in `%USERPROFILE%\Documents`).
* Maintains `InstanceName`, `DataDirectory`, and helper IO utilities (read/write/delete files beneath app base dir).
* Hooks `AppDomain.CurrentDomain.UnhandledException` for logging.

**Extend here**

* Surface configuration defaults (fps, pixel format, cache root) as properties to avoid scattering magic numbers in `Program.cs`.
* Provide CLI parsing helpers (e.g., strongly typed options) and unify interactive prompts.
* Add structured telemetry/logging enrichment, environment detection, or metrics sinks.

### 3.3 `Chromium/CefWrapper.cs`

* Wraps `ChromiumWebBrowser` with audio handler, sets size, and starts a watchdog thread that periodically invalidates the view to avoid Cef throttling.
* `InitializeWrapperAsync` waits for the initial load, caps `WindowlessFrameRate` at 60, mutes/unmutes audio (`ToggleAudioMute()` currently toggles once, leaving audio active), attaches `Paint` event.
* `OnBrowserPaint` copies the raw BGRA buffer pointer supplied by Cef into an `NDIlib.video_frame_v2_t` without CPU copy (NDI consumes the pointer immediately while Cef is still in scope).
* Input helpers translate API calls to Cef host events.

**Extend here**

* Guard `NDIlib.send_send_video_v2` with error handling and optional frame pacing queue.
* Support dynamic resize by disposing and recreating the browser (ensure watchers stop cleanly).
* Integrate additional input handling (right-click, keyboard key-up, scroll per-axis) and ensure `Click` is asynchronous-friendly (avoid `Thread.Sleep`).
* Address the TODO in `Dispose(bool)` with explicit unmanaged cleanup (stop watchdog thread, null references, dispose audio handler).

### 3.4 `Chromium/CustomAudioHandler.cs`

* Maintains a native buffer sized for one second of float PCM, copies planar channel data from Cef into that buffer, and sends to NDI.
* Maps Cef `ChannelLayout` enums to channel counts.

**Extend here**

* Revisit interleaving logic (currently reuses planar buffers; ensure alignment/stride matches NDI expectations).
* Handle audio format changes mid-stream (reallocate buffer, flush old frame).
* Capture audio level metadata for `/stats` or logging.

### 3.5 `Chromium/AsyncContext.cs` & `SingleThreadSynchronizationContext.cs`

* Provide a single-threaded synchronization context so Cef initialization (which requires STA-like sequencing) can be awaited synchronously from `Main`.
* Lifted from CefSharp samples; no modifications yet.

**Extend here**

* If additional async Cef setup is needed (e.g., GPU process, multi-browser instances), ensure tasks post back into this context or migrate to a dedicated hosted service.

### 3.6 `/Models`

* `GoToUrlModel` (string `Url`) used by `/seturl` POST.
* `SendKeystrokeModel` (string `ToSend`) reused by `/keystroke` and `/type` endpoints.

**Extend here**

* Add DTOs for future APIs (`/size`, `/fps`, `/eval`) with validation attributes for Swagger docs.
* Consider splitting models into individual files for clarity.

### 3.7 `Tractus.HtmlToNdi.csproj`

* Targets `net8.0`, x64 platform, `AllowUnsafeBlocks=true` (needed for audio buffer manipulation).
* Package references: `CefSharp.OffScreen.NETCore` 129, `NDILibDotNetCoreBase` 2024.7.22.1, Serilog stack, Swagger tooling.
* Always copies icon (`HtmlToNdi.ico`) and splash image (`HtmlToNdi.png`) to output.

**Extend here**

* Add post-build copy of NDI native binaries if distribution requires manual deployment.
* Define RID-specific publish profiles (`win-x64`) and disable `AnyCPU` if x64 is mandatory.
* Add analyzers/style rules to keep future contributions consistent.

### 3.8 `Tractus.HtmlToNdi.http`

* Currently holds template WeatherForecast request (scaffold leftover). Update with actual control endpoints to simplify manual testing.

### 3.9 `README.md`

* Describes intent but still references `--width/--height` flags that the code no longer consumes (`--w`/`--h`). Keep doc/code parity when modifying either side.

---

## 4) NDI KVM status

* The metadata listener thread (`NDIlib.send_capture`) polls every 1000 ms and decodes base64 payloads from `<ndi_kvm>` metadata frames.
* Opcode handling:
  * `0x03` — mouse move; stores normalized X/Y floats (0–1 range) for later clicks.
  * `0x04` — mouse left down; scales stored normalized coordinates to pixel space and issues `browserWrapper.Click(...)`, which performs down→sleep→up. (Acts as full click, not just down.)
  * `0x07` — mouse left up; currently parsed but intentionally left blank.
* Metadata is logged verbosely via Serilog (`Log.Logger.Warning`), which can flood logs; consider downgrading once debugging is complete.
* No keyboard, scroll wheel, or right-button support yet.

---

## 5) Quick grep snippets

Use `rg` (ripgrep) for fast navigation:

* Chromium setup: `rg "ChromiumWebBrowser"`, `rg "WindowlessFrameRate"`.
* Video path: `rg "send_send_video_v2"`, `rg "OnBrowserPaint"`.
* Audio path: `rg "send_send_audio_v2"`, `rg "ChannelLayout"`.
* HTTP API: `rg "MapPost\(\"/seturl" Program.cs`, `rg "MapGet\(\"/scroll"`.
* KVM: `rg "ndi_kvm"`, `rg "send_capture"`.

---

## 6) Known constraints & current gaps

* **Fixed 60 fps.** `frame_rate_N = 60`, `frame_rate_D = 1` hard-coded in `OnBrowserPaint`. Fractional NTSC rates require explicit support.
* **Single browser instance.** Global `Program.browserWrapper` prevents multi-feed scenarios.
* **Interactive CLI prompts** block unattended startup if `--ndiname`/`--port` missing.
* **Logging verbosity.** Every metadata payload is logged at warning level; adjust before production use.
* **Resource cleanup.** `CefWrapper.Dispose` still includes TODO comments—watchdog thread continues until `disposedValue` flips; ensure thread exit before disposing browser to avoid race conditions.
* **README drift.** CLI docs mention `--width/--height`; update either docs or code to stay synchronized.

---

## 7) Extension roadmap (prioritized)

1. **Frame pacing & fractional fps**
   * Add CLI/API for `--fps-n`/`--fps-d`; implement a pacing loop that repeats last frame when Chromium renders faster than target.
   * Surface metrics via `/stats` (actual send fps, queue depth, dropped/repeated frame counts).
2. **API expansion & ergonomics**
   * Implement `/size`, `/fps`, `/eval`, `/stats`, `/screenshot`; return structured JSON (status, message) with proper error codes.
   * Populate `Tractus.HtmlToNdi.http` with ready-to-run requests.
3. **Input & KVM parity**
   * Complete opcode table (mouse up, drag, keyboard, scroll) and avoid blocking sleeps in `Click`.
   * Support optional secure token for HTTP endpoints and/or KVM enable flag.
4. **Multi-feed architecture**
   * Refactor `Program` to manage multiple `(CefWrapper, NDI sender)` pairs or supervise child processes for isolation.
5. **Performance & stability**
   * Reuse pixel buffers or integrate GPU-backed WebGL2/WebGPU surfaces when Chromium build allows it.
   * Profile audio buffer copying; ensure interleaving meets NDI expectations (channel stride currently equals `sizeof(float)*samples`).
6. **Distribution polish**
   * Supply publish profile (self-contained win-x64) bundling Cef/NDI dependencies, update README with accurate CLI table and Swagger docs.

---

## 8) HTTP API quick reference (current implementation)

| Route | Method | Payload | Behavior |
| --- | --- | --- | --- |
| `/seturl` | `POST` | `{ "url": "https://example.com" }` | Calls `browserWrapper.SetUrl` (no await). Returns `true` (bare JSON `true`). |
| `/scroll/{increment}` | `GET` | None | Sends mouse wheel event with delta `increment`. No response body. |
| `/click/{x}/{y}` | `GET` | None | Issues a synthetic left click at pixel coordinates. |
| `/keystroke` | `POST` | `{ "toSend": "text" }` | Sends each character as a key down event. |
| `/type/{toType}` | `GET` | None | Convenience wrapper over `/keystroke` for simple strings. |
| `/refresh` | `GET` | None | Reloads the current page. |

> All endpoints are anonymous and synchronous. Add cancellation/error handling when extending.

---

## 9) Validation checklist (post-change)

* **Video:** Confirm alpha channel by loading a transparent page and viewing in an NDI receiver with checkerboard background.
* **Audio:** Play stereo content; verify channel count and latency within acceptable bounds in receiving software.
* **HTTP API:** Exercise every route (set URL, scroll, click, keystroke, refresh) via Swagger UI and `Tractus.HtmlToNdi.http`.
* **KVM:** Use an NDI viewer with KVM to ensure mouse moves trigger clicks; add tests when more opcodes supported.
* **Resource cleanup:** Stop the app; confirm cache directory under `cache/<guid>` is deleted and no Cef/NDI handles remain.

---

## 10) Appendices (filled for quick reference)

### /Chromium index

* `AsyncContext.cs` — Static helper exposing `Run(Func<Task>)` that installs a single-thread synchronization context, executes an async delegate, and blocks until completion.
* `CefWrapper.cs` — Class `CefWrapper`: owns the off-screen Chromium browser, handles paint events, pushes BGRA frames to NDI, provides input helpers, and runs a watchdog invalidation thread.
* `CustomAudioHandler.cs` — Class `CustomAudioHandler`: implements `IAudioHandler`, allocates native float buffers per channel layout, copies planar audio into interleaved memory, and sends audio frames to NDI.
* `SingleThreadSynchronizationContext.cs` — Class `SingleThreadSynchronizationContext`: blocking collection–backed context used by `AsyncContext` to serialize work onto the initialization thread.

### /Models index

* `Models/GoToUrlModel.cs` — Defines `GoToUrlModel` (`string Url`) for `/seturl` and `SendKeystrokeModel` (`string ToSend`) consumed by `/keystroke` and `/type`.

### NDI path quick facts

* **Creation:** `Program` builds `NDIlib.send_create_t` with `p_ndi_name` (UTF-8) and calls `NDIlib.send_create` → `Program.NdiSenderPtr` (global handle).
* **Video:** `CefWrapper.OnBrowserPaint` fills `NDIlib.video_frame_v2_t` (BGRA, progressive, 60/1) using `e.BufferHandle` from Cef and calls `NDIlib.send_send_video_v2`.
* **Audio:** `CustomAudioHandler.OnAudioStreamPacket` builds `NDIlib.audio_frame_v2_t` (float interleaved, `channel_stride_in_bytes = sizeof(float)*samples`, `no_channels`, `no_samples`, `sample_rate`) and calls `NDIlib.send_send_audio_v2`.
* **Metadata/KVM:** Startup advertises `<ndi_capabilities ntk_kvm="true" />`; background thread invokes `NDIlib.send_capture` to pull metadata frames.

### Known TODOs noted in code

* `CefWrapper.Dispose(bool)` still contains TODO comments about freeing unmanaged resources and nulling large fields.
* `Program`’s KVM handler ignores opcode `0x07` (mouse up) and lacks error handling around `Convert.FromBase64String`.
* `Tractus.HtmlToNdi.http` retains default WeatherForecast stub and should be updated to match current API.

---

## 11) Notes for LLM agents

* Keep edits **surgical**; gate new behavior behind CLI flags or API toggles to avoid breaking existing workflows.
* Document any CLI/API changes in both the README and this AGENTS guide to prevent future drift.
* Prefer direct BGRA paths when working with NDI—current implementation already bypasses RGBA swizzles; maintain that efficiency.
* Avoid blocking sleeps on hot paths (e.g., replace `Thread.Sleep(100)` in `Click` with async delays or host-side debouncing when adding complex input logic).
* When introducing frame pacing queues, accompany code with plain-English comments describing timing math and fallbacks.
* Log queue depth, send cadence, and drops/repeats when pacing is implemented; expose those metrics via `/stats`.

---

## 12) References

* Architecture & behavior summary derived from repo inspection (Program.cs, CefWrapper.cs, CustomAudioHandler.cs, AppManagement.cs).
* Public README: [https://github.com/tractusevents/Tractus.HtmlToNdi](https://github.com/tractusevents/Tractus.HtmlToNdi)

**End of AGENTS.md**
