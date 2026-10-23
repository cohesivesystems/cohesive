using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("TraceAnalysis <existing-trace.nettrace>");
    return 2;
}

// Retain method-load metadata to resolve native instruction offsets, including tiered versions.
var etlx = TraceLog.CreateFromEventPipeDataFile(args[0],
    etlxFilePath: Path.ChangeExtension(args[0], ".profile.etlx"),
    options: new TraceLogOptions { KeepAllEvents = true });
using var log = new TraceLog(etlx);
var inclusive = new Dictionary<string, int>();
var self = new Dictionary<string, int>();
var versions = log.Events.OfType<MethodLoadUnloadVerboseTraceData>()
    .Select(item => new
    {
        start = item.MethodStartAddress,
        size = item.MethodSize,
        name = item.MethodNamespace + "." + item.MethodName,
        tier = item.OptimizationTier.ToString()
    })
    .Where(item => item.size > 0)
    .DistinctBy(item => item.start)
    .OrderBy(item => item.start)
    .ToArray();
// Copy payloads while enumerating: TraceLog reuses its event objects.
var inlining = new List<object>();
foreach (var item in log.Events)
{
    if (!item.EventName.Contains("Inlining", StringComparison.Ordinal)) continue;
    var payload = item.PayloadNames.ToDictionary(name => name, item.PayloadByName);
    if (payload.Values.OfType<string>().Any(value => value.Contains("ObservationJsonReader", StringComparison.Ordinal)))
        inlining.Add(new { name = item.EventName, payload });
}
var starts = versions.Select(item => item.start).ToArray();
var offsets = new Dictionary<string, int>();
int total = 0, selected = 0;
foreach (var item in log.Events)
{
    if (item.ProviderName != "Microsoft-DotNETCore-SampleProfiler") continue;
    total++;
    var frames = new List<string>();
    for (var stack = item.CallStack(); stack is not null; stack = stack.Caller)
        frames.Add(stack.CodeAddress.Method?.FullMethodName ?? stack.CodeAddress.ToString());
    if (!frames.Any(name => name.Contains("ObservationJsonParseProfile.Measure("))) continue;
    selected++;

    var leaf = item.CallStack()?.CodeAddress;
    if (leaf is not null)
    {
        var index = Array.BinarySearch(starts, leaf.Address);
        if (index < 0) index = ~index - 1;
        if (index >= 0 && leaf.Address < versions[index].start + (ulong)versions[index].size)
        {
            var version = versions[index];
            if (version.name.Contains("ObservationJsonReader") || version.name.Contains("TokenScope"))
            {
                var key = version.name + "|" + version.tier + "|" + (leaf.Address - version.start).ToString("X4");
                offsets[key] = offsets.GetValueOrDefault(key) + 1;
            }
        }
    }
    if (frames.Count > 0) self[frames[0]] = self.GetValueOrDefault(frames[0]) + 1;
    foreach (var frame in frames.Distinct()) inclusive[frame] = inclusive.GetValueOrDefault(frame) + 1;
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    total,
    selected,
    inlining,
    offsets = offsets.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
    versions = versions.Where(item => item.name.Contains("ObservationJsonReader") || item.name.Contains("TokenScope")).ToArray(),
    self = self.OrderByDescending(item => item.Value).ToArray(),
    inclusive = inclusive.OrderByDescending(item => item.Value).ToArray()
}, new JsonSerializerOptions { WriteIndented = true }));
return 0;
