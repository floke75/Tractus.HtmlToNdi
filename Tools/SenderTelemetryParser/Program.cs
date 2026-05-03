using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

// Reads a sender log file (Serilog stdout dump) and extracts every
// "NDI video pipeline stats: …" line. Aggregates the field values across
// all stats lines emitted between --start-iso and --end-iso (UTC ISO-8601),
// then emits a single JSON object representing the run.
//
// Usage:
//   SenderTelemetryParser --log=path\to\sender.log [--start-iso=...] [--end-iso=...] [--output=stats.json]
//
// If --start-iso / --end-iso are omitted, all stats lines are aggregated.
// The aggregator picks the LAST matching stats line (which carries the
// largest cumulative counters and the most settled jitter values) plus
// summary deltas vs the FIRST matching stats line where applicable.

string? logPath = null;
string? startIso = null;
string? endIso = null;
string? outputPath = null;
foreach (var arg in args)
{
    if (arg.StartsWith("--log=")) logPath = arg["--log=".Length..];
    else if (arg.StartsWith("--start-iso=")) startIso = arg["--start-iso=".Length..];
    else if (arg.StartsWith("--end-iso=")) endIso = arg["--end-iso=".Length..];
    else if (arg.StartsWith("--output=")) outputPath = arg["--output=".Length..];
}

if (logPath is null || !File.Exists(logPath))
{
    Console.Error.WriteLine("usage: SenderTelemetryParser --log=path [--start-iso=...] [--end-iso=...] [--output=stats.json]");
    return 2;
}

DateTimeOffset? start = ParseIso(startIso);
DateTimeOffset? end = ParseIso(endIso);

// Sender log lines look like:
//   [16:55:01 INF] NDI video pipeline stats: captured=1280, sent=876, repeated=0, ...
// The Serilog default outputs only HH:mm:ss in local time; no date. We need to
// reattach a calendar date to each line so it can be filtered by --start-iso /
// --end-iso. Strategy: anchor on the start-iso's local-date and walk forward;
// if a later line's wall-clock HH:mm:ss is earlier than the previous one's, a
// midnight rollover happened, so increment the date. This handles capture
// windows that span midnight without ever consulting DateTime.Now (which would
// be wrong for any historical log file).
//
// If no start-iso is given, fall back to today's date as a best-effort base.

var lineTimeRegex = new Regex(@"^\[(?<h>\d{2}):(?<m>\d{2}):(?<s>\d{2})", RegexOptions.Compiled);
var statsRegex = new Regex(@"NDI video pipeline stats:\s*(?<body>.+?)(?:\s*\(caller=.+?\))?\s*$", RegexOptions.Compiled);

var anchorLocal = (start ?? DateTimeOffset.Now).LocalDateTime;
var baseDate = anchorLocal.Date;

var statsLines = new List<(DateTime localTime, string body)>();
TimeSpan? lastTimeOfDay = null;
foreach (var raw in File.ReadAllLines(logPath))
{
    var lineMatch = lineTimeRegex.Match(raw);
    if (!lineMatch.Success) continue;
    var h = int.Parse(lineMatch.Groups["h"].Value, CultureInfo.InvariantCulture);
    var m = int.Parse(lineMatch.Groups["m"].Value, CultureInfo.InvariantCulture);
    var s = int.Parse(lineMatch.Groups["s"].Value, CultureInfo.InvariantCulture);
    var tod = new TimeSpan(h, m, s);

    if (lastTimeOfDay.HasValue && tod < lastTimeOfDay.Value)
    {
        // Wall-clock went backwards => midnight rollover.
        baseDate = baseDate.AddDays(1);
    }
    lastTimeOfDay = tod;

    var lineDate = new DateTime(baseDate.Year, baseDate.Month, baseDate.Day, h, m, s, DateTimeKind.Local);

    // Edge case: the very first parsed line falls on the post-midnight side of
    // a rollover that happened BEFORE we saw any line (so the inter-line
    // monotonicity check above can't catch it). Anchor is e.g. 23:55 today;
    // the first stats line is 00:01 today (because baseDate = today), so
    // anchorLocal - lineDate ~= +23h. The line is actually tomorrow's, so
    // bump baseDate forward (the previous version of this branch went the
    // wrong direction).
    if (start.HasValue && statsLines.Count == 0 && (anchorLocal - lineDate) > TimeSpan.FromHours(12))
    {
        baseDate = baseDate.AddDays(1);
        lineDate = new DateTime(baseDate.Year, baseDate.Month, baseDate.Day, h, m, s, DateTimeKind.Local);
    }

    var statsMatch = statsRegex.Match(raw);
    if (statsMatch.Success)
    {
        statsLines.Add((lineDate, statsMatch.Groups["body"].Value));
    }
}

// Filter by time window
List<(DateTime localTime, string body)> windowed;
if (start.HasValue && end.HasValue)
{
    var startLocal = start.Value.LocalDateTime;
    var endLocal = end.Value.LocalDateTime;
    windowed = statsLines.Where(t => t.localTime >= startLocal && t.localTime <= endLocal).ToList();
}
else
{
    windowed = statsLines;
}

if (windowed.Count == 0)
{
    var empty = new
    {
        statsLineCount = 0,
        message = "no pipeline stats lines in window",
    };
    var emptyJson = JsonSerializer.Serialize(empty, new JsonSerializerOptions { WriteIndented = true });
    if (outputPath is not null) File.WriteAllText(outputPath, emptyJson);
    Console.WriteLine(emptyJson);
    return 0;
}

// Parse k=v fields from the LAST line — that one carries cumulative counters
// and the latest jitter values.
var last = windowed[^1].body;
var fields = ParseFields(last);

// Compute derived deltas vs the first line where useful.
var first = windowed[0].body;
var firstFields = ParseFields(first);

double GetD(Dictionary<string, string> dict, string key, double dflt = 0)
{
    if (dict.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
    return dflt;
}

long GetL(Dictionary<string, string> dict, string key, long dflt = 0)
{
    if (dict.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
    return dflt;
}

bool GetB(Dictionary<string, string> dict, string key, bool dflt = false)
{
    if (dict.TryGetValue(key, out var v) && bool.TryParse(v, out var b)) return b;
    return dflt;
}

var summary = new Dictionary<string, object?>
{
    ["statsLineCount"] = windowed.Count,
    ["windowStart"] = windowed[0].localTime.ToString("o", CultureInfo.InvariantCulture),
    ["windowEnd"] = windowed[^1].localTime.ToString("o", CultureInfo.InvariantCulture),
    ["windowSeconds"] = (windowed[^1].localTime - windowed[0].localTime).TotalSeconds,

    // Terminal counter values (cumulative since process start)
    ["captured"] = GetL(fields, "captured"),
    ["sent"] = GetL(fields, "sent"),
    ["repeated"] = GetL(fields, "repeated"),
    ["primed"] = GetB(fields, "primed"),
    ["buffered"] = GetL(fields, "buffered"),
    ["droppedOverflow"] = GetL(fields, "droppedOverflow"),
    ["droppedStale"] = GetL(fields, "droppedStale"),
    ["underruns"] = GetL(fields, "underruns"),
    ["warmups"] = GetL(fields, "warmups"),
    ["resyncDrops"] = GetL(fields, "resyncDrops"),

    // Counter deltas across the window
    ["capturedDelta"] = GetL(fields, "captured") - GetL(firstFields, "captured"),
    ["sentDelta"] = GetL(fields, "sent") - GetL(firstFields, "sent"),
    ["repeatedDelta"] = GetL(fields, "repeated") - GetL(firstFields, "repeated"),
    ["droppedOverflowDelta"] = GetL(fields, "droppedOverflow") - GetL(firstFields, "droppedOverflow"),
    ["underrunsDelta"] = GetL(fields, "underruns") - GetL(firstFields, "underruns"),

    // Latest pacing/jitter values
    ["latencyError"] = GetD(fields, "latencyError"),
    ["pacedInvalidation"] = GetB(fields, "pacedInvalidation"),
    ["pacedPaused"] = GetB(fields, "pacedPaused"),
    ["pacedOffsetMs"] = GetD(fields, "pacedOffsetMs"),
    ["cadenceAdaptation"] = GetB(fields, "cadenceAdaptation"),
    ["pacedHighResTimer"] = GetB(fields, "pacedHighResTimer"),
    ["pacedLatencyMs"] = GetD(fields, "pacedLatencyMs"),
    ["pendingInvalidations"] = GetL(fields, "pendingInvalidations"),

    ["captureCadenceFps"] = GetD(fields, "captureCadenceFps"),
    ["captureCadencePercent"] = GetD(fields, "captureCadencePercent"),
    ["captureCadenceShortfallPercent"] = GetD(fields, "captureCadenceShortfallPercent"),
    ["captureJitterRmsMs"] = GetD(fields, "captureJitterRmsMs"),
    ["captureJitterPkMs"] = GetD(fields, "captureJitterPkMs"),
    ["captureDriftMs"] = GetD(fields, "captureDriftMs"),

    ["outputJitterRmsMs"] = GetD(fields, "outputJitterRmsMs"),
    ["outputJitterPkMs"] = GetD(fields, "outputJitterPkMs"),
    ["outputDriftMs"] = GetD(fields, "outputDriftMs"),
    ["driftDeltaFrames"] = GetD(fields, "driftDeltaFrames"),

    ["compositorCapture"] = GetB(fields, "compositorCapture"),
    ["compositorFrames"] = GetL(fields, "compositorFrames"),
    ["legacyInvalidationFrames"] = GetL(fields, "legacyInvalidationFrames"),
    ["clockPrecisionNs"] = GetD(fields, "clockPrecisionNs"),
    ["stopwatchHighResolution"] = GetB(fields, "stopwatchHighResolution"),
};

var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
if (outputPath is not null)
{
    File.WriteAllText(outputPath, json);
    Console.Error.WriteLine($"wrote {outputPath}");
}
Console.WriteLine(json);
return 0;

static DateTimeOffset? ParseIso(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}

static Dictionary<string, string> ParseFields(string body)
{
    // Split on ", " and pick out k=v pairs.
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var part in body.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    {
        var eq = part.IndexOf('=');
        if (eq <= 0) continue;
        var k = part[..eq].Trim();
        var v = part[(eq + 1)..].Trim();
        dict[k] = v;
    }
    return dict;
}
