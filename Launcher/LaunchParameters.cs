using System;
using System.Globalization;
using System.Linq;
using Serilog;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi.Launcher;

/// <summary>
/// Represents the parameters used to launch the application.
/// </summary>
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
        bool disableFrameRateLimit,
        bool allowLatencyExpansion,
        bool alignWithCaptureTimestamps,
        bool enableCadenceTelemetry,
        bool enablePacedInvalidation,
        bool disablePacedInvalidation,
        bool enableCaptureBackpressure,
        bool enablePumpCadenceAdaptation,
        bool enableCompositorCapture,
        bool enableGpuRasterization,
        bool enableZeroCopy,
        bool enableOutOfProcessRasterization,
        bool disableBackgroundThrottling,
        bool presetHighPerformance,
        PacingMode pacingMode,
        bool ndiSendAsync)
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
        AllowLatencyExpansion = allowLatencyExpansion;
        AlignWithCaptureTimestamps = alignWithCaptureTimestamps;
        EnableCadenceTelemetry = enableCadenceTelemetry;
        EnablePacedInvalidation = enablePacedInvalidation;
        DisablePacedInvalidation = disablePacedInvalidation;
        EnableCaptureBackpressure = enableCaptureBackpressure;
        EnablePumpCadenceAdaptation = enablePumpCadenceAdaptation;
        EnableCompositorCapture = enableCompositorCapture;
        EnableGpuRasterization = enableGpuRasterization;
        EnableZeroCopy = enableZeroCopy;
        EnableOutOfProcessRasterization = enableOutOfProcessRasterization;
        DisableBackgroundThrottling = disableBackgroundThrottling;
        PresetHighPerformance = presetHighPerformance;
        PacingMode = pacingMode;
        NdiSendAsync = ndiSendAsync;
    }

    /// <summary>
    /// Gets the NDI source name.
    /// </summary>
    public string NdiName { get; }

    /// <summary>
    /// Gets the HTTP server port.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Gets the startup URL.
    /// </summary>
    public string StartUrl { get; }

    /// <summary>
    /// Gets the width of the browser.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the height of the browser.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the target NDI frame rate.
    /// </summary>
    public FrameRate FrameRate { get; }

    /// <summary>
    /// Gets a value indicating whether the paced output buffer is enabled.
    /// </summary>
    public bool EnableBuffering { get; }

    /// <summary>
    /// Gets the capacity of the paced output buffer.
    /// </summary>
    public int BufferDepth { get; }

    /// <summary>
    /// Gets the interval between video pipeline telemetry log entries.
    /// </summary>
    public TimeSpan TelemetryInterval { get; }

    /// <summary>
    /// Gets an optional override for Chromium's internal repaint cadence.
    /// </summary>
    public int? WindowlessFrameRateOverride { get; }

    /// <summary>
    /// Gets a value indicating whether to disable Chromium's GPU vsync throttling.
    /// </summary>
    public bool DisableGpuVsync { get; }

    /// <summary>
    /// Gets a value indicating whether to disable Chromium's frame rate limiter.
    /// </summary>
    public bool DisableFrameRateLimit { get; }

    /// <summary>
    /// Gets a value indicating whether the paced buffer should keep playing any queued frames during recovery.
    /// </summary>
    public bool AllowLatencyExpansion { get; }

    /// <summary>
    /// Gets a value indicating whether paced output should align deadlines using capture timestamps.
    /// </summary>
    public bool AlignWithCaptureTimestamps { get; }

    /// <summary>
    /// Gets a value indicating whether telemetry should include capture/output cadence metrics.
    /// </summary>
    public bool EnableCadenceTelemetry { get; }

    /// <summary>
    /// Gets a value indicating whether Chromium invalidation should be paced by the sender loop.
    /// </summary>
    public bool EnablePacedInvalidation { get; }

    /// <summary>
    /// Gets a value indicating whether paced invalidation should be forcefully disabled.
    /// </summary>
    public bool DisablePacedInvalidation { get; }

    /// <summary>
    /// Gets a value indicating whether capture backpressure should pause Chromium invalidation when the buffer is ahead.
    /// </summary>
    public bool EnableCaptureBackpressure { get; }

    /// <summary>
    /// Gets a value indicating whether the Chromium pump adapts its cadence using pipeline telemetry.
    /// </summary>
    public bool EnablePumpCadenceAdaptation { get; }

    /// <summary>
    /// Gets a value indicating whether the compositor capture path should be enabled.
    /// </summary>
    public bool EnableCompositorCapture { get; }

    /// <summary>
    /// Gets a value indicating whether Chromium should force GPU rasterization.
    /// </summary>
    public bool EnableGpuRasterization { get; }

    /// <summary>
    /// Gets a value indicating whether Chromium should enable zero-copy raster uploads.
    /// </summary>
    public bool EnableZeroCopy { get; }

    /// <summary>
    /// Gets a value indicating whether Chromium should use the out-of-process rasterizer.
    /// </summary>
    public bool EnableOutOfProcessRasterization { get; }

    /// <summary>
    /// Gets a value indicating whether Chromium should keep renderers active even when hidden.
    /// </summary>
    public bool DisableBackgroundThrottling { get; }

    /// <summary>
    /// Gets a value indicating whether the high-performance preset should be enabled.
    /// </summary>
    public bool PresetHighPerformance { get; }

    /// <summary>
    /// Gets the pacing mode for the video pipeline.
    /// </summary>
    public PacingMode PacingMode { get; }

    /// <summary>
    /// Gets a value indicating whether the NDI sender should use the asynchronous send method.
    /// </summary>
    public bool NdiSendAsync { get; }

    /// <summary>
    /// Attempts to create a <see cref="LaunchParameters"/> instance from command-line arguments.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="parameters">The resulting launch parameters, if parsing is successful.</param>
    /// <returns>True if the parameters were parsed successfully; otherwise, false.</returns>
    public static bool TryFromArgs(string[] args, out LaunchParameters? parameters)
    {
        parameters = null;

        string? GetArgValue(string switchName)
            => args.FirstOrDefault(x => x.StartsWith($"{switchName}=", StringComparison.Ordinal))?
                .Split('=', 2)[1];

        bool HasFlag(string flag) => args.Any(x => x.Equals(flag, StringComparison.Ordinal));

        bool ResolveToggle(string enableFlag, string disableFlag, bool defaultValue)
        {
            var enabled = HasFlag(enableFlag);
            var disabled = HasFlag(disableFlag);

            if (enabled && disabled)
            {
                Log.Warning("Both {EnableFlag} and {DisableFlag} were provided; defaulting to {Default}", enableFlag, disableFlag, defaultValue);
                return defaultValue;
            }

            if (enabled)
            {
                return true;
            }

            if (disabled)
            {
                return false;
            }

            return defaultValue;
        }

        bool? ResolveOptionalToggle(string enableFlag, string disableFlag)
        {
            var enabled = HasFlag(enableFlag);
            var disabled = HasFlag(disableFlag);

            if (enabled && disabled)
            {
                Log.Warning("Both {EnableFlag} and {DisableFlag} were provided; ignoring both and using defaults", enableFlag, disableFlag);
                return null;
            }

            if (enabled)
            {
                return true;
            }

            if (disabled)
            {
                return false;
            }

            return null;
        }

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
                return false;
            }
        }
        else
        {
            var portNumber = string.Empty;
            while (string.IsNullOrWhiteSpace(portNumber) || !int.TryParse(portNumber, out port))
            {
                Console.Write("HTTP API port # >");
                portNumber = Console.ReadLine()?.Trim() ?? string.Empty;
            }
        }

        var startUrl = GetArgValue("--url") ?? "https://testpattern.tractusevents.com/";

        if (!Uri.TryCreate(startUrl, UriKind.Absolute, out _))
        {
            Log.Error("Invalid --url parameter. Exiting.");
            return false;
        }

        var width = 1920;
        var widthArg = GetArgValue("--w");
        if (widthArg is not null && (!int.TryParse(widthArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out width) || width <= 0))
        {
            Log.Error("Could not parse the --w (width) parameter. Exiting.");
            return false;
        }

        var height = 1080;
        var heightArg = GetArgValue("--h");
        if (heightArg is not null && (!int.TryParse(heightArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out height) || height <= 0))
        {
            Log.Error("Could not parse the --h (height) parameter. Exiting.");
            return false;
        }

        FrameRate frameRate;
        try
        {
            frameRate = FrameRate.Parse(GetArgValue("--fps"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not parse the --fps parameter. Exiting.");
            return false;
        }

        var bufferDepth = 0;
        var bufferDepthArg = GetArgValue("--buffer-depth");
        if (bufferDepthArg is not null && (!int.TryParse(bufferDepthArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out bufferDepth) || bufferDepth < 0))
        {
            Log.Error("Could not parse the --buffer-depth parameter. Exiting.");
            return false;
        }

        var telemetryInterval = TimeSpan.FromSeconds(10);
        var telemetryArg = GetArgValue("--telemetry-interval");
        if (telemetryArg is not null)
        {
            if (!double.TryParse(telemetryArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var telemetrySeconds) || telemetrySeconds <= 0)
            {
                Log.Error("Could not parse the --telemetry-interval parameter. Exiting.");
                return false;
            }

            telemetryInterval = TimeSpan.FromSeconds(telemetrySeconds);
        }

        var enableBuffering = HasFlag("--enable-output-buffer") || bufferDepth > 0;

        var alignWithCaptureTimestamps = !HasFlag("--disable-capture-alignment");
        if (!alignWithCaptureTimestamps && HasFlag("--align-with-capture-timestamps"))
        {
            alignWithCaptureTimestamps = true;
        }

        var enableCadenceTelemetry = !HasFlag("--disable-cadence-telemetry");
        if (!enableCadenceTelemetry && HasFlag("--enable-cadence-telemetry"))
        {
            enableCadenceTelemetry = true;
        }

        var pacedInvalidationToggle = ResolveOptionalToggle("--enable-paced-invalidation", "--disable-paced-invalidation");
        var enablePacedInvalidation = pacedInvalidationToggle == true;
        var disablePacedInvalidation = pacedInvalidationToggle == false;
        var enableCaptureBackpressure = ResolveToggle("--enable-capture-backpressure", "--disable-capture-backpressure", false);
        var enablePumpCadenceAdaptation = ResolveToggle("--enable-pump-cadence-adaptation", "--disable-pump-cadence-adaptation", false);
        var enableCompositorCapture = ResolveToggle("--enable-compositor-capture", "--disable-compositor-capture", false);
        var enableGpuRasterization = HasFlag("--enable-gpu-rasterization");
        var enableZeroCopy = HasFlag("--enable-zero-copy");
        var enableOutOfProcessRasterization = HasFlag("--enable-oop-rasterization") || HasFlag("--enable-out-of-process-rasterization");
        var disableBackgroundThrottling = HasFlag("--disable-background-throttling") || HasFlag("--disable-renderer-backgrounding");
        var presetHighPerformance = HasFlag("--preset-high-performance");
        var ndiSendAsync = HasFlag("--ndi-send-async");
        var pacingMode = PacingMode.Latency;
        var pacingModeArg = GetArgValue("--pacing-mode");
        if (pacingModeArg is not null && !Enum.TryParse(pacingModeArg, true, out pacingMode))
        {
            Log.Error("Could not parse the --pacing-mode parameter. Exiting.");
            return false;
        }

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
            HasFlag("--disable-gpu-vsync"),
            HasFlag("--disable-frame-rate-limit"),
            HasFlag("--allow-latency-expansion"),
            alignWithCaptureTimestamps,
            enableCadenceTelemetry,
            enablePacedInvalidation,
            disablePacedInvalidation,
            enableCaptureBackpressure,
            enablePumpCadenceAdaptation,
            enableCompositorCapture,
            enableGpuRasterization,
            enableZeroCopy,
            enableOutOfProcessRasterization,
            disableBackgroundThrottling,
            presetHighPerformance,
            pacingMode,
            ndiSendAsync);

        return true;
    }

    /// <summary>
    /// Creates a <see cref="LaunchParameters"/> instance from a <see cref="LauncherSettings"/> object.
    /// </summary>
    /// <param name="settings">The launcher settings.</param>
    /// <returns>A new <see cref="LaunchParameters"/> instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="settings"/> is null.</exception>
    /// <exception cref="FormatException">Thrown if any of the settings are invalid.</exception>
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
            settings.DisableFrameRateLimit,
            settings.AllowLatencyExpansion,
            settings.AlignWithCaptureTimestamps,
            settings.EnableCadenceTelemetry,
            settings.EnablePacedInvalidation,
            settings.DisablePacedInvalidation,
            settings.EnableCaptureBackpressure,
            settings.EnablePumpCadenceAdaptation,
            settings.EnableCompositorCapture,
            settings.EnableGpuRasterization,
            settings.EnableZeroCopy,
            settings.EnableOutOfProcessRasterization,
            settings.DisableBackgroundThrottling,
            settings.PresetHighPerformance,
            settings.PacingMode,
            settings.NdiSendAsync);
    }
}
