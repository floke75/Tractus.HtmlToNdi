# CompositorCapture native helper

This Visual C++ project hosts the native helper that proxies compositor frames back to the managed layer. The exported C ABI matches the `CompositorCaptureBridge` managed wrapper in `Native/CompositorCaptureBridge.cs`.

The implementation is intentionally lightweight—the helper instantiates a `viz::FrameSinkVideoCapturer` when Chromium's compositor infrastructure is available (guarded with `__has_include` so the project still builds on developer machines that do not have the Chromium headers installed yet). In test-only builds the helper falls back to a stub so the managed bridge can still be exercised.

Chromium's begin-frame scheduling toggles remain on the managed side because CefSharp does not expose a supported way to marshal a `CefBrowserHost*`. `CefWrapper` disables auto begin frames before creating a compositor session and restores the setting when the experiment shuts down.

> **Build note:** add this project to the Visual Studio solution when producing signed builds. The managed application expects the resulting `CompositorCapture.dll` to sit alongside `Tractus.HtmlToNdi.exe`.
