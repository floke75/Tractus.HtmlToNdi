
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
using System.Runtime.InteropServices;
using Tractus.HtmlToNdi.Chromium;
using Tractus.HtmlToNdi.Models;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi;
public class Program
{
    public static nint NdiSenderPtr;
    public static CefWrapper browserWrapper;
    public static FrameRingBuffer FrameBuffer = null!;
    public static FramePacer? VideoFramePacer;
    public static double TargetFrameRate { get; private set; } = 30000.0 / 1001.0;
    public static (int Numerator, int Denominator) FrameRateFraction { get; private set; } = (30000, 1001);

    public static void Main(string[] args)
    {
        var launchCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", Guid.NewGuid().ToString());

        var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
        Directory.SetCurrentDirectory(exeDirectory);
        AppManagement.Initialize(args);

        var ndiName = "HTML5";
        if (args.Any(x => x.StartsWith("--ndiname")))
        {
            try
            {
                ndiName = args.FirstOrDefault(x => x.StartsWith("--ndiname")).Split("=")[1];

                if (string.IsNullOrWhiteSpace(ndiName))
                {
                    throw new ArgumentException();
                }
            }
            catch
            {
                Log.Error("Invalid NDI source name. Exiting.");
                return;
            }
        }
        else
        {
            ndiName = "";
            while (string.IsNullOrWhiteSpace(ndiName))
            {
                Console.Write("NDI source name >");
                ndiName = Console.ReadLine()?.Trim();
            }
        }

        var port = 9999;
        if (args.Any(x => x.StartsWith("--port")))
        {
            try
            {
                port = int.Parse(args.FirstOrDefault(x => x.StartsWith("--port")).Split("=")[1]);
            }
            catch (Exception)
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

        var startUrl = "https://testpattern.tractusevents.com/";
        if (args.Any(x => x.StartsWith("--url")))
        {
            try
            {
                startUrl = args.FirstOrDefault(x => x.StartsWith("--url")).Split("=")[1];
            }
            catch (Exception)
            {
                Log.Error("Could not parse the --url parameter. Exiting.");
                return;
            }
        }

        var width = 1920;
        var height = 1080;

        if (args.Any(x => x.StartsWith("--w")))
        {
            try
            {
                width = int.Parse(args.FirstOrDefault(x => x.StartsWith("--w")).Split("=")[1]);
            }
            catch (Exception)
            {
                Log.Error("Could not parse the --w (width) parameter. Exiting.");
                return;
            }
        }

        if (args.Any(x => x.StartsWith("--h")))
        {
            try
            {
                height = int.Parse(args.FirstOrDefault(x => x.StartsWith("--h")).Split("=")[1]);
            }
            catch (Exception)
            {
                Log.Error("Could not parse the --h (height) parameter. Exiting.");
                return;
            }
        }

        var bufferDepth = 3;
        if (args.Any(x => x.StartsWith("--buffer-depth")))
        {
            try
            {
                bufferDepth = int.Parse(args.First(x => x.StartsWith("--buffer-depth")).Split("=")[1]);
                if (bufferDepth <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(bufferDepth));
                }
            }
            catch (Exception)
            {
                Log.Error("Could not parse the --buffer-depth parameter. Exiting.");
                return;
            }
        }

        TargetFrameRate = 30000.0 / 1001.0;
        if (args.Any(x => x.StartsWith("--fps")))
        {
            try
            {
                var fpsValue = double.Parse(args.First(x => x.StartsWith("--fps")).Split("=")[1], CultureInfo.InvariantCulture);
                if (fpsValue <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(fpsValue));
                }

                TargetFrameRate = fpsValue;
            }
            catch (Exception)
            {
                Log.Error("Could not parse the --fps parameter. Exiting.");
                return;
            }
        }

        FrameRateFraction = CalculateFrameRateFraction(TargetFrameRate);
        FrameBuffer = new FrameRingBuffer(bufferDepth);

        var disableVsync = args.Contains("--disable-vsync");
        var disableFrameRateLimit = args.Contains("--disable-frame-rate-limit");

        AsyncContext.Run(async delegate
        {
            var settings = new CefSettings();
            if (!Directory.Exists(launchCachePath))
            {
                Directory.CreateDirectory(launchCachePath);
            }

            settings.RootCachePath = launchCachePath;
            //settings.CefCommandLineArgs.Add("--disable-gpu-sandbox");
            //settings.CefCommandLineArgs.Add("--no-sandbox");
            //settings.CefCommandLineArgs.Add("--in-process-gpu");
            //settings.SetOffScreenRenderingBestPerformanceArgs();
            settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");
            if (disableFrameRateLimit)
            {
                settings.CefCommandLineArgs.Add("disable-frame-rate-limit");
            }

            if (disableVsync)
            {
                settings.CefCommandLineArgs.Add("disable-gpu-vsync");
            }
            settings.EnableAudio();
            Cef.Initialize(settings);
            browserWrapper = new CefWrapper(
                width,
                height,
                startUrl,
                FrameBuffer);

            await browserWrapper.InitializeWrapperAsync();
        });

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

        var settings_T = new NDIlib.send_create_t
        {
            p_ndi_name = UTF.StringToUtf8(ndiName)
        };

        Program.NdiSenderPtr = NDIlib.send_create(ref settings_T);

        var videoSender = new NdiVideoFrameSender(() => Program.NdiSenderPtr, () => Program.FrameRateFraction);
        VideoFramePacer = new FramePacer(FrameBuffer, videoSender, TargetFrameRate);
        VideoFramePacer.Start();

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
        if (VideoFramePacer is not null)
        {
            VideoFramePacer.Dispose();
        }
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

    private static (int Numerator, int Denominator) CalculateFrameRateFraction(double fps)
    {
        const double tolerance = 1e-6;
        if (Math.Abs(fps - (30000.0 / 1001.0)) < 1e-3)
        {
            return (30000, 1001);
        }

        var denominator = 1;
        var numerator = fps;
        while (Math.Abs(numerator - Math.Round(numerator)) > tolerance && denominator < 1000)
        {
            denominator++;
            numerator = fps * denominator;
        }

        return ((int)Math.Round(numerator), denominator);
    }
}
