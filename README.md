# Tractus HTML to NDI Utility

A simple wrapper around [CEFSharp](https://github.com/cefsharp/CefSharp) and [NDI Wrapper for .NET](https://github.com/eliaspuurunen/NdiLibDotNetCoreBase). Sends HTML pages to NDI, including audio if playback on the page is supported.

[Grab the latest ZIP file](https://github.com/tractusevents/Tractus.HtmlToNdi/releases) from the releases page.

## Usage

Launch as-is for a 1920x1080 browser instance. The app will ask you for a source name if one is not provided on the command line.

If the web page you are loading has a transparent background, NDI will honor that transparency.

## Command Line Parameters

Parameter|Description
----|---
`--ndiname="NDI Source Name"`|The source name this browser instance will send. Defaults to `HTML5`.
`--w=1920`|The width of the browser source in pixels. Defaults to `1920`.
`--h=1080`|The height of the browser source in pixels. Defaults to `1080`.
`--port=9999`|The port the HTTP server will listen on. Defaults to `9999`.
`--url="https://www.tractus.ca"`|The startup webpage. Defaults to `https://testpattern.tractusevents.com/`.
`--target-fps=29.97`|Target NDI frame rate for the pacer. Defaults to `29.97` fps and is exposed as a rational (`30000/1001`).
`--buffer-depth=3`|Number of frames held in the pacing ring buffer. Higher values add latency but smooth bursty rendering.
`--windowless-frame-rate=60`|Chromium windowless frame rate cap. Keep this at or above the target FPS; defaults to `60`.
`--disable-gpu-vsync`|Adds the Chromium `--disable-gpu-vsync` flag to minimise host vsync interference.
`--disable-frame-rate-limit`|Adds the Chromium `--disable-frame-rate-limit` flag so Chromium can render as fast as possible.

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
- The pacer defaults to 29.97 fps output with a three-frame ring buffer. Raising the buffer depth increases latency but helps when Chromium delivers bursts of frames.

## More Tools

We have more tools available at [our website, tractusevents.com](https://www.tractusevents.com/tools).
