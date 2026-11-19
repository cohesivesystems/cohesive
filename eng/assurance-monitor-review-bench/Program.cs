using System.Collections.Immutable;
using System.Diagnostics;
using Cohesive.Assurance;
using Cohesive.Authoring;
using Cohesive.Model;

static ClaimReference R(string id) => new(new("bench://" + id), "1", id);
var input = new ExpressionAuthoring().Input<Stock>("stock");
var claim = Claim.Define("stock", "1", R("subject"), R("mapping"), R("source"), R("config"))
    .AtEveryCommittedState(R("states"), InitialStateSelection.Include)
    .Require(input, ClaimInputRole.State, stock => stock.Quantity >= 0m).Build();

foreach (var count in new[] { 100, 2000 })
{
    var observations = Enumerable.Range(1, count).Select(index => new SemanticObservation(
        "s" + index, "p", index, index, R("states"), SemanticObservationKind.CommittedState,
        ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty.Add(
            ClaimInputRole.State, ObservationValue.FromObject(new Stock(index))))).ToImmutableArray();
    var batch = new TraceObservationBatch(
        new(R("mapping"), R("scope"), [new(R("states"), ["p"])]), R("target"), R("epoch"),
        [new(R("current"), observations, [])], EnumerationCompleteness.Complete, R("enumeration"));
    var monitor = TraceMonitor.Start(claim, batch);
    var replay = new TraceObservationAlternative(R("current"), [observations[^1]], []);
    var append = new TraceObservationAlternative(R("next"),
        [observations[^1] with { Occurrence = "next", Step = count + 1, Ticks = count + 1 }], []);
    Measure("Append", () => monitor.Advance(append));
    Measure("Replay", () => monitor.Advance(replay));
    Measure("Reference", () => monitor.Reference());

    void Measure(string name, Func<object> action)
    {
        const int warmups = 5, iterations = 30;
        for (var iteration = 0; iteration < warmups; iteration++) action();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        for (var iteration = 0; iteration < iterations; iteration++) action();
        timer.Stop();
        var bytes = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations;
        Console.WriteLine($"{name} rows={count}: {timer.Elapsed.TotalMilliseconds / iterations:F4} ms; {bytes} bytes/op");
    }
}

record Stock(decimal Quantity);
