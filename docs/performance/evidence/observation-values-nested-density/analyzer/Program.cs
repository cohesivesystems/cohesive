using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using System.Text.Json;
if (args[0] == "--api")
{
 var type = typeof(Microsoft.Diagnostics.Tracing.Parsers.Clr.MethodLoadUnloadVerboseTraceData);
 foreach (var property in type.GetProperties()) Console.WriteLine(property.Name + " " + property.PropertyType);
 return;
}
var etlx = TraceLog.CreateFromEventPipeDataFile(args[0], options: new TraceLogOptions { KeepAllEvents = true });
using var log = new TraceLog(etlx);
var inclusive = new Dictionary<string,int>();
var self = new Dictionary<string,int>();
var versions = log.Events.OfType<Microsoft.Diagnostics.Tracing.Parsers.Clr.MethodLoadUnloadVerboseTraceData>()
    .Select(e => new { start=e.MethodStartAddress, size=e.MethodSize, name=e.MethodNamespace+"."+e.MethodName, tier=e.OptimizationTier.ToString() })
    .Where(e => e.size > 0).DistinctBy(e => e.start).OrderBy(e => e.start).ToArray();
var starts = versions.Select(e => e.start).ToArray();
var offsets = new Dictionary<string, int>();
int total = 0, selected = 0;
foreach (var item in log.Events)
{
    if (item.ProviderName != "Microsoft-DotNETCore-SampleProfiler") continue;
    total++;
    var frames = new List<string>();
    for (var stack = item.CallStack(); stack != null; stack = stack.Caller)
        frames.Add(stack.CodeAddress.Method?.FullMethodName ?? stack.CodeAddress.ToString());
    if (!frames.Any(name => name.Contains("NestedLayoutProfile.Measure"))) continue;
    selected++;
    var leaf = item.CallStack()?.CodeAddress;
    if (leaf != null)
    {
        var index = Array.BinarySearch(starts, leaf.Address);
        if (index < 0) index = ~index - 1;
        if (index >= 0 && leaf.Address < versions[index].start + (ulong)versions[index].size)
        {
            var version = versions[index];
            if(version.name.Contains("ObservationJsonReader") || version.name.Contains("NestedLayout"))
            {
                var key = version.name + "|" + version.tier + "|" + (leaf.Address-version.start).ToString("X4");
                offsets[key]=offsets.GetValueOrDefault(key)+1;
            }
        }
    }
    if(frames.Count>0) self[frames[0]]=self.GetValueOrDefault(frames[0])+1;
    foreach (var frame in frames.Distinct()) inclusive[frame]=inclusive.GetValueOrDefault(frame)+1;
}
Console.WriteLine(JsonSerializer.Serialize(new { total, selected,
    offsets=offsets.OrderBy(p=>p.Key).ToArray(),
    versions=versions.Where(v=>v.name.Contains("ObservationJsonReader") || v.name.Contains("NestedLayout")).ToArray(),
    self=self.OrderByDescending(p=>p.Value).ToArray(),
    inclusive=inclusive.OrderByDescending(p=>p.Value).ToArray() }, new JsonSerializerOptions {WriteIndented=true}));
