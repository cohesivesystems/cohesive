using System.Collections.Immutable;
using System.Diagnostics;
using Cohesive.Assurance;
using Cohesive.Model.Serialization;

static ClaimReference Ref(string name) => new(new("benchmark://" + name), "1", name);
var options = StrictDocumentJson.CreateOptions();
foreach (var count in new[] { 1, 16, 128 })
{
    var expected = new TraceObservationContract(Ref("contract"), Ref("scope"),
        [.. Enumerable.Range(0, count).Select(i => new ObservationRequirement(Ref("selector-" + i), ["a", "b"]))]);
    // Separate instances, as after transport decoding. Setup is outside measurements.
    var received = new TraceObservationContract(Ref("contract"), Ref("scope"),
        [.. Enumerable.Range(0, count).Select(i => new ObservationRequirement(Ref("selector-" + i), ["a", "b"]))]);
    Measure("JSON", count, () => StrictDocumentJson.GetCanonicalBytes(expected, options).AsSpan()
        .SequenceEqual(StrictDocumentJson.GetCanonicalBytes(received, options)));
    Measure("SameContent", count, () => expected.SameContent(received));
}

static void Measure(string method, int requirements, Func<bool> compare)
{
    const int iterations = 2000;
    for (var i = 0; i < 200; i++) if (!compare()) throw new InvalidOperationException();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var bytes = GC.GetAllocatedBytesForCurrentThread();
    var start = Stopwatch.GetTimestamp();
    for (var i = 0; i < iterations; i++) if (!compare()) throw new InvalidOperationException();
    var elapsed = Stopwatch.GetElapsedTime(start);
    bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
    Console.WriteLine($"{requirements} | {method} | {elapsed.TotalMicroseconds / iterations:F3} us | {bytes / iterations} B");
}
