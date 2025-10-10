# Building Tractus.HtmlToNdi

The application targets **.NET 8.0 (windows)** and depends on the Windows Desktop
reference assemblies because the launcher UI is implemented with Windows Forms.
If those reference packs are missing the build fails with MSBuild error
`MSB4019` complaining about `Microsoft.NET.Sdk.WindowsDesktop.targets`.

Follow the steps below to build the solution from source:

1. **Install the .NET 8 SDK that includes the Windows Desktop packs.**
   * On Windows, install the standard .NET 8 SDK from
     [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0)
     and ensure the "Windows desktop" workload is selected.
   * On Linux or CI environments, use Microsoft's package feed instead of the
     distro-provided SDK so that `Microsoft.NET.Sdk.WindowsDesktop` is
     available. For Ubuntu 24.04 for example:

       ```bash
       wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb
       sudo dpkg -i packages-microsoft-prod.deb
       sudo apt-get update
       sudo apt-get install -y dotnet-sdk-8.0
       ```

     The SDK published by Microsoft ships the Windows Desktop reference packs
     that are required to compile this repository even on non-Windows hosts.

2. **Restore and build the solution:**

   ```bash
   dotnet build Tractus.HtmlToNdi.sln
   ```

3. **Run the unit tests (optional but recommended):**

   ```bash
   dotnet test Tests/Tractus.HtmlToNdi.Tests/Tractus.HtmlToNdi.Tests.csproj
   ```

4. **Publish a runnable binary:**

   ```bash
   dotnet publish Tractus.HtmlToNdi.csproj -c Release -r win-x64 --self-contained false
   ```

   This command produces a Windows x64 executable under
   `bin/Release/net8.0-windows/win-x64/publish/` that depends on the .NET 8
   runtime being present on the target machine.

## Troubleshooting

* `error MSB4019: The imported project ... Microsoft.NET.Sdk.WindowsDesktop.targets was not found`
  – the Windows Desktop reference pack is missing. Install the SDK from
  Microsoft's feed or enable the "Windows desktop" workload in Visual Studio.
* `NDIlib` related `DllNotFoundException` at runtime – install the NDI runtime
  (see [README prerequisites](../README.md#prerequisites)) or copy the native
  DLLs next to the executable.
