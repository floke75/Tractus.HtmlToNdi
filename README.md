# Tractus HTML to NDI Utility

A simple wrapper around [CEFSharp](https://github.com/cefsharp/CefSharp) and [NDI Wrapper for .NET](https://github.com/eliaspuurunen/NdiLibDotNetCoreBase). Sends HTML pages to NDI, including audio if playback on the page is supported.

[Grab the latest ZIP file](https://github.com/tractusevents/Tractus.HtmlToNdi/releases) from the releases page.

## Prerequisites

- No external NDI Runtime installation is required for typical deployments—the ZIP now ships with `Processing.NDI.Lib.x64.dll` under `runtimes/win-x64/native/` and the app prefers that bundled copy automatically.
- If you prefer to use an existing runtime (or need to update the DLL independently), install the NDI Runtime (NDI® 6 or later). The app still scans common locations such as `C:\Program Files\NDI\NDI 6 Runtime\v6` and `...\NDI 6 SDK\Bin\x64`, and it honours the `NDILIB_REDIST_FOLDER` environment variable.

## Building from source

See [`Docs/building.md`](Docs/building.md) for detailed instructions. The
project targets **.NET 8.0 (windows)** and requires the Windows Desktop
reference packs, so make sure the .NET SDK you install includes the "Windows
desktop" workload before compiling.

## Usage

Launching the executable without command-line parameters now opens a simple launcher window. The launcher loads the most recent settings, lets you tweak NDI, HTTP and rendering options, and starts the application when you press **Launch**. Settings are written to `launcher-settings.json` beside the executable and reused next time you open the tool.

If the web page you are loading has a transparent background, NDI will honor that transparency.

## Command Line Parameters

Parameter|Description
----|---
`--ndiname="NDI Source Name"`|The source name this browser instance will send. Defaults to "`HTML5`".
`--w=1920`|The width of the browser source. Defaults to `1920`.
`--h=1080`|The height of the browser source. Defaults to `1080`.
`--port=9999`|The port the HTTP server will listen on. Defaults to `9999`.
`--url="https://www.tractus.ca"`|The startup webpage. Defaults to `https://testpattern.tractusevents.com/`.
`--fps=59.94`|Target NDI frame rate. Accepts integer, decimal or rational values (e.g. `60000/1001`). Defaults to `60`.
`--buffer-depth=3`|Enable the paced output buffer with the specified frame capacity. When enabled the sender waits for the queue to hold `depth` frames before transmitting, adding roughly `depth / fps` seconds of intentional latency. Set to `0` (default) to run zero-copy.
`--enable-output-buffer`|Shortcut to turn on paced buffering with the default depth of 3 frames (≈`3 / fps` seconds of latency once primed).
`--allow-latency-expansion`|Let the paced buffer keep playing any queued frames during recovery instead of immediately repeating the last frame. This trades temporary extra latency for smoother motion after underruns.
`--disable-capture-alignment`|Turns off the paced sender’s capture timestamp alignment (enabled by default). Use `--align-with-capture-timestamps` to explicitly re-enable it for a specific run.
`--disable-cadence-telemetry`|Suppresses the capture/output cadence jitter metrics in telemetry logs (enabled by default). Use `--enable-cadence-telemetry` to force-enable them when needed.
`--enable-paced-invalidation` / `--disable-paced-invalidation`|Ties Chromium invalidation to the paced sender so no more than one capture runs per send slot, even when the paced buffer is disabled. Defaults to disabled.
`--enable-capture-backpressure` / `--disable-capture-backpressure`|Pauses Chromium invalidation while the paced buffer is above its high-water mark, resuming automatically once depth settles. Requires `--enable-paced-invalidation`; when pacing is off the backpressure toggle is ignored. Defaults to disabled.
`--enable-pump-cadence-adaptation` / `--disable-pump-cadence-adaptation`|Allows the invalidation scheduler to stretch or delay Chromium renders using capture/output drift telemetry. Defaults to disabled.
`--telemetry-interval=10`|Seconds between video pipeline telemetry log entries. Defaults to 10 seconds.
`--windowless-frame-rate=60`|Overrides CEF's internal repaint cadence. Defaults to the nearest integer of `--fps`.
`--disable-gpu-vsync`|Disables Chromium's GPU vsync throttling.
`--disable-frame-rate-limit`|Disables Chromium's frame rate limiter.
`--launcher`|Forces the launcher window to appear even when other parameters are supplied.
`--no-launcher`|Skips the launcher and honours the supplied command-line arguments only.

When the paced buffer is enabled the pipeline repeats the most recently transmitted frame while warming up or recovering from an underrun so receivers continue to see a stable cadence. Passing `--allow-latency-expansion` switches that recovery into a variable-latency mode that keeps playing any queued frames before falling back to repeats, smoothing out motion at the cost of temporary additional delay. The launcher exposes checkboxes for latency expansion, paced invalidation, capture backpressure, pump cadence adaptation, capture alignment, and cadence telemetry so operators can toggle those behaviours without touching the command line. See [`Docs/paced-output-buffer.md`](Docs/paced-output-buffer.md) for a deeper walkthrough of the priming and telemetry behaviour.

### Pacing, invalidation, and backpressure

Chromium renders are now driven by a pacing-aware scheduler that coordinates invalidations with the paced output buffer. When `--enable-paced-invalidation` is set the scheduler runs Chromium in on-demand mode so each send slot triggers at most one capture. The same scheduler feeds cadence adaptation (when `--enable-pump-cadence-adaptation` is active), allowing Chromium to stretch or delay invalidations slightly so capture stays aligned with the paced sender. Capture backpressure (`--enable-capture-backpressure`) piggybacks on this scheduler: the capture gate pauses invalidations while the buffer sits above its high-water mark and resumes them automatically once depth settles. Because backpressure depends on paced invalidation, the pipeline ignores the toggle (and the launcher clears the checkbox) whenever pacing is off.

The invalidation scheduler tracks outstanding requests with short-lived tickets so backpressure, direct pacing, and buffered pacing all share a single pending-slot counter. Tickets are automatically discarded once the corresponding paint arrives, a request is cancelled, or the timeout elapses. During Chromium stalls the timeout handler now trims expired tickets before scheduling follow-up invalidations, preventing the queue from growing unbounded while capture is paused. Direct paced sends also run a watchdog that periodically tops up pending requests so Chromium keeps rendering even when recent sends have been paused, making the ticket queue resilient to slow receivers. When the paced pipeline resets, the ticket registry is drained under lock and finalized outside the critical section so stale entries cannot accumulate during recovery.

Telemetry reflects the pacing state. Buffer health logs now include `captureGateActive`, `captureGatePauses`, and `captureGateResumes` so operators can see when backpressure engaged. Additional fields such as `pacedPaused`, `pacedOffsetMs`, and `cadenceAdaptation` describe how the scheduler is steering Chromium, while `resyncDrops` shows when stale frames were trimmed to get latency back under control.

#### Example Launch

`.\Tractus.HtmlToNdi.exe --ndiname="HTML 5 Test" --w=1080 --h=1080 --url="https://testpattern.tractusevents.com"`

## API Routes

Route|Method|Description|Example
----|----|----|---
`/seturl`|`POST`|Sets the URL for this instance.|`{"url": "https://www.google.ca"}`
`/scroll/{increment}`|`GET`|Scrolls the page vertically.|`/scroll/-100` (scrolls up)
`/click/{x}/{y}`|`GET`|Simulates a left mouse click at the specified coordinates.|`/click/100/200`
`/keystroke`|`POST`|Sends a sequence of keystrokes.|`{"toSend": "Hello, world!"}`
`/type/{toType}`|`GET`|A convenience endpoint for sending keystrokes via a GET request.|`/type/Hello%2C%20world%21`
`/refresh`|`GET`|Refreshes the current page.|`/refresh`

## Known Limitations

- Frames are sent to NDI in RGBA format. Some machines may experience a slight performance penalty.
- H.264 and any other non-free codecs are not available for video playback since this uses Chromium. Sites like YouTube likely won't work.
- Audio data received from the browser is passed to NDI directly.
- NDI frame rate defaults to 60 fps but can be overridden with `--fps`. Chromium's internal repaint cadence can be adjusted with `--windowless-frame-rate`.

## More Tools

We have more tools available at [our website, tractusevents.com](https://www.tractusevents.com/tools).
