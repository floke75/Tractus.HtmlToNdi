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
using Tractus.HtmlToNdi.Chromium;
using Tractus.HtmlToNdi.Models;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi;
public class Program
{
    public static nint NdiSenderPtr;
    public static CefWrapper browserWrapper = null!;

    private static FramePump? framePump;
    private static NdiVideoPipeline? videoPipeline;

    public static void Main(string[] args)
    {
        var launchCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", Guid.NewGuid().ToString());

        var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
        Directory.SetCurrentDirectory(exeDirectory);
        AppManagement.Initialize(args);

        var ndiName = ParseStringArg(args, "--ndiname") ?? PromptForValue("NDI source name >", v => !string.IsNullOrWhiteSpace(v), "Invalid NDI source name. Exiting.");
        if (ndiName is null)
        {
            return;
        }

        var port = 9999;
        var portArg = ParseStringArg(args, "--port");
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
            port = PromptForPort();
        }

        var startUrl = ParseStringArg(args, "--url") ?? "https://testpattern.tractusevents.com/";

        var width = ParseIntArg(args, "--w") ?? 1920;
        var height = ParseIntArg(args, "--h") ?? 1080;

        FrameRate targetFrameRate;
        try
        {
            targetFrameRate = FrameRate.Parse(ParseStringArg(args, "--fps"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not parse the --fps parameter. Exiting.");
            return;
        }

        var windowlessFrameRate = ParseIntArg(args, "--windowless-frame-rate") ?? (int)Math.Round(targetFrameRate.Hertz);
        var disableVsync = args.Any(x => string.Equals(x, "--disable-vsync", StringComparison.OrdinalIgnoreCase));

        var enableOutputBuffer = args.Any(x => string.Equals(x, "--enable-output-buffer", StringComparison.OrdinalIgnoreCase));
        var bufferDepth = ParseIntArg(args, "--buffer-depth")
            ?? ParseIntArg(args, "--output-buffer-depth")
            ?? (enableOutputBuffer ? 3 : 0);
        bufferDepth = Math.Max(0, bufferDepth);

        AsyncContext.Run(async delegate
        {
            var settings = new CefSettings();
            if (!Directory.Exists(launchCachePath))
            {
                Directory.CreateDirectory(launchCachePath);
            }

            settings.RootCachePath = launchCachePath;
            settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");
            settings.CefCommandLineArgs.Add("off-screen-frame-rate", windowlessFrameRate.ToString(CultureInfo.InvariantCulture));
            if (disableVsync)
            {
                settings.CefCommandLineArgs.Add("disable-gpu-vsync");
            }

            settings.EnableAudio();
            Cef.Initialize(settings);
            browserWrapper = new CefWrapper(
                width,
                height,
                startUrl);

            await browserWrapper.InitializeWrapperAsync();
            browserWrapper.Browser.GetBrowserHost().WindowlessFrameRate = windowlessFrameRate;
        });

        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddSerilog();

        builder.WebHost.UseUrls($"http://*:{port}");

        builder.Services.AddAuthorization();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();
        app.UseSwagger();
        app.UseSwaggerUI();

        var settings_T = new NDIlib.send_create_t
        {
            p_ndi_name = UTF.StringToUtf8(ndiName)
        };

        Program.NdiSenderPtr = NDIlib.send_create(ref settings_T);

        var capabilitiesXml = $$"""<ndi_capabilities ntk_kvm="true" />""" + "\0";
        var capabilitiesPtr = UTF.StringToUtf8(capabilitiesXml);

        var metaframe = new NDIlib.metadata_frame_t()
        {
            p_data = capabilitiesPtr
        };

        NDIlib.send_add_connection_metadata(NdiSenderPtr, ref metaframe);
        Marshal.FreeHGlobal(capabilitiesPtr);

        framePump = new FramePump(() => browserWrapper.InvalidateAsync(), targetFrameRate.FrameDuration, Log.Logger);
        browserWrapper.AttachFramePump(framePump);
        framePump.Start();

        videoPipeline = new NdiVideoPipeline(Program.NdiSenderPtr, targetFrameRate, bufferDepth, Log.Logger);
        browserWrapper.SetFrameHandler((paintArgs, timestamp) => videoPipeline.HandleFrame(paintArgs, timestamp));

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

                    if (metadataConverted.StartsWith("<ndi_kvm u=\""))
                    {
                        metadataConverted = metadataConverted.Replace("<ndi_kvm u=\"", string.Empty);
                        metadataConverted = metadataConverted.Replace("\"/>", string.Empty);

                        try
                        {
                            var binary = Convert.FromBase64String(metadataConverted);

                            var opcode = binary[0];

                            if (opcode == 0x03)
                            {
                                x = BitConverter.ToSingle(binary, 1);
                                y = BitConverter.ToSingle(binary, 5);
                            }
                            else if (opcode == 0x04)
                            {
                                var screenX = (int)(x * width);
                                var screenY = (int)(y * height);

                                browserWrapper.Click(screenX, screenY);
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

        framePump?.Dispose();
        videoPipeline?.Dispose();
        browserWrapper.Dispose();

        if (Directory.Exists(launchCachePath))
        {
            try
            {
                Directory.Delete(launchCachePath, true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not delete cache path {CachePath}", launchCachePath);
            }
        }
    }

    private static int PromptForPort()
    {
        while (true)
        {
            Console.Write("HTTP API port # >");
            var portNumber = Console.ReadLine()?.Trim();
            if (int.TryParse(portNumber, out var port))
            {
                return port;
            }
        }
    }

    private static string? ParseStringArg(string[] args, string key)
    {
        var raw = args.FirstOrDefault(x => x.StartsWith(key, StringComparison.OrdinalIgnoreCase));
        if (raw is null)
        {
            return null;
        }

        var parts = raw.Split('=', 2);
        return parts.Length == 2 ? parts[1] : null;
    }

    private static int? ParseIntArg(string[] args, string key)
    {
        var value = ParseStringArg(args, key);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static string? PromptForValue(string prompt, Func<string?, bool> validator, string errorMessage)
    {
        while (true)
        {
            Console.Write(prompt);
            var value = Console.ReadLine()?.Trim();
            if (validator(value))
            {
                return value;
            }

            Log.Error(errorMessage);
        }
    }
}
