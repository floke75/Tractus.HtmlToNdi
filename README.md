# Tractus HTML to NDI Utility

A simple wrapper around [CEFSharp](https://github.com/cefsharp/CefSharp) and [NDI Wrapper for .NET](https://github.com/eliaspuurunen/NdiLibDotNetCoreBase). Sends HTML pages to NDI, including audio if playback on the page is supported.

[Grab the latest ZIP file](https://github.com/tractusevents/Tractus.HtmlToNdi/releases) from the releases page.

## Usage

Launch as-is for a 1920x1080 browser instance. The app will ask you for a source name if one is not provided on the command line.

If the web page you are loading has a transparent background, NDI will honor that transparency.

## Command Line Parameters

Parameter|Description
----|---
`--ndiname="NDI Source Name"`|The source name this browser instance will send. Defaults to "`HTML5`".
`--w=1920`|The width of the browser source. Defaults to `1920`.
`--h=1080`|The height of the browser source. Defaults to `1080`.
`--port=9999`|The port the HTTP server will listen on. Defaults to `9999`.
`--url="https://www.tractus.ca"`|The startup webpage. Defaults to `https://testpattern.tractusevents.com/`.
`--fps=30000/1001`|Target NDI output frame rate. Accepts ratios (e.g. `30000/1001`) or decimals (e.g. `29.97`). Defaults to ~29.97 fps.
`--buffer-depth=5`|Number of frames retained in the pacing buffer. Higher values smooth bursts at the cost of latency. Defaults to `5`.
`--disable-gpu-vsync`|Passes `--disable-gpu-vsync` to Chromium. Useful when the host GPU forces vsync.
`--disable-frame-rate-limit`|Passes `--disable-frame-rate-limit` to Chromium. Allows Chromium to render as quickly as possible.

#### Example Launch

`.\Tractus.HtmlToNdi.exe --ndiname="HTML 5 Test" --w=1080 --h=1080 --fps=30000/1001 --buffer-depth=5 --url="https://testpattern.tractusevents.com"`

## Frame pacing and telemetry

Chromium paint callbacks enqueue frames into a fixed-size ring buffer. A dedicated pacing thread wakes at the configured frame interval and submits the latest frame to NDI, repeating the last frame if Chromium has not produced a new one. Serilog logs aggregate stats (average interval, min/max jitter, repeated and dropped counts) once per second by default—watch the console or log file to validate pacing performance.

## API Routes

Route|Method|Description|Example
----|----|----|---
`/seturl`|`POST`|Sets the URL for this instance.|```{"url": "https://www.google.ca"}```

## Known Limitations

- Frames are sent to NDI in RGBA format. Some machines may experience a slight performance penalty.
- H.264 and any other non-free codecs are not available for video playback since this uses Chromium. Sites like YouTube likely won't work.
- Audio data received from the browser is passed to NDI directly.
- Video frames are paced to the configured frame rate (default 30000/1001). Increase `--buffer-depth` if receivers report repeated frames during bursty rendering.

## More Tools

We have more tools available at [our website, tractusevents.com](https://www.tractusevents.com/tools).
