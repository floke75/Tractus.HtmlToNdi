
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
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Tractus.HtmlToNdi.Chromium;
using Tractus.HtmlToNdi.Models;
using Tractus.HtmlToNdi.Video;
using Tractus.HtmlToNdi.Launcher;

namespace Tractus.HtmlToNdi;
public class Program
{
    public static nint NdiSenderPtr;
    public static CefWrapper browserWrapper;

    [STAThread]
    public static void Main(string[] args)
    {
        var launchCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", Guid.NewGuid().ToString());

        var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
        Directory.SetCurrentDirectory(exeDirectory);
        AppManagement.Initialize(args);

        if (ShouldUseLauncher(args))
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var persistedSettings = LauncherSettingsStorage.Load();
            using var launcherForm = new LauncherForm(persistedSettings);
            var dialogResult = launcherForm.ShowDialog();
            if (dialogResult != DialogResult.OK || launcherForm.SelectedSettings is null)
            {
                Log.Information("Launcher closed without starting the application.");
                return;
            }

            LauncherSettingsStorage.Save(launcherForm.SelectedSettings);

            var passthroughFlags = args.Where(arg => arg.StartsWith('-', StringComparison.Ordinal) && !arg.StartsWith("--", StringComparison.Ordinal));
            args = passthroughFlags
                .Concat(launcherForm.SelectedSettings.BuildArguments())
                .ToArray();
        }

        string? GetArgValue(string switchName)
            => args.FirstOrDefault(x => x.StartsWith($"{switchName}=", StringComparison.Ordinal))?
                .Split('=', 2)[1];

        bool HasFlag(string flag) => args.Any(x => x.Equals(flag, StringComparison.Ordinal));

        var ndiName = GetArgValue("--ndiname") ?? "HTML5";
        if (string.IsNullOrWhiteSpace(ndiName))
        {
            do
            {
                Console.Write("NDI source name >");
                ndiName = Console.ReadLine()?.Trim();
            }
            while (string.IsNullOrWhiteSpace(ndiName));
        }

        var port = 9999;
        var portArg = GetArgValue("--port");
        if (portArg is not null)
        {
            if (!int.TryParse(portArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
            {
                Log.Error("Could not parse the --port parameter. Exiting.");
                return;
            }
        }
        else
        {
            var portNumber = "";
            while (string.IsNullOrWhiteSpace(portNumber) || !int.TryParse(portNumber, out port))
            {
                Console.Write("HTTP API port # >");
                portNumber = Console.ReadLine()?.Trim();
            }
        }

        var startUrl = GetArgValue("--url") ?? "https://testpattern.tractusevents.com/";

        if (!Uri.TryCreate(startUrl, UriKind.Absolute, out _))
        {
            Log.Error("Invalid --url parameter. Exiting.");
            return;
        }

        var width = 1920;
        var widthArg = GetArgValue("--w");
        if (widthArg is not null && (!int.TryParse(widthArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out width) || width <= 0))
        {
            Log.Error("Could not parse the --w (width) parameter. Exiting.");
            return;
        }

        var height = 1080;
        var heightArg = GetArgValue("--h");
        if (heightArg is not null && (!int.TryParse(heightArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out height) || height <= 0))
        {
            Log.Error("Could not parse the --h (height) parameter. Exiting.");
            return;
        }

        var frameRate = FrameRate.Parse(GetArgValue("--fps"));

        var bufferDepth = 0;
        var bufferDepthArg = GetArgValue("--buffer-depth");
        if (bufferDepthArg is not null && (!int.TryParse(bufferDepthArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out bufferDepth) || bufferDepth < 0))
        {
            Log.Error("Could not parse the --buffer-depth parameter. Exiting.");
            return;
        }

        var telemetryInterval = TimeSpan.FromSeconds(10);
        var telemetryArg = GetArgValue("--telemetry-interval");
        if (telemetryArg is not null)
        {
            if (!double.TryParse(telemetryArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var telemetrySeconds) || telemetrySeconds <= 0)
            {
                Log.Error("Could not parse the --telemetry-interval parameter. Exiting.");
                return;
            }

            telemetryInterval = TimeSpan.FromSeconds(telemetrySeconds);
        }

        var enableBuffering = HasFlag("--enable-output-buffer") || bufferDepth > 0;
        var effectiveDepth = enableBuffering ? Math.Max(1, bufferDepth == 0 ? 3 : bufferDepth) : 1;

        int? windowlessFrameRateOverride = null;
        var windowlessRateArg = GetArgValue("--windowless-frame-rate");
        if (windowlessRateArg is not null)
        {
            if (double.TryParse(windowlessRateArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var windowlessRate) && windowlessRate > 0)
            {
                windowlessFrameRateOverride = (int)Math.Clamp(Math.Round(windowlessRate), 1, 240);
            }
            else
            {
                Log.Error("Could not parse the --windowless-frame-rate parameter. Exiting.");
                return;
            }
        }

        var pipelineOptions = new NdiVideoPipelineOptions
        {
            EnableBuffering = enableBuffering,
            BufferDepth = effectiveDepth,
            TelemetryInterval = telemetryInterval,
        };

        var ndiNamePtr = UTF.StringToUtf8(ndiName);
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

                if (HasFlag("--disable-gpu-vsync"))
                {
                    settings.CefCommandLineArgs.Add("disable-gpu-vsync", "1");
                }

                if (HasFlag("--disable-frame-rate-limit"))
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

        builder.WebHost.UseUrls($"http://*:{port}");

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

    private static bool ShouldUseLauncher(string[] args)
    {
        if (args.Length == 0)
        {
            return true;
        }

        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
