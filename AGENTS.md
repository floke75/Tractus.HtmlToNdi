# AGENTS.md — Code Navigation & Improve Plan (for LLMs only)

> Base repo: `https://github.com/tractusevents/Tractus.HtmlToNdi`
> Purpose: help coding agents quickly navigate, modify, and extend **Tractus.HtmlToNdi** into a headless, multi-feed, smooth-pacing WebGL2/WebGPU → **NDI 6+** renderer on Windows with alpha.
> Source of truth for this doc: the user-supplied architecture brief summarizing the repo (merged here). 

---

## 0) Ground truth (what the app does today)

* Wraps **Chromium (CefSharp OffScreen)** and an **NDI .NET wrapper** to publish a web page (video+audio) as an NDI source. Default viewport: **1920×1080 @ 60 fps**. Alpha is preserved if the page is transparent. HTTP control API is included. 
* Core pieces: off-screen Chromium with transparency; NDI sender (RGBA frames, optional audio); minimal ASP.NET Core API (default port **9999**); optional NDI **KVM** support (mouse move/down/up handled). 
* CLI flags (from README/usage): `--ndiname`, `--width`, `--height`, `--url`, `--port`. 
* Key limits today: frames sent as **RGBA** (byte-swizzle overhead from BGRA likely); **no H.264** codecs in Chromium build (YouTube etc. won’t work); 60 fps cap; single browser instance per process; fixed output size; audio passed through without mixing. 

---

## 1) Repository map (fast navigation)

> Use these paths/keywords to index the codebase. Adapt to actual filenames with a quick directory scan.

```
/Chromium/                 # CefSharp OffScreen host & handlers (render/audio/input/helpers)
/Models/                   # HTTP API request/response DTOs (e.g., SetUrl)
/Properties/               # Assembly info, resources
AppManagement.cs           # Bootstrap/config helpers (args/appsettings wiring)
/* Program.cs */           # Entry point: args → NDI + Chromium → HTTP API → run/shutdown
Tractus.HtmlToNdi.csproj   # NuGet refs (CefSharp OffScreen, NDI wrapper), target framework, native copies
Tractus.HtmlToNdi.http     # HTTP request examples (API smoke tests)
appsettings*.json          # Optional config (logging, HTTP, etc.)
README.md                  # Usage/flags/limits
```

> Tip: enumerate `/Chromium` and `/Models` first; they contain the primary extension points.

---

## 2) High-level architecture & data flow (confirmed)

```
[Program.cs]
  ├─ Parse CLI (--ndiname/--width/--height/--port/--url)
  ├─ Init NDI sender (name, RGBA WxH, ~60 fps)
  ├─ Init CefSharp OffScreen browser (windowless+transparent, WxH, cap fps=60)
  ├─ Start ASP.NET Core minimal API (e.g., POST /seturl, /refresh)
  └─ Run until shutdown; dispose NDI + Chromium, stop KVM thread
         │
         ▼
[Chromium wrapper (OffScreen)]
  ├─ Create ChromiumWebBrowser (transparent background)
  ├─ Frame capture (BGRA → RGBA copy if needed) → NDI video frame
  ├─ (Optional) IAudioHandler → NDI audio frame
  └─ Control surface: SetUrl(), Refresh(), Click(x,y) for KVM
         │
         ▼
[NDI .NET wrapper]
  ├─ Create sender (metadata can advertise KVM)
  ├─ Send video frames (RGBA) and PCM audio
  └─ Capture NDI metadata for KVM; inject to Chromium
```

All above is explicitly described or implied in the architecture brief and README. 

---

## 3) File-by-file guidance (what to extend where)

> Names/locations reflect the merged architecture brief. Confirm with a quick `grep` (see §5).

### 3.1 `Program.cs` — orchestration & hosting

**Responsibilities**

* Parses CLI → sets up logging (Serilog) → creates **NDI sender** → creates **Chromium OffScreen** browser → starts **HTTP API** → runs until shutdown; cleanly disposes NDI/browser; joins **KVM** thread if enabled. 

**Extend here**

* Add new API routes: `/refresh`, `/size`, `/fps`, `/eval`, `/stats`. 
* Insert **FramePacer** hookup (consumer loop) if send cadence is coordinated here.
* Wire **KVM enable/disable** by CLI flag; ensure thread lifetime is managed.

### 3.2 `AppManagement.cs` — bootstrap/helpers

**Responsibilities**

* Centralizes argument parsing & defaults (name = “HTML5”, 1920×1080, port 9999) and possibly Cef/NDI init scaffolding. 

**Extend here**

* Add CLI for **fractional fps** (`--fps-n`, `--fps-d`), **pixel format** (`--pixel=bgra|rgba`), **HDR** toggle (NDI 6).
* Inject Chromium switches for **WebGL2**/**WebGPU** when you move to accelerated paths.

### 3.3 `/Chromium/*` — CefSharp OffScreen host

**Responsibilities**

* Creates **ChromiumWebBrowser** (windowless, transparent), caps internal redraw to **60 fps** (`BrowserSettings.WindowlessFrameRate`). 
* Captures frames (BGRA buffer) on paint or via screenshot API; converts to **RGBA** (current sender path) and forwards to NDI. 
* Implements **IAudioHandler** (optional) → forward PCM to NDI; implements control methods: `SetUrl`, `Refresh`, `Click(x,y)` (for NDI KVM). 

**Extend here**

* Add producer hook to push frames into **FramePacer** queue.
* Provide **BGRA** → NDI direct path if wrapper supports it (avoid swizzle).
* Add **EvaluateScriptAsync** bridge for `/eval`.

### 3.4 `/Models/*` — HTTP DTOs

* Expect a `SetUrl`/`GoToUrlModel` with `Url` string for `POST /seturl`; extend with DTOs for `/size`, `/fps`, `/eval`. 

### 3.5 `Tractus.HtmlToNdi.csproj`

* NuGet: **CefSharp.OffScreen**, **CefSharp.Common**, **NDI .NET wrapper** (“NdiLibDotNetAdvanced”). Ensure native NDI DLL copy rules. 
* TargetFramework: .NET 6/7/8 (verify). Add build constants for **x64**, optional **HDR** builds.

### 3.6 `Tractus.HtmlToNdi.http`

* Contains ready-to-run `POST /seturl` etc. Extend with `/refresh`, `/size`, `/fps`, `/eval`, `/stats`. 

---

## 4) NDI KVM (implemented subset; extendable)

* Sender advertises `<ndi_capabilities ntk_kvm="true" />`; a **background thread** uses `NDIlib.send_capture(...)` to receive metadata. Data is XML `<ndi_kvm ...>` with base64 payload. Handled opcodes:

  * `0x03` **mouse move** → store normalized (x,y) in [0..1].
  * `0x04` **mouse left down** → scale to pixels → `Click(x,y)` (down).
  * `0x07` **mouse left up** → scale to pixels → `Click(x,y)` (up).
    Extend with keyboard, right-click, scroll by decoding more opcodes and mapping to Cef input. 

---

## 5) Quick “grep map” (drop-in queries)

Search these strings to jump to relevant code:

**Chromium setup**
`WindowlessRenderingEnabled` · `SetAsWindowless` · `BrowserSettings.WindowlessFrameRate` · `ChromiumWebBrowser(`

**Frame capture paths**
`OnPaint(` · `IRenderHandler` · `ScreenshotAsync` · `GetBitmapAsync` · `BGRA` · `RGBA` · `PixelFormat`

**NDI video/audio**
`NDIlib_send_create` · `NDIlib.send_` · `VideoFrame` · `FourCC` · `frame_rate_N` · `frame_rate_D` · `send_audio`

**HTTP API**
`WebApplication.CreateBuilder` · `MapPost("/seturl"` · `Refresh()` · `GoToUrlModel`

**KVM**
`ndi_kvm` · `base64` · `0x03` · `0x04` · `0x07` · `Click(`

(Entities and flow confirmed in the brief/README.) 

---

## 6) Known constraints & facts (repo-stated)

* **Defaults**: 1920×1080; **60 fps**; alpha honored when page is transparent; audio passthrough. 
* **Limits**: RGBA send path (BGRA→RGBA conversion cost); no H.264 codecs (YouTube likely won’t play); 60 fps cap; one browser/NDI per process; fixed output size. 

---

## 7) Extension plan (prioritized tasks for agents)

> Keep changes additive; gate behind CLI flags where possible.

### A. Stable 29.97p (or 59.94p) pacing

* Add `--fps-n`/`--fps-d` (e.g., **30000/1001** for 29.97).
* Implement **FramePacer** (SPSC ring). Producer = Chromium frames; Consumer = precise timer at target fps; on tick: **send newest**, else **repeat last**. (Alpha preserved.)

**Sender wiring (C# snippet)**

```csharp
// When filling the NDI video frame:
video.frame_rate_N = FpsNumerator;   // e.g., 30000
video.frame_rate_D = FpsDenominator; // e.g., 1001
// frame_format_type = progressive; FourCC = BGRA or RGBA per wrapper support.
```

**Consumer loop (C# skeleton)**

```csharp
var ticksPerSec = (double)Stopwatch.Frequency;
var interval = 1001.0 / 30000.0; // 29.97p
var next = Stopwatch.GetTimestamp();
Frame? last = null;
while (running) {
    if (ring.TryPop(out var f)) last = f;   // drain to freshest
    if (last != null) NdiSend(last);
    next += (long)(interval * ticksPerSec);
    var sleep = next - Stopwatch.GetTimestamp();
    if (sleep > 0) Thread.Sleep(TimeSpan.FromSeconds(sleep / ticksPerSec));
    else next = Stopwatch.GetTimestamp();  // catch up
}
```

### B. Multi-instance / multi-output

* Introduce `BrowserSession` + `NdiSession`; manage a collection keyed by `--instance`.
* Option 1: multiple sessions in one process (thread per session). Option 2: supervisor process spawning child processes (simpler isolation).

### C. Adjustable resolution & live resize

* Add `POST /size { width, height }` → recreate Chromium + NDI (expect one-time stutter).
* Optional: scale or letterbox via CSS.

### D. WebGL2/WebGPU toggles

* Add `--webgl2 on`, `--webgpu on`; inject Chromium switches in Cef settings. Use WebGL2 baseline first; gate WebGPU behind flag.

### E. Pixel formats & HDR (NDI 6)

* Add `--pixel=bgra|rgba`; prefer **BGRA→NDI** direct if supported by wrapper to avoid copy.
* Gate HDR/10-bit behind `--hdr on` (if the chosen NDI wrapper supports it).

### F. API surface

* `POST /refresh` → reload
* `POST /eval { script }` → JS in page
* `GET /stats` → fps, queue depth, drops/repeats
* `POST /screenshot` → PNG (with alpha)
* `POST /fps { n, d }` → set fractional fps at runtime (reinit pacer)

All extensions are compatible with the current architecture. 

---

## 8) Minimal route/example stubs (paste then align to actual types)

**POST /seturl**

```csharp
app.MapPost("/seturl", async (GoToUrlModel req, BrowserWrapper browser) =>
{
    await browser.SetUrlAsync(req.Url);
    return Results.Ok();
});
```

(Existing behavior; confirm model name and DI style.) 

**KVM click injection (concept)**

```csharp
// From KVM thread after decoding 0x04/0x07 and scaling normalized coords:
browser.Click(screenX, screenY); // already present; extend with move/keys
```

(Subset already implemented: move, left down, left up.) 

---

## 9) Validation checklist (post-change)

* **Alpha:** load transparent test page → verify alpha in NDI receiver (checkerboard). 
* **Pacing:** measure output inter-frame ~**33.366 ms** (29.97p) or **16.683 ms** (59.94p) over ≥10 minutes; report drops/repeats.
* **Stress:** heavy WebGL2 animation: queue depth 1–4, no visible stutter for 10 min.
* **Audio:** PCM from page reaches NDI; no drift vs video. 
* **API:** `/seturl`, `/refresh`, `/size`, `/fps`, `/stats` work and are documented in `.http`.

---

## 10) Appendices for agents to fill after first scan

> Do a quick repo crawl and complete these summaries for faster future edits.

### /Chromium index (fill)

* `Chromium/<file>.cs` — classes: … — responsibilities: … — key methods: … — **signals → NDI**: …

### /Models index (fill)

* `Models/<file>.cs` — DTO: … (properties …) — used by route …

### NDI path

* NDI wrapper type(s): … — creation: … — send call(s): … — pixel format: … — **frame_rate_N/D** hookup: …

### Known TODOs in code

* …

---

## 11) Notes for LLM agents

* Keep edits **surgical**; preserve existing behavior unless gated by new CLI flags.
* Prefer **BGRA direct send** if possible to eliminate RGBA conversion.
* Treat WebGPU as **opt-in experimental**; keep WebGL2 as the default baseline.
* Always add **plain-English comments** near timing/pacing code.
* Log queue depth, send cadence, and drops/repeats; expose via `/stats`.

---

## 12) Provenance / references (for maintainers)

* Architecture & behavior summary (merged into this doc from the supplied analysis of the repo). 
* Public repo (README with usage/limits):
  [https://github.com/tractusevents/Tractus.HtmlToNdi](https://github.com/tractusevents/Tractus.HtmlToNdi)

**End of AGENTS.md**
