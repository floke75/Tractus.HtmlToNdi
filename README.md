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
`--fps=59.94`|Target NDI frame rate. Accepts integers, decimals (e.g. `59.94`) or rationals (`60000/1001`). Defaults to `60/1`.
`--windowless-frame-rate=60`|Overrides Chromium's internal paint rate without changing output pacing. Defaults to the value supplied to `--fps`.
`--enable-output-buffer`|Enables the paced buffering pipeline for smoother cadence when Chromium falls behind. Disabled by default.
`--buffer-depth=3`|Number of frames the output buffer can hold before dropping the oldest frame. Supplying a value automatically enables buffering.
`--disable-vsync`|Adds the `--disable-gpu-vsync` Chromium switch for cases where the GPU driver imposes a sync limit.

#### Example Launch

`.\Tractus.HtmlToNdi.exe --ndiname="HTML 5 Test" --w=1080 --h=1080 --url="https://testpattern.tractusevents.com" --fps=60000/1001 --enable-output-buffer --buffer-depth=4`

## API Routes

Route|Method|Description|Example
----|----|----|---
`/seturl`|`POST`|Sets the URL for this instance.|```{"url": "https://www.google.ca"}```

## Known Limitations

- Frames are sent to NDI in RGBA format. Some machines may experience a slight performance penalty.
- H.264 and any other non-free codecs are not available for video playback since this uses Chromium. Sites like YouTube likely won't work.
- Audio data received from the browser is passed to NDI directly.
- When buffering is disabled, frames are forwarded directly from Chromium to NDI (zero-copy). When enabled, frames are copied into a paced buffer that drops the oldest frame when full and repeats the last frame during underruns.

## More Tools

We have more tools available at [our website, tractusevents.com](https://www.tractusevents.com/tools).
