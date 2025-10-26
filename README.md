# Tractus HTML to NDI Utility

A simple wrapper around [CEFSharp](https://github.com/cefsharp/CefSharp) and [NDI Wrapper for .NET](https://github.com/eliaspuurunen/NdiLibDotNetCoreBase). Sends HTML pages to NDI, including audio if playback on the page is supported.

[Grab the latest ZIP file](https://github.com/tractusevents/Tractus.HtmlToNdi/releases) from the releases page.

## Purpose

This Windows-only utility renders a Chromium page off-screen and publishes its video and audio over the NDI protocol. It also provides a minimal HTTP control API for interaction, allowing you to change the URL, scroll, click, and type keystrokes.

## Prerequisites

- Install the NDI Runtime (NDI® 6 or later). The app automatically scans common locations such as `C:\Program Files\NDI\NDI 6 Runtime\v6` and `...\NDI 6 SDK\Bin\x64`, and it honours the `NDILIB_REDIST_FOLDER` environment variable. If the runtime is not installed, copy `Processing.NDI.Lib.x64.dll` next to `Tractus.HtmlToNdi.exe` before launching.

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
`--telemetry-interval=10`|Seconds between video pipeline telemetry log entries. Defaults to 10 seconds.
`--windowless-frame-rate=60`|Overrides CEF's internal repaint cadence. Defaults to the nearest integer of `--fps`.
`--disable-gpu-vsync`|Disables Chromium's GPU vsync throttling.
`--disable-frame-rate-limit`|Disables Chromium's frame rate limiter.
`--launcher`|Forces the launcher window to appear even when other parameters are supplied.
`--no-launcher`|Skips the launcher and honours the supplied command-line arguments only.

When the paced buffer is enabled the pipeline repeats the most recently transmitted frame while warming up or recovering from an underrun so receivers continue to see a stable cadence. Passing `--allow-latency-expansion` switches that recovery into a variable-latency mode that keeps playing any queued frames before falling back to repeats, smoothing out motion at the cost of temporary additional delay. See [`Docs/paced-output-buffer.md`](Docs/paced-output-buffer.md) for a deeper walkthrough of the priming and telemetry behaviour.

#### Example Launch

`.\Tractus.HtmlToNdi.exe --ndiname="HTML 5 Test" --w=1080 --h=1080 --url="https://testpattern.tractusevents.com"`

## API Routes

The application exposes a minimal HTTP API for controlling the browser instance.

| Route | Method | Payload | Effect |
| --- | --- | --- | --- |
| `/seturl` | POST | JSON `{ "url": "https://..." }` | Navigates the browser to the specified URL. |
| `/scroll/{increment}` | GET | Path `increment` (integer) | Scrolls the page vertically. Positive values scroll down, negative values scroll up. |
| `/click/{x}/{y}` | GET | Path `x`, `y` (integer pixels) | Simulates a left mouse click at the specified coordinates. |
| `/keystroke` | POST | JSON `{ "toSend": "..." }` | Sends a sequence of keystrokes to the browser. |
| `/type/{toType}` | GET | Path string | A convenience endpoint for sending keystrokes via a GET request. |
| `/refresh` | GET | none | Refreshes the current page. |

## Known Limitations

- **Single Instance:** The application is designed to run a single browser instance.
- **No Authentication:** The HTTP API has no authentication. It should not be exposed to untrusted networks.
- **Input Fidelity:** Keystroke simulation only sends `KeyDown` events, without `KeyUp` or modifier keys. Mouse clicks are limited to the left button.
- **Codec Support:** The underlying Chromium build does not include proprietary codecs, so sites that rely on H.264 or other licensed codecs may not play video correctly.
- **Audio Layout:** The audio is sent to NDI in a pseudo-planar format, which may not be compatible with all NDI receivers.
- **Resource Cleanup:** NDI and CefSharp resources are not explicitly shut down; the application relies on the operating system to clean them up on process exit.

## More Tools

We have more tools available at [our website, tractusevents.com](https://www.tractusevents.com/tools).
