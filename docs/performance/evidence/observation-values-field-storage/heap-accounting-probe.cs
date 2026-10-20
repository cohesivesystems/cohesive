using Cohesive.Model;
using Cohesive.Relations.Benchmarks;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
var bench = new ObservationJsonFieldStorageBenchmarks { Width = 4 };bench.Setup();
var values = new ObservationValue[20000];
static object Info() { var x = GC.GetGCMemoryInfo(); return new { x.HeapSizeBytes, x.FragmentedBytes, Generations = x.GenerationInfo.ToArray().Select(g=>new {g.SizeBeforeBytes,g.SizeAfterBytes,g.FragmentationAfterBytes}).ToArray()}; }
var before=GC.GetTotalMemory(true);var beforeInfo=Info();
var allBefore=GC.GetTotalAllocatedBytes(true);var threadBefore=GC.GetAllocatedBytesForCurrentThread();
for(var i=0;i<values.Length;i++)values[i]=bench.Parse();
var allocated=GC.GetAllocatedBytesForCurrentThread()-threadBefore;var allAllocated=GC.GetTotalAllocatedBytes(true)-allBefore;
var after=GC.GetTotalMemory(true);var afterInfo=Info();
GC.Collect(2,GCCollectionMode.Forced,blocking:true,compacting:true);GC.WaitForPendingFinalizers();GC.Collect(2,GCCollectionMode.Forced,blocking:true,compacting:true);
var compacted=GC.GetTotalMemory(false);var compactedInfo=Info();
Console.WriteLine(JsonSerializer.Serialize(new {before,after,compacted,allocated,allAllocated,beforeInfo,afterInfo,compactedInfo}));
Console.WriteLine(string.Join("\n",typeof(RuntimeHelpers).GetMethods(BindingFlags.NonPublic|BindingFlags.Static).Where(m=>m.Name.Contains("Size")||m.Name.Contains("Raw")).Select(m=>m.ToString())));
GC.KeepAlive(values);GC.KeepAlive(bench);
