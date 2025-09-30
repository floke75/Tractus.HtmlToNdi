using System.Globalization;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi.Launcher;

public sealed class LaunchParameters
{
    private LaunchParameters(
        string ndiName,
        int port,
        string startUrl,
        int width,
        int height,
        FrameRate frameRate,
        bool enableBuffering,
        int bufferDepth,
        TimeSpan telemetryInterval,
        int? windowlessFrameRateOverride,
        bool disableGpuVsync,
        bool disableFrameRateLimit)
    {
        NdiName = ndiName;
        Port = port;
        StartUrl = startUrl;
        Width = width;
        Height = height;
        FrameRate = frameRate;
        EnableBuffering = enableBuffering;
        BufferDepth = bufferDepth;
        TelemetryInterval = telemetryInterval;
        WindowlessFrameRateOverride = windowlessFrameRateOverride;
        DisableGpuVsync = disableGpuVsync;
        DisableFrameRateLimit = disableFrameRateLimit;
    }

    public string NdiName { get; }

    public int Port { get; }

    public string StartUrl { get; }

    public int Width { get; }

    public int Height { get; }

    public FrameRate FrameRate { get; }

    public bool EnableBuffering { get; }

    public int BufferDepth { get; }

    public TimeSpan TelemetryInterval { get; }

    public int? WindowlessFrameRateOverride { get; }

    public bool DisableGpuVsync { get; }

    public bool DisableFrameRateLimit { get; }

    public static bool TryFromArgs(string[] args, out LaunchParameters? parameters)
    {
        parameters = null;

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
                Serilog.Log.Error("Could not parse the --port parameter. Exiting.");
                return false;
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
            Serilog.Log.Error("Invalid --url parameter. Exiting.");
            return false;
        }

        var width = 1920;
        var widthArg = GetArgValue("--w");
        if (widthArg is not null && (!int.TryParse(widthArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out width) || width <= 0))
        {
            Serilog.Log.Error("Could not parse the --w (width) parameter. Exiting.");
            return false;
        }

        var height = 1080;
        var heightArg = GetArgValue("--h");
        if (heightArg is not null && (!int.TryParse(heightArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out height) || height <= 0))
        {
            Serilog.Log.Error("Could not parse the --h (height) parameter. Exiting.");
            return false;
        }

        FrameRate frameRate;
        try
        {
            frameRate = FrameRate.Parse(GetArgValue("--fps"));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Could not parse the --fps parameter. Exiting.");
            return false;
        }

        var bufferDepth = 0;
        var bufferDepthArg = GetArgValue("--buffer-depth");
        if (bufferDepthArg is not null && (!int.TryParse(bufferDepthArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out bufferDepth) || bufferDepth < 0))
        {
            Serilog.Log.Error("Could not parse the --buffer-depth parameter. Exiting.");
            return false;
        }

        var telemetryInterval = TimeSpan.FromSeconds(10);
        var telemetryArg = GetArgValue("--telemetry-interval");
        if (telemetryArg is not null)
        {
            if (!double.TryParse(telemetryArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var telemetrySeconds) || telemetrySeconds <= 0)
            {
                Serilog.Log.Error("Could not parse the --telemetry-interval parameter. Exiting.");
                return false;
            }

            telemetryInterval = TimeSpan.FromSeconds(telemetrySeconds);
        }

        bool disableGpuVsync = HasFlag("--disable-gpu-vsync");
        bool disableFrameRateLimit = HasFlag("--disable-frame-rate-limit");

        bool enableBuffering = HasFlag("--enable-output-buffer") || bufferDepth > 0;

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
                Serilog.Log.Error("Could not parse the --windowless-frame-rate parameter. Exiting.");
                return false;
            }
        }

        parameters = new LaunchParameters(
            ndiName,
            port,
            startUrl,
            width,
            height,
            frameRate,
            enableBuffering,
            bufferDepth,
            telemetryInterval,
            windowlessFrameRateOverride,
            disableGpuVsync,
            disableFrameRateLimit);

        return true;
    }

    public static LaunchParameters FromSettings(LauncherSettings settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        FrameRate frameRate;
        try
        {
            frameRate = FrameRate.Parse(settings.FrameRate);
        }
        catch (Exception ex)
        {
            throw new FormatException("The configured frame rate is invalid.", ex);
        }

        if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out _))
        {
            throw new FormatException("The configured URL is invalid.");
        }

        int? windowlessFrameRateOverride = null;
        if (!string.IsNullOrWhiteSpace(settings.WindowlessFrameRateOverride))
        {
            if (double.TryParse(settings.WindowlessFrameRateOverride, NumberStyles.Float, CultureInfo.InvariantCulture, out var windowlessRate) && windowlessRate > 0)
            {
                windowlessFrameRateOverride = (int)Math.Clamp(Math.Round(windowlessRate), 1, 240);
            }
            else
            {
                throw new FormatException("The windowless frame rate override must be a positive number.");
            }
        }

        if (settings.Port <= 0 || settings.Port > 65535)
        {
            throw new FormatException("Port must be between 1 and 65535.");
        }

        if (settings.Width <= 0)
        {
            throw new FormatException("Width must be greater than zero.");
        }

        if (settings.Height <= 0)
        {
            throw new FormatException("Height must be greater than zero.");
        }

        if (settings.TelemetryIntervalSeconds <= 0)
        {
            throw new FormatException("Telemetry interval must be greater than zero.");
        }

        if (settings.EnableBuffering && settings.BufferDepth <= 0)
        {
            throw new FormatException("Buffer depth must be at least 1 when buffering is enabled.");
        }

        return new LaunchParameters(
            settings.NdiName,
            settings.Port,
            settings.Url,
            settings.Width,
            settings.Height,
            frameRate,
            settings.EnableBuffering,
            settings.EnableBuffering ? settings.BufferDepth : 0,
            TimeSpan.FromSeconds(settings.TelemetryIntervalSeconds),
            windowlessFrameRateOverride,
            settings.DisableGpuVsync,
            settings.DisableFrameRateLimit);
    }
}
