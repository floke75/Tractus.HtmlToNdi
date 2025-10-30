# CompositorCapture native helper

This Visual C++ project hosts the native helper that disables Chromium's auto begin-frame scheduling and proxies compositor frames back to the managed layer. The exported C ABI matches the `CompositorCaptureBridge` managed wrapper in `Native/CompositorCaptureBridge.cs`.

The implementation is intentionally lightweight—the helper grabs the `CefBrowserHost`, calls `SetAutoBeginFrameEnabled(false)`, and instantiates a `viz::FrameSinkVideoCapturer` when Chromium's compositor infrastructure is available. The capturer hookup is guarded with `__has_include` so the project still builds on developer machines that do not have the Chromium headers installed yet; in that configuration the helper compiles against a stub implementation so the managed bridge can be exercised in unit tests.

> **Build note:** add this project to the Visual Studio solution when producing signed builds. The managed application expects the resulting `CompositorCapture.dll` to sit alongside `Tractus.HtmlToNdi.exe`.
