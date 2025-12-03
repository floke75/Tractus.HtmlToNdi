# Upgrading CefSharp / Chromium builds

These steps assume you are on Windows with the .NET 8 SDK (including the Windows desktop workload) and you are in the repository root next to `Tractus.HtmlToNdi.sln`.

## Pick the right package
- **Use `CefSharp.OffScreen.NETCore` (NuGet)**. This package targets modern .NET (including net8.0-windows) and pulls the matching Chromium/CEF runtime automatically.
- **Do NOT use `CefSharp.OffScreen` (the .NET Framework package)** for this project. It targets .NET Framework only; restoring it on net8.0-windows produces NU1701 warnings and missing-type build errors such as `IBrowserHost`, `IWebBrowser`, and `AudioParameters`.
- Stick to released versions from NuGet. MyGet CI builds (e.g., `142.0.150-CI...`) often lack the `*.NETCore` variants and are not tested here.

## Step-by-step upgrade
1. **Open a terminal in the repo root** (the folder containing `Tractus.HtmlToNdi.sln`).
2. **Restore the currently pinned packages** (optional sanity check):
   ```powershell
   dotnet restore
   ```
3. **Edit the package version** in `Tractus.HtmlToNdi.csproj`:
   - Locate the line `<PackageReference Include="CefSharp.OffScreen.NETCore" Version="129.0.110" />`.
   - Replace `129.0.110` with the desired released version number (for example, `131.0.1`).
   - Save the file.
4. **Restore with the new version** to download the Chromium/CEF payload that matches that build:
   ```powershell
   dotnet restore
   ```
   If you see NU1701 warnings, you picked the wrong package (likely `CefSharp.OffScreen` instead of `CefSharp.OffScreen.NETCore`).
5. **Build to confirm the upgrade works**:
   ```powershell
   dotnet build Tractus.HtmlToNdi.sln
   ```
   A clean build with no missing-type errors (`IBrowserHost`, `IWebBrowser`, etc.) indicates the correct package is referenced.
6. **Publish for distribution (optional)**:
   ```powershell
   dotnet publish Tractus.HtmlToNdi.csproj -c Release -r win-x64 --self-contained false
   ```
   The publish output under `bin/Release/net8.0-windows/win-x64/publish/` will include the updated CefSharp/Chromium runtime files.

## Recovering from a broken upgrade
If you accidentally installed the .NET Framework package or a CI build and now see NU1701 warnings or missing CefSharp types:
1. Edit `Tractus.HtmlToNdi.csproj` and change the reference back to `CefSharp.OffScreen.NETCore` with a released version.
2. Delete `bin` and `obj` folders (optional cleanup).
3. Run `dotnet restore` followed by `dotnet build Tractus.HtmlToNdi.sln`.

Following these steps keeps the project on a Chromium build that is compatible with its net8.0-windows target without any manual Chromium download or compilation.
