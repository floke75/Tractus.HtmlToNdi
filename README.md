# Tractus HTML to NDI Utility

A simple wrapper around [CEFSharp](https://github.com/cefsharp/CefSharp) and [NDI Wrapper for .NET](https://github.com/eliaspuurunen/NdiLibDotNetCoreBase). Sends HTML pages to NDI, including audio if playback on the page is supported.

[Grab the latest ZIP file](https://github.com/tractusevents/Tractus.HtmlToNdi/releases) from the releases page.

## Usage

Launch as-is for a 1920x1080 browser instance. The app will ask you for a source name if one is not provided on the command line.

If the web page you are loading has a transparent background, NDI will honor that transparency.

## Command Line Parameters

Parameter|Description
----|---
`--ndiname="NDI Source Name"`|The source name this browser instance will send. Defaults to `HTML5` and will prompt if omitted.
`--port=9999`|The port the HTTP server will listen on. Defaults to `9999`.
`--url="https://www.tractus.ca"`|The startup webpage. Defaults to `https://testpattern.tractusevents.com/`.
`--w=1920`|The width of the browser source in pixels. Defaults to `1920`.
`--h=1080`|The height of the browser source in pixels. Defaults to `1080`.
`--fps=29.97`|Target NDI frame rate. Supports decimal values (e.g. `29.97`) or ratios (e.g. `30000/1001`). Defaults to 29.97 fps.
`--buffer-depth=5`|Number of frames retained in the pacing ring buffer before overwriting. Higher values add latency but improve burst tolerance. Defaults to `5`.
`--windowless-frame-rate=120`|Override Chromium's internal windowless frame rate. By default the app chooses the greater of 60 fps or double the requested NDI rate.
`--disable-gpu-vsync`|Forwarded to Chromium to disable GPU VSync. Useful on machines where Chromium's compositor throttles rendering.
`--disable-frame-rate-limit`|Forwarded to Chromium to disable the renderer's internal frame-rate limiter.

#### Example Launch

`.\Tractus.HtmlToNdi.exe --ndiname="HTML 5 Test" --w=1080 --h=1080 --url="https://testpattern.tractusevents.com" --fps=59.94 --buffer-depth=6`

## API Routes

Route|Method|Description|Example
----|----|----|---
`/seturl`|`POST`|Sets the URL for this instance.|```{"url": "https://www.google.ca"}```

## Known Limitations

- Frames are sent to NDI in BGRA format. Some machines may experience a slight performance penalty.
- H.264 and any other non-free codecs are not available for video playback since this uses Chromium. Sites like YouTube likely won't work.
- Audio data received from the browser is passed to NDI directly.
- Chromium still renders off-screen at the configured windowless rate; choose values carefully to avoid unnecessary CPU usage.

## More Tools

We have more tools available at [our website, tractusevents.com](https://www.tractusevents.com/tools).
