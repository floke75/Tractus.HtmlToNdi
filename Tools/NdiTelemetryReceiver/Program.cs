using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using NewTek;

// NDIlib's static cctor registers its own resolver but it can fail on some systems.
// Pre-load the native DLL so the resolver finds it already mapped into the process.
foreach (var p in new[]
{
    Path.Combine(AppContext.BaseDirectory, "Processing.NDI.Lib.x64.dll"),
    @"C:\Program Files\NDI\NDI 6 Tools\Runtime\Processing.NDI.Lib.x64.dll",
    @"C:\Program Files\NDI\NDI 6 Runtime\v6\Processing.NDI.Lib.x64.dll",
    @"C:\Program Files\NDI\NDI 6 SDK\Bin\x64\Processing.NDI.Lib.x64.dll",
})
{
    if (File.Exists(p) && NativeLibrary.TryLoad(p, out _))
    {
        // Also add the directory to the DLL search path so the resolver picks it up by short name.
        var dir = Path.GetDirectoryName(p);
        if (dir is not null)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + path);
        }
        break;
    }
}

string? sourceFilter = null;
int durationSeconds = 30;
string? outputPath = null;
int findTimeoutMs = 5000;

foreach (var arg in args)
{
    if (arg.StartsWith("--ndi-source=")) sourceFilter = arg["--ndi-source=".Length..];
    else if (arg.StartsWith("--duration-seconds=")) durationSeconds = int.Parse(arg["--duration-seconds=".Length..], CultureInfo.InvariantCulture);
    else if (arg.StartsWith("--output=")) outputPath = arg["--output=".Length..];
    else if (arg.StartsWith("--find-timeout-ms=")) findTimeoutMs = int.Parse(arg["--find-timeout-ms=".Length..], CultureInfo.InvariantCulture);
}

if (string.IsNullOrWhiteSpace(sourceFilter))
{
    Console.Error.WriteLine("usage: NdiTelemetryReceiver --ndi-source=\"NAME\" [--duration-seconds=30] [--output=stats.json] [--find-timeout-ms=5000]");
    return 2;
}

if (!NDIlib.initialize())
{
    Console.Error.WriteLine("NDIlib.initialize() failed");
    return 1;
}

try
{
    var findCfg = new NDIlib.find_create_t { show_local_sources = true };
    var finder = NDIlib.find_create_v2(ref findCfg);
    if (finder == IntPtr.Zero) { Console.Error.WriteLine("find_create_v2 returned null"); return 1; }

    NDIlib.source_t? matched = null;
    var deadline = Stopwatch.GetTimestamp() + (long)((double)findTimeoutMs / 1000.0 * Stopwatch.Frequency);
    while (Stopwatch.GetTimestamp() < deadline && matched is null)
    {
        NDIlib.find_wait_for_sources(finder, 500);
        uint count = 0;
        var ptr = NDIlib.find_get_current_sources(finder, ref count);
        if (count > 0 && ptr != IntPtr.Zero)
        {
            int stride = Marshal.SizeOf<NDIlib.source_t>();
            for (int i = 0; i < count; i++)
            {
                var s = Marshal.PtrToStructure<NDIlib.source_t>(IntPtr.Add(ptr, i * stride));
                var name = Marshal.PtrToStringAnsi(s.p_ndi_name) ?? "";
                if (name.Contains(sourceFilter, StringComparison.OrdinalIgnoreCase))
                {
                    matched = s;
                    Console.Error.WriteLine($"matched source: {name}");
                    break;
                }
            }
        }
    }

    if (matched is null)
    {
        NDIlib.find_destroy(finder);
        Console.Error.WriteLine($"No NDI source matching '{sourceFilter}' within {findTimeoutMs} ms");
        return 1;
    }

    var recvCfg = new NDIlib.recv_create_v3_t
    {
        source_to_connect_to = matched.Value,
        color_format = NDIlib.recv_color_format_e.recv_color_format_BGRX_BGRA,
        bandwidth = NDIlib.recv_bandwidth_e.recv_bandwidth_highest,
        allow_video_fields = false,
        p_ndi_recv_name = Marshal.StringToHGlobalAnsi("NdiTelemetryReceiver"),
    };
    var recv = NDIlib.recv_create_v3(ref recvCfg);
    NDIlib.find_destroy(finder);
    if (recv == IntPtr.Zero) { Console.Error.WriteLine("recv_create_v3 returned null"); return 1; }

    Console.Error.WriteLine($"connected; capturing for {durationSeconds}s");

    var arrivalTicks = new List<long>(durationSeconds * 70);
    var ndiTimestamps = new List<long>(durationSeconds * 70);
    int statusChanges = 0;
    int errors = 0;
    int videoFrames = 0;
    int frameRateN = 0, frameRateD = 0;
    int xres = 0, yres = 0;

    var endTicks = Stopwatch.GetTimestamp() + (long)((double)durationSeconds * Stopwatch.Frequency);
    var captureStart = Stopwatch.GetTimestamp();
    long? firstVideoTick = null;

    while (Stopwatch.GetTimestamp() < endTicks)
    {
        NDIlib.video_frame_t v = default;
        NDIlib.audio_frame_t a = default;
        NDIlib.metadata_frame_t m = default;
        var type = NDIlib.recv_capture(recv, ref v, ref a, ref m, 100);
        var nowTick = Stopwatch.GetTimestamp();
        switch (type)
        {
            case NDIlib.frame_type_e.frame_type_video:
                videoFrames++;
                if (firstVideoTick is null) firstVideoTick = nowTick;
                arrivalTicks.Add(nowTick);
                ndiTimestamps.Add(v.timecode);
                if (frameRateN == 0) { frameRateN = v.frame_rate_N; frameRateD = v.frame_rate_D; xres = v.xres; yres = v.yres; }
                NDIlib.recv_free_video(recv, ref v);
                break;
            case NDIlib.frame_type_e.frame_type_audio:
                // We don't measure audio cadence, but the NDI receive API requires
                // every returned frame to be released or buffers accumulate and the
                // receiver can stall, corrupting jitter measurements.
                NDIlib.recv_free_audio(recv, ref a);
                break;
            case NDIlib.frame_type_e.frame_type_metadata:
                NDIlib.recv_free_metadata(recv, ref m);
                break;
            case NDIlib.frame_type_e.frame_type_status_change:
                statusChanges++;
                break;
            case NDIlib.frame_type_e.frame_type_error:
                errors++;
                break;
        }
    }

    var captureEnd = Stopwatch.GetTimestamp();
    NDIlib.recv_destroy(recv);

    if (recvCfg.p_ndi_recv_name != IntPtr.Zero) Marshal.FreeHGlobal(recvCfg.p_ndi_recv_name);

    double tickToMs = 1000.0 / Stopwatch.Frequency;
    double actualDurationMs = (captureEnd - captureStart) * tickToMs;

    double targetIntervalMs = (frameRateN > 0 && frameRateD > 0) ? (1000.0 * frameRateD / frameRateN) : 0;

    var intervals = new double[Math.Max(0, arrivalTicks.Count - 1)];
    for (int i = 1; i < arrivalTicks.Count; i++)
        intervals[i - 1] = (arrivalTicks[i] - arrivalTicks[i - 1]) * tickToMs;

    double meanIntervalMs = intervals.Length > 0 ? intervals.Average() : 0;
    double maxGapMs = intervals.Length > 0 ? intervals.Max() : 0;
    double minGapMs = intervals.Length > 0 ? intervals.Min() : 0;

    double sumSqDev = 0;
    double peakDev = 0;
    int lateCount = 0;
    int veryLateCount = 0;
    foreach (var iv in intervals)
    {
        var dev = iv - targetIntervalMs;
        var ad = Math.Abs(dev);
        sumSqDev += dev * dev;
        if (ad > peakDev) peakDev = ad;
        if (targetIntervalMs > 0 && iv > 1.5 * targetIntervalMs) lateCount++;
        if (targetIntervalMs > 0 && iv > 2.5 * targetIntervalMs) veryLateCount++;
    }
    double jitterRmsMs = intervals.Length > 0 ? Math.Sqrt(sumSqDev / intervals.Length) : 0;

    double effectiveFps = actualDurationMs > 0 ? videoFrames * 1000.0 / actualDurationMs : 0;

    var stats = new
    {
        sourceMatched = sourceFilter,
        durationSeconds = actualDurationMs / 1000.0,
        videoFrames,
        statusChanges,
        errors,
        xres,
        yres,
        frameRateN,
        frameRateD,
        targetIntervalMs,
        effectiveFps,
        meanIntervalMs,
        minGapMs,
        maxGapMs,
        jitterRmsMs,
        jitterPeakMs = peakDev,
        lateCount,
        veryLateCount,
        intervalCount = intervals.Length,
    };

    var json = JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true });
    if (outputPath is not null)
    {
        File.WriteAllText(outputPath, json);
        Console.Error.WriteLine($"wrote {outputPath}");
    }
    Console.WriteLine(json);
    return 0;
}
finally
{
    NDIlib.destroy();
}
