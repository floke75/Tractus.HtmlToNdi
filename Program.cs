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
using System.Linq;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    [STAThread]
    public static void Main(string[] args)
    {
        var sanitizedArgs = RemoveLauncherFlags(args);
        var launchCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", Guid.NewGuid().ToString());

        var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
        Directory.SetCurrentDirectory(exeDirectory);
        AppManagement.Initialize(sanitizedArgs);

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
        }
        else
        {
            if (!LaunchParameters.TryFromArgs(sanitizedArgs, out parameters) || parameters is null)
            {
                return;
            }
        }

        RunApplication(parameters, sanitizedArgs, launchCachePath);
    }

    private static void RunApplication(LaunchParameters parameters, string[] args, string launchCachePath)
    {
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

        var ndiNamePtr = UTF.StringToUtf8(parameters.NdiName);
        try
        {
            var settings_T = new NDIlib.send_create_t
            {
                p_ndi_name = ndiNamePtr
            };

            Program.NdiSenderPtr = NDIlib.send_create(ref settings_T);
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

        var ndiSender = new NativeNdiVideoSender(Program.NdiSenderPtr);
        var videoPipeline = new NdiVideoPipeline(ndiSender, frameRate, pipelineOptions, Log.Logger);

        try
        {
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
            });
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to initialize Chromium or the video pipeline.");
            videoPipeline.Dispose();
            return;
        }

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

        app.Run();

        running = false;
        thread.Join();
        browserWrapper.Dispose();

        if (Directory.Exists(launchCachePath))
        {
            try
            {
                Directory.Delete(launchCachePath, true);
            }
            catch
            {

            }
        }
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
