# Tractus HTML to NDI Utility

A simple wrapper around [CEFSharp](https://github.com/cefsharp/CefSharp) and [NDI Wrapper for .NET](https://github.com/eliaspuurunen/NdiLibDotNetCoreBase). Sends HTML pages to NDI, including audio if playback on the page is supported.

[Grab the latest ZIP file](https://github.com/tractusevents/Tractus.HtmlToNdi/releases) from the releases page.

## Prerequisites

- Install the NDI Runtime (NDI® 6 or later). The app automatically scans common locations such as `C:\Program Files\NDI\NDI 6 Runtime\v6` and `...\NDI 6 SDK\Bin\x64`, and it honours the `NDILIB_REDIST_FOLDER` environment variable. If the runtime is not installed, copy `Processing.NDI.Lib.x64.dll` next to `Tractus.HtmlToNdi.exe` before launching.

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
`--buffer-depth=3`|Enable the paced output buffer with the specified frame capacity. Set to `0` (default) to run zero-copy.
`--enable-output-buffer`|Shortcut to turn on paced buffering with the default depth of 3 frames.
`--telemetry-interval=10`|Seconds between video pipeline telemetry log entries. Defaults to 10 seconds.
`--windowless-frame-rate=60`|Overrides CEF's internal repaint cadence. Defaults to the nearest integer of `--fps`.
`--disable-gpu-vsync`|Disables Chromium's GPU vsync throttling.
`--disable-frame-rate-limit`|Disables Chromium's frame rate limiter.
`--launcher`|Forces the launcher window to appear even when other parameters are supplied.
`--no-launcher`|Skips the launcher and honours the supplied command-line arguments only.

#### Example Launch

`.\Tractus.HtmlToNdi.exe --ndiname="HTML 5 Test" --w=1080 --h=1080 --url="https://testpattern.tractusevents.com"`

## API Routes

Route|Method|Description|Example
----|----|----|---
`/seturl`|`POST`|Sets the URL for this instance.|```{"url": "https://www.google.ca"}```

## Known Limitations

- Frames are sent to NDI in RGBA format. Some machines may experience a slight performance penalty.
- H.264 and any other non-free codecs are not available for video playback since this uses Chromium. Sites like YouTube likely won't work.
- Audio data received from the browser is passed to NDI directly.
- NDI frame rate defaults to 60 fps but can be overridden with `--fps`. Chromium's internal repaint cadence can be adjusted with `--windowless-frame-rate`.

## More Tools

We have more tools available at [our website, tractusevents.com](https://www.tractusevents.com/tools).
