# Tractus HTML to NDI Utility

A simple wrapper around [CEFSharp](https://github.com/cefsharp/CefSharp) and [NDI Wrapper for .NET](https://github.com/eliaspuurunen/NdiLibDotNetCoreBase). Sends HTML pages to NDI, including audio if playback on the page is supported.

[Grab the latest ZIP file](https://github.com/tractusevents/Tractus.HtmlToNdi/releases) from the releases page.

## Usage

Launch as-is for a 1920x1080 browser instance. The app will ask you for a source name if one is not provided on the command line.

If the web page you are loading has a transparent background, NDI will honor that transparency.

## Command Line Parameters

Parameter|Description
----|---
`--ndiname="NDI Source Name"`|Sets the NDI source name. Defaults to `HTML5` if omitted.
`--w=1920`|The width of the browser surface in pixels. Defaults to `1920`.
`--h=1080`|The height of the browser surface in pixels. Defaults to `1080`.
`--port=9999`|Port for the HTTP control API. When omitted the app will prompt.
`--url="https://www.tractus.ca"`|The startup webpage. Defaults to `https://testpattern.tractusevents.com/`.
`--fps=59.94`|Target NDI frame rate. Accepts decimals (`59.94`) or rationals (`60000/1001`). Defaults to `60`.
`--buffer-depth=3`|Enables the paced output buffer with the specified depth (frames). Set to `0` or omit to disable buffering.
`--enable-output-buffer`|Shorthand to enable buffering with a default depth of `3` frames.
`--windowless-frame-rate=60`|Overrides Chromium's internal frame pump. Defaults to the rounded `--fps` value.
`--disable-vsync`|Passes `--disable-gpu-vsync` to Chromium for workloads that prefer uncapped rendering.

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
- When buffering is enabled the app logs pacing metrics (measured fps, drops, repeats) and publishes them as NDI metadata.

## More Tools

We have more tools available at [our website, tractusevents.com](https://www.tractusevents.com/tools).
