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
using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Tractus.HtmlToNdi.Chromium;
using Tractus.HtmlToNdi.Models;

namespace Tractus.HtmlToNdi;
public class Program
{
    public static nint NdiSenderPtr;
    public static CefWrapper browserWrapper;

    public static void Main(string[] args)
    {
        var launchCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", Guid.NewGuid().ToString());

        var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
        Directory.SetCurrentDirectory(exeDirectory);
        AppManagement.Initialize(args);

        // --- Argument Parsing ---
        var ndiName = "HTML5";
        if (args.Any(x => x.StartsWith("--ndiname")))
        {
            try
            {
                ndiName = args.FirstOrDefault(x => x.StartsWith("--ndiname")).Split("=")[1];
                if (string.IsNullOrWhiteSpace(ndiName)) throw new ArgumentException();
            }
            catch { Log.Error("Invalid NDI source name. Exiting."); return; }
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
            try { port = int.Parse(args.FirstOrDefault(x => x.StartsWith("--port")).Split("=")[1]); }
            catch (Exception) { Log.Error("Could not parse the --port parameter. Exiting."); return; }
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
            try { startUrl = args.FirstOrDefault(x => x.StartsWith("--url")).Split("=")[1]; }
            catch (Exception) { Log.Error("Could not parse the --url parameter. Exiting."); return; }
        }

        var width = 1920;
        if (args.Any(x => x.StartsWith("--w")))
        {
            try { width = int.Parse(args.FirstOrDefault(x => x.StartsWith("--w")).Split("=")[1]); }
            catch (Exception) { Log.Error("Could not parse the --w (width) parameter. Exiting."); return; }
        }

        var height = 1080;
        if (args.Any(x => x.StartsWith("--h")))
        {
            try { height = int.Parse(args.FirstOrDefault(x => x.StartsWith("--h")).Split("=")[1]); }
            catch (Exception) { Log.Error("Could not parse the --h (height) parameter. Exiting."); return; }
        }

        var targetFps = 60d;
        if (args.Any(x => x.StartsWith("--fps")))
        {
            try
            {
                var value = args.First(x => x.StartsWith("--fps")).Split("=")[1];
                targetFps = double.Parse(value, CultureInfo.InvariantCulture);
                if (targetFps <= 0) throw new ArgumentOutOfRangeException(nameof(targetFps));
            }
            catch (Exception) { Log.Error("Could not parse the --fps parameter. Exiting."); return; }
        }

        var enableBuffering = args.Any(x => string.Equals(x, "--buffered", StringComparison.OrdinalIgnoreCase));
        var bufferDepth = 3;
        var bufferDepthArgument = args.FirstOrDefault(x => x.StartsWith("--buffer-depth"));
        if (!string.IsNullOrEmpty(bufferDepthArgument))
        {
            try
            {
                var value = bufferDepthArgument.Split("=")[1];
                var parsedDepth = int.Parse(value, CultureInfo.InvariantCulture);
                if (parsedDepth <= 0)
                {
                    enableBuffering = false;
                }
                else
                {
                    enableBuffering = true;
                    bufferDepth = parsedDepth;
                }
            }
            catch (Exception) { Log.Error("Could not parse the --buffer-depth parameter. Exiting."); return; }
        }
        if (enableBuffering) bufferDepth = Math.Max(1, bufferDepth);

        var pipelineOptions = new NdiVideoPipelineOptions
        {
            TargetFrameRate = targetFps,
            EnableBuffering = enableBuffering,
            BufferDepth = bufferDepth,
        };

        // --- Initialization ---
        AsyncContext.Run(async delegate
        {
            var settings = new CefSettings();
            if (!Directory.Exists(launchCachePath)) Directory.CreateDirectory(launchCachePath);
            settings.RootCachePath = launchCachePath;
            settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");
            settings.EnableAudio();
            Cef.Initialize(settings);
            browserWrapper = new CefWrapper(width, height, startUrl, pipelineOptions);
            await browserWrapper.InitializeWrapperAsync();
        });

        // --- Web App and NDI Setup ---
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSerilog();
        builder.WebHost.UseUrls($"http://*:{port}");
        builder.Services.AddAuthorization();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        var app = builder.Build();
        app.UseSwagger();
        app.UseSwaggerUI();

        var settings_T = new NDIlib.send_create_t { p_ndi_name = UTF.StringToUtf8(ndiName) };
        Program.NdiSenderPtr = NDIlib.send_create(ref settings_T);

        // --- KVM and Metadata Thread ---
        var capabilitiesXml = $$"""<ndi_capabilities ntk_kvm="true" />""";
        capabilitiesXml += "\0";
        var capabilitiesPtr = UTF.StringToUtf8(capabilitiesXml);
        var metaframe = new NDIlib.metadata_frame_t() { p_data = capabilitiesPtr };
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
                if (result == NDIlib.frame_type_e.frame_type_none) continue;
                else if (result == NDIlib.frame_type_e.frame_type_metadata)
                {
                    var metadataConverted = UTF.Utf8ToString(metadata.p_data);
                    if (metadataConverted.StartsWith("<ndi_kvm u=\""))
                    {
                        metadataConverted = metadataConverted.Replace("<ndi_kvm u=\"", "");
                        metadataConverted = metadataConverted.Replace("\"/>", "");
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
                        catch { }
                    }
                    Log.Logger.Warning("Got metadata: " + metadataConverted);
                    NDIlib.send_free_metadata(NdiSenderPtr, ref metadata);
                }
            }
        });
        thread.Start();

        // --- API Routes ---
        app.MapPost("/seturl", (HttpContext httpContext, GoToUrlModel url) => { browserWrapper.SetUrl(url.Url); return true; }).WithOpenApi();
        app.MapGet("/scroll/{increment}", (int increment) => { browserWrapper.ScrollBy(increment); }).WithOpenApi();
        app.MapGet("/click/{x}/{y}", (int x, int y) => { browserWrapper.Click(x, y); }).WithOpenApi();
        app.MapPost("/keystroke", (SendKeystrokeModel model) => { browserWrapper.SendKeystrokes(model); }).WithOpenApi();
        app.MapGet("/type/{toType}", (string toType) => { browserWrapper.SendKeystrokes(new SendKeystrokeModel { ToSend = toType }); }).WithOpenApi();
        app.MapGet("/refresh", () => { browserWrapper.RefreshPage(); }).WithOpenApi();

        // --- Run and Shutdown ---
        app.Run();
        running = false;
        thread.Join();
        browserWrapper.Dispose();

        if (Directory.Exists(launchCachePath))
        {
            try { Directory.Delete(launchCachePath, true); }
            catch { }
        }
    }
}