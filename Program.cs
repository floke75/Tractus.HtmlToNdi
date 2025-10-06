using CefSharp;
using CefSharp.OffScreen;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewTek;
using NewTek.NDI;
using Serilog;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tractus.HtmlToNdi.Chromium;
using Tractus.HtmlToNdi.Launcher;
using Tractus.HtmlToNdi.Models;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi;
public class Program
{
    public static nint NdiSenderPtr;
    internal static CefWrapper browserWrapper;

    private static readonly object NdiLibraryLock = new();
    private static bool NdiLibraryConfigured;
    private static string[] NdiLibraryCandidates = Array.Empty<string>();
    private static string[] NdiSearchDirectories = Array.Empty<string>();
    private static readonly Stopwatch StartupStopwatch = Stopwatch.StartNew();
    private static nint NdiNativeLibraryHandle;

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
        {
            var exception = eventArgs.ExceptionObject as Exception;
            if (exception is not null)
            {
                Log.Fatal(exception, "AppDomain unhandled exception (IsTerminating={IsTerminating})", eventArgs.IsTerminating);
            }
            else
            {
                Log.Fatal("AppDomain unhandled exception object: {ExceptionObject} (IsTerminating={IsTerminating})", eventArgs.ExceptionObject, eventArgs.IsTerminating);
            }
        };

        TaskScheduler.UnobservedTaskException += (sender, eventArgs) =>
        {
            Log.Fatal(eventArgs.Exception, "Unobserved task exception captured");
            eventArgs.SetObserved();
        };

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (sender, eventArgs) =>
        {
            Log.Fatal(eventArgs.Exception, "WinForms UI thread exception");
        };

        var sanitizedArgs = RemoveLauncherFlags(args);
        var launchCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", Guid.NewGuid().ToString());

        var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
        Directory.SetCurrentDirectory(exeDirectory);
        AppManagement.Initialize(sanitizedArgs);

        Log.Information("Startup arguments sanitized. Original={OriginalArgs} Sanitized={SanitizedArgs}", string.Join(' ', args), string.Join(' ', sanitizedArgs));

        LaunchParameters? parameters;
        if (ShouldUseLauncher(args))
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var persistedSettings = LauncherSettingsStore.Load();
            using var launcherForm = new LauncherForm(persistedSettings);
            var dialogResult = launcherForm.ShowDialog();
            if (dialogResult != DialogResult.OK || launcherForm.SelectedParameters is null)
            {
                Log.Information("Launcher closed without starting the application.");
                return;
            }

            if (launcherForm.CurrentSettings is not null)
            {
                LauncherSettingsStore.Save(launcherForm.CurrentSettings);
            }

            parameters = launcherForm.SelectedParameters;
            Log.Information("Launcher accepted parameters: {@Parameters}", parameters);
        }
        else
        {
            if (!LaunchParameters.TryFromArgs(sanitizedArgs, out parameters) || parameters is null)
            {
                Log.Error("Failed to parse command-line parameters; exiting early");
                return;
            }

            Log.Information("Command-line parameters parsed: {@Parameters}", parameters);
        }

        try
        {
            RunApplication(parameters, sanitizedArgs, launchCachePath);
        }
        catch (DllNotFoundException ex) when (ex.Message.Contains("NDI", StringComparison.OrdinalIgnoreCase))
        {
            Log.Fatal(ex, "Unable to start because the native NDI runtime was not found. {Reason}", ex.Message);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal exception during startup");
        }
        finally
        {
            Log.Information("Program.Main exiting after {Elapsed}ms", StartupStopwatch.ElapsedMilliseconds);
        }
    }

    private static void RunApplication(LaunchParameters parameters, string[] args, string launchCachePath)
    {
        Log.Information("RunApplication starting with {@Parameters} (cache={CachePath})", parameters, launchCachePath);

        var frameRate = parameters.FrameRate;
        var width = parameters.Width;
        var height = parameters.Height;
        var startUrl = parameters.StartUrl;
        var windowlessFrameRateOverride = parameters.WindowlessFrameRateOverride;

        var enableBuffering = parameters.EnableBuffering;
        var effectiveDepth = enableBuffering ? Math.Max(1, parameters.BufferDepth == 0 ? 3 : parameters.BufferDepth) : 1;

        var pipelineOptions = new NdiVideoPipelineOptions
        {
            EnableBuffering = enableBuffering,
            BufferDepth = effectiveDepth,
            TelemetryInterval = parameters.TelemetryInterval,
        };

        Log.Information("Ensuring NDI native runtime is available...");
        EnsureNdiNativeLibraryLoaded();
        Log.Information("NDI native runtime setup complete");

        var ndiNamePtr = UTF.StringToUtf8(parameters.NdiName);
        try
        {
            var settings_T = new NDIlib.send_create_t
            {
                p_ndi_name = ndiNamePtr
            };

            try
            {
                Program.NdiSenderPtr = NDIlib.send_create(ref settings_T);
            }
            catch (DllNotFoundException ex)
            {
                var message = CreateNdiFailureMessage();
                Log.Fatal(ex, message);
                throw;
            }
        }
        finally
        {
            if (ndiNamePtr != nint.Zero)
            {
                Marshal.FreeHGlobal(ndiNamePtr);
            }
        }

        if (Program.NdiSenderPtr == nint.Zero)
        {
            Log.Error("Failed to create NDI sender. Exiting.");
            return;
        }

        Log.Information("NDI sender created successfully");

        var ndiSender = new NativeNdiVideoSender(Program.NdiSenderPtr);
        var videoPipeline = new NdiVideoPipeline(ndiSender, frameRate, pipelineOptions, Log.Logger);

        try
        {
            Log.Information("Initialising Chromium via AsyncContext");
            AsyncContext.Run(async delegate
            {
                var settings = new CefSettings();
                if (!Directory.Exists(launchCachePath))
                {
                    Directory.CreateDirectory(launchCachePath);
                }

                settings.RootCachePath = launchCachePath;
                settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");

                var targetWindowlessRate = windowlessFrameRateOverride ?? Math.Clamp((int)Math.Round(frameRate.Value), 1, 240);
                settings.CefCommandLineArgs.Add("off-screen-frame-rate", targetWindowlessRate.ToString(CultureInfo.InvariantCulture));

                if (parameters.DisableGpuVsync)
                {
                    settings.CefCommandLineArgs.Add("disable-gpu-vsync", "1");
                }

                if (parameters.DisableFrameRateLimit)
                {
                    settings.CefCommandLineArgs.Add("disable-frame-rate-limit", "1");
                }

                settings.EnableAudio();
                Cef.Initialize(settings);
                browserWrapper = new CefWrapper(
                    width,
                    height,
                    startUrl,
                    videoPipeline,
                    frameRate,
                    Log.Logger,
                    windowlessFrameRateOverride);

                await browserWrapper.InitializeWrapperAsync();
                Log.Information("Chromium initialised successfully");
            });
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to initialize Chromium or the video pipeline.");
            videoPipeline.Dispose();
            return;
        }

        Log.Information("Building ASP.NET Core host on port {Port}", parameters.Port);
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddSerilog();

        builder.WebHost.UseUrls($"http://*:{parameters.Port}");

        // Add services to the container.
        builder.Services.AddAuthorization();

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();
        app.UseSwagger();
        app.UseSwaggerUI();

        var capabilitiesXml = $$"""<ndi_capabilities ntk_kvm="true" />""";
        capabilitiesXml += "\0";
        var capabilitiesPtr = UTF.StringToUtf8(capabilitiesXml);

        var metaframe = new NDIlib.metadata_frame_t()
        {
            p_data = capabilitiesPtr
        };

        NDIlib.send_add_connection_metadata(NdiSenderPtr, ref metaframe);
        Marshal.FreeHGlobal(capabilitiesPtr);

        var running = true;
        var thread = new Thread(() =>
        {
            var metadata = new NDIlib.metadata_frame_t();
            var x = 0.0f;
            var y = 0.0f;
            while (running)
            {
                var result = NDIlib.send_capture(NdiSenderPtr, ref metadata, 1000);

                if (result == NDIlib.frame_type_e.frame_type_none)
                {
                    continue;
                }
                else if (result == NDIlib.frame_type_e.frame_type_metadata)
                {
                    var metadataConverted = UTF.Utf8ToString(metadata.p_data);

                    if(metadataConverted.StartsWith("<ndi_kvm u=\""))
                    {
                        metadataConverted = metadataConverted.Replace("<ndi_kvm u=\"", "");
                        metadataConverted = metadataConverted.Replace("\"/>", "");

                        try
                        {
                            var binary = Convert.FromBase64String(metadataConverted);

                            var opcode = binary[0];

                            if(opcode == 0x03)
                            {
                                x = BitConverter.ToSingle(binary, 1);
                                y = BitConverter.ToSingle(binary, 5);
                            }
                            else if(opcode == 0x04)
                            {
                                // Mouse Left Down
                                var screenX = (int)(x * width);
                                var screenY = (int)(y * height);

                                browserWrapper.Click(screenX, screenY);
                            }
                            else if(opcode == 0x07)
                            {
                                // Mouse Left Up
                            }
                        }
                        catch
                        {

                        }
                    }

                    Log.Logger.Warning("Got metadata: " + metadataConverted);
                    NDIlib.send_free_metadata(NdiSenderPtr, ref metadata);
                }

            }
        });
        thread.Start();


        app.MapPost("/seturl", (HttpContext httpContext, GoToUrlModel url) =>
        {
            browserWrapper.SetUrl(url.Url);
            return true;
        })
        .WithOpenApi();

        app.MapGet("/scroll/{increment}", (int increment) =>
        {
            browserWrapper.ScrollBy(increment);
        }).WithOpenApi();

        app.MapGet("/click/{x}/{y}", (int x, int y) =>
        {
            browserWrapper.Click(x, y);
        }).WithOpenApi();

        app.MapPost("/keystroke", (SendKeystrokeModel model) =>
        {
            browserWrapper.SendKeystrokes(model);
        }).WithOpenApi();

        app.MapGet("/type/{toType}", (string toType) =>
        {
            browserWrapper.SendKeystrokes(new SendKeystrokeModel
            {
                ToSend = toType
            });
        }).WithOpenApi();

        app.MapGet("/refresh", () =>
        {
            browserWrapper.RefreshPage();
        }).WithOpenApi();

        Log.Information("Starting ASP.NET Core host");
        app.Run();
        Log.Information("ASP.NET Core host exited");

        running = false;
        thread.Join();
        browserWrapper.Dispose();

        if (Directory.Exists(launchCachePath))
        {
            try
            {
                Directory.Delete(launchCachePath, true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to delete launch cache {CachePath}", launchCachePath);
            }
        }

        Log.Information("RunApplication completed after {Elapsed}ms", StartupStopwatch.ElapsedMilliseconds);
    }

    private static void EnsureNdiNativeLibraryLoaded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (NdiLibraryLock)
        {
            if (NdiLibraryConfigured)
            {
                return;
            }

            NdiSearchDirectories = BuildNdiProbeDirectories();
            NdiLibraryCandidates = BuildNdiCandidateFiles(NdiSearchDirectories);
            Log.Information("NDI probe directories: {Directories}", NdiSearchDirectories);
            Log.Information("NDI library candidates: {Candidates}", NdiLibraryCandidates);

            if (NdiLibraryCandidates.Length == 0)
            {
                throw new DllNotFoundException(CreateNdiFailureMessage());
            }

            static bool IsManagedWrapper(string path)
            {
                var fileName = Path.GetFileName(path);
                return fileName.Contains("DotNet", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith("DotNetCore.dll", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith("DotNetCoreBase.dll", StringComparison.OrdinalIgnoreCase);
            }

            var nativeCandidates = NdiLibraryCandidates
                .Where(path => !IsManagedWrapper(path))
                .ToArray();

            if (nativeCandidates.Length == 0)
            {
                nativeCandidates = NdiLibraryCandidates;
            }

            var archSuffix = Environment.Is64BitProcess ? "x64" : "x86";
            var preferredNames = new[]
            {
                $"Processing.NDI.Lib.{archSuffix}.dll",
                $"NDIlib.{archSuffix}.dll",
                "NDIlib.dll"
            };

            string? SelectPreferredCandidate(IEnumerable<string> candidates)
            {
                foreach (var preferredName in preferredNames)
                {
                    var match = candidates.FirstOrDefault(path =>
                        string.Equals(Path.GetFileName(path), preferredName, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        return match;
                    }
                }

                return candidates.FirstOrDefault();
            }

            var selectedLibrary = SelectPreferredCandidate(nativeCandidates);

            if (string.IsNullOrWhiteSpace(selectedLibrary))
            {
                throw new DllNotFoundException(CreateNdiFailureMessage());
            }

            var runtimeDirectory = Path.GetDirectoryName(selectedLibrary);
            if (string.IsNullOrWhiteSpace(runtimeDirectory))
            {
                throw new DllNotFoundException(CreateNdiFailureMessage());
            }

            runtimeDirectory = Path.GetFullPath(runtimeDirectory);
            Log.Information("Using NDI runtime directory {RuntimeDirectory}", runtimeDirectory);
            Log.Information("Selected NDI native library {Library}", selectedLibrary);
            Environment.SetEnvironmentVariable("NDILIB_REDIST_FOLDER", runtimeDirectory);

            TryPrependToPath(runtimeDirectory);

            try
            {
                if (!SetDllDirectory(runtimeDirectory))
                {
                    var error = Marshal.GetLastWin32Error();
                    Log.Warning("SetDllDirectory failed for {Path} with error {Error}", runtimeDirectory, error);
                }
            }
            catch (EntryPointNotFoundException)
            {
                // Older Windows versions may not expose SetDllDirectory; ignore.
            }

            if (NativeLibrary.TryLoad(selectedLibrary, out var handle))
            {
                NdiNativeLibraryHandle = handle;
                Log.Information("Loaded native NDI library from {Library}", selectedLibrary);
            }
            else
            {
                Log.Warning("NativeLibrary.TryLoad was unable to load {Library}; relying on default probing.", selectedLibrary);
            }

            try
            {
                var versionPtr = NDIlib.version();
                var version = Marshal.PtrToStringAnsi(versionPtr);
                if (!string.IsNullOrWhiteSpace(version))
                {
                    Log.Information("Detected NDI runtime {Version}", version);
                }
            }
            catch (DllNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Unable to query NDI runtime version");
            }

            NdiLibraryConfigured = true;
        }
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);

    private static void TryPrependToPath(string directory)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var paths = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (!paths.Any(p => string.Equals(p.Trim(), directory, StringComparison.OrdinalIgnoreCase)))
        {
            var updated = string.IsNullOrEmpty(current)
                ? directory
                : directory + Path.PathSeparator + current;

            Environment.SetEnvironmentVariable("PATH", updated);
        }
    }

    private static string[] BuildNdiProbeDirectories()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfExists(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(path);
                if (Directory.Exists(full))
                {
                    directories.Add(full);
                }
            }
            catch
            {
                // ignore malformed paths
            }
        }

        AddIfExists(AppContext.BaseDirectory);
        AddIfExists(Path.Combine(AppContext.BaseDirectory, Environment.Is64BitProcess ? "x64" : "x86"));
        AddIfExists(Path.Combine(AppContext.BaseDirectory, "runtimes", Environment.Is64BitProcess ? "win-x64" : "win-x86", "native"));

        var envVars = new[]
        {
            "NDILIB_REDIST_FOLDER",
            "NDI_RUNTIME_DIR",
            "NDI_RUNTIME_DIR_V6",
            "NDI_SDK_DIR",
            "NDI_SDK",
            "NDI_LIB_PATH",
        };

        foreach (var envVar in envVars)
        {
            AddIfExists(Environment.GetEnvironmentVariable(envVar));
        }

        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var ndiRoot = Path.Combine(root, "NDI");
            AddIfExists(ndiRoot);

            if (Directory.Exists(ndiRoot))
            {
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(ndiRoot))
                    {
                        AddIfExists(sub);
                        AddIfExists(Path.Combine(sub, "v6"));
                        AddIfExists(Path.Combine(sub, "Runtime"));
                        AddIfExists(Path.Combine(sub, "Bin"));
                        AddIfExists(Path.Combine(sub, "Bin", Environment.Is64BitProcess ? "x64" : "x86"));
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Failed to enumerate NDI install directories under {Root}", ndiRoot);
                }
            }

            AddIfExists(Path.Combine(root, "NDI", "NDI 6 Runtime", "v6"));
            AddIfExists(Path.Combine(root, "NDI", "NDI 6 SDK"));
            AddIfExists(Path.Combine(root, "NDI", "NDI 6 SDK", "Bin"));
            AddIfExists(Path.Combine(root, "NDI", "NDI 6 SDK", "Bin", Environment.Is64BitProcess ? "x64" : "x86"));
            AddIfExists(Path.Combine(root, "NDI", "NDI 6 Tools", "Runtime"));
        }

        return directories.ToArray();
    }

    private static string[] BuildNdiCandidateFiles(string[] directories)
    {
        var archSuffix = Environment.Is64BitProcess ? "x64" : "x86";
        var fileNames = new[]
        {
            "NDILib.dll",
            $"NDILib.{archSuffix}.dll",
            $"Processing.NDI.Lib.{archSuffix}.dll",
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void AddCandidate(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(path);
                if (File.Exists(full) && seen.Add(full))
                {
                    result.Add(full);
                }
            }
            catch
            {
            }
        }

        foreach (var directory in directories)
        {
            foreach (var name in fileNames)
            {
                AddCandidate(Path.Combine(directory, name));
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, $"Processing.NDI.Lib.{archSuffix}.dll", SearchOption.AllDirectories))
                {
                    AddCandidate(file);
                }

                foreach (var file in Directory.EnumerateFiles(directory, "NDILib*.dll", SearchOption.AllDirectories))
                {
                    AddCandidate(file);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to enumerate native NDI libraries under {Directory}", directory);
            }
        }

        // As a final fallback, check for a sibling Processing.NDI.Lib.* next to the executable even if the directory was not captured above.
        foreach (var name in fileNames)
        {
            AddCandidate(Path.Combine(AppContext.BaseDirectory, name));
        }

        return result.ToArray();
    }

    private static string CreateNdiFailureMessage()
    {
        var builder = new StringBuilder();
        builder.Append("Unable to locate the native NDI runtime (expecting NDILib.dll or Processing.NDI.Lib.*). ");

        if (NdiLibraryCandidates.Length > 0)
        {
            builder.Append("Attempted paths: ");
            builder.Append(string.Join(", ", NdiLibraryCandidates));
            builder.Append(". ");
        }

        if (NdiSearchDirectories.Length > 0)
        {
            builder.Append("Searched directories: ");
            builder.Append(string.Join(", ", NdiSearchDirectories));
            builder.Append(". ");
        }

        builder.Append("Install the NDI Runtime (for example, \"C\\Program Files\\NDI\\NDI 6 Runtime\\v6\") or copy Processing.NDI.Lib.x64.dll next to Tractus.HtmlToNdi.exe, then restart the application. ");
        builder.Append("You can also set the NDILIB_REDIST_FOLDER environment variable to the folder that contains the native library.");

        return builder.ToString();
    }

    private static string[] RemoveLauncherFlags(string[] args)
        => args.Where(a => !string.Equals(a, "--launcher", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(a, "--no-launcher", StringComparison.OrdinalIgnoreCase)).ToArray();

    private static bool ShouldUseLauncher(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--launcher", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (args.Any(a => string.Equals(a, "--no-launcher", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (args.Length == 0)
        {
            return true;
        }

        foreach (var arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var switchName = arg.Split('=', 2)[0];
            if (ConfigurationSwitches.Contains(switchName))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly HashSet<string> ConfigurationSwitches = new(StringComparer.Ordinal)
    {
        "--ndiname",
        "--port",
        "--url",
        "--w",
        "--h",
        "--fps",
        "--buffer-depth",
        "--telemetry-interval",
        "--windowless-frame-rate",
        "--enable-output-buffer",
        "--disable-gpu-vsync",
        "--disable-frame-rate-limit",
    };
}

