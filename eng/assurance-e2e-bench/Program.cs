using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cohesive.Adapters.OpenTelemetry.Assurance;
using Cohesive.Assurance;
using Cohesive.Authoring;
using Cohesive.Model;
using Cohesive.Examples.Assurance;
using OpenTelemetry;
using OpenTelemetry.Trace;

// No network exporter: separate timed producer and consumer stages with an in-process SDK handoff.
// This measures serial service cost, not a load generator's offered throughput or queueing latency.
var output = args.Length == 0 ? "assurance-e2e-results.json" : args[0];
var cases = new List<Case>();
foreach (var mode in new[] { "plain-sdk", "sampled-out", "sampled-out-eager", "export-only", "monitor", "journal" })
    cases.Add(new(mode, "matched", 128, 1, 32));
foreach (var mode in new[] { "monitor", "journal" })
foreach (var rows in new[] { 128, 256 })
foreach (var batch in new[] { 1, 16 })
foreach (var shape in new[] { "matched", "pending", "state" })
    cases.Add(new(mode, shape, rows, batch, 32));
cases.Add(new("monitor", "matched", 256, 16, 2048));
cases.Add(new("journal", "matched", 256, 16, 2048));
cases = cases.Distinct().Where(x => args.Length < 2 || x.Mode == args[1]).ToList();
if (cases.Count == 0) throw new ArgumentException("Unknown benchmark mode.");
var results = new List<Run>();
foreach (var item in cases)
{
    Execute(item with { Rows = 16 }, repetition: -1); // Exercise the same path before measuring.
    for (var repeat = 0; repeat < 3; repeat++)
    {
        var result = Execute(item, repeat);
        results.Add(result);
        Console.WriteLine($"{item.Mode}/{item.Shape} rows={item.Rows} batch={item.Batch} payload={item.Padding} run={repeat}: {result.ServiceMilliseconds:F1} ms, {result.AllocatedBytes / 1024.0:F0} KiB");
    }
}
File.WriteAllText(output, JsonSerializer.Serialize(new
{
    CapturedAt = DateTimeOffset.UtcNow, Runtime = RuntimeInformation.FrameworkDescription,
    OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default",
    LogicalProcessors = Environment.ProcessorCount, StopwatchFrequency = Stopwatch.Frequency,
    Results = results
}, new JsonSerializerOptions { WriteIndented = true }));

static Run Execute(Case scenario, int repetition)
{
    var directory = Path.Combine(Path.GetTempPath(), "assurance-e2e-" + Guid.NewGuid());
    var fixture = new Fixture(scenario);
    var monitor = TraceMonitor.Start(fixture.Claim, fixture.Batch([]));
    using var exporter = new CaptureExporter();
    using var provider = Sdk.CreateTracerProviderBuilder()
        .SetSampler(scenario.Mode.StartsWith("sampled-out", StringComparison.Ordinal) ? new AlwaysOffSampler() : new AlwaysOnSampler())
        .AddCohesiveAssuranceInstrumentation()
        .AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
    FileCaptureHost? host = null;
    try
    {
        if (scenario.Mode == "journal") host = new(directory, "benchmark-source", monitor,
            maxDeliveries: scenario.Rows, maxJournalBytes: 256 * 1024 * 1024);
        var producer = new List<double>();
        var consumer = new List<double>();
        long allocations = 0, cpuTicks = 0, journalBytesWritten = 0, payloadBytes = 0;
        var acknowledgments = 0;
        var gcStart = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        long delivery = 0;
        for (var start = 0; start < scenario.Rows; start += scenario.Batch)
        {
            delivery++;
            var beforeAllocation = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            using (var activity = AssuranceActivityAdapter.Source.StartActivity("capture"))
            {
                // Guard payload construction with sampling, unlike an unconditional AddEvent argument.
                if (scenario.Mode != "plain-sdk" && (activity?.IsAllDataRequested == true || scenario.Mode == "sampled-out-eager" && activity is not null))
                    activity!.AddEvent(AssuranceActivityAdapter.CreateEvent(fixture.Batch(fixture.Rows(start))));
            }
            producer.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            allocations += GC.GetTotalAllocatedBytes(precise: true) - beforeAllocation;
            var captured = exporter.Last;
            exporter.Last = null;
            if (captured is null) continue;
            foreach (var item in captured.Events)
            foreach (var tag in item.Tags)
                if (tag.Key == AssuranceActivityAdapter.PayloadTag && tag.Value is string text)
                    payloadBytes += System.Text.Encoding.UTF8.GetByteCount(text);
            if (scenario.Mode is not ("monitor" or "journal")) continue;
            beforeAllocation = GC.GetTotalAllocatedBytes(precise: true);
            started = Stopwatch.GetTimestamp();
            var reference = Fixture.Ref("delivery-" + delivery);
            if (host is null) monitor = AssuranceActivityAdapter.Apply(monitor, captured, reference).Monitor;
            else host.Process(delivery, captured, reference, _ => acknowledgments++);
            consumer.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            allocations += GC.GetTotalAllocatedBytes(precise: true) - beforeAllocation;
            if (host is not null) journalBytesWritten += new FileInfo(Path.Combine(directory, "capture.json")).Length;
        }
        cpuTicks = (process.TotalProcessorTime - cpuStart).Ticks;
        var gc = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gcStart[i]).ToArray();
        var expectedExports = scenario.Mode.StartsWith("sampled-out", StringComparison.Ordinal) ? 0 : delivery;
        if (exporter.Count != expectedExports) throw new InvalidOperationException("Unexpected SDK sampling/export count.");
        var final = host?.Monitor ?? monitor;
        if (scenario.Mode is "monitor" or "journal")
        {
            if (final.Evaluation.Batch.Alternatives[0].Observations.Length != scenario.Rows
                || final.Evaluation.Verdict != ClaimVerdict.Inconclusive)
                throw new InvalidOperationException("Benchmark did not evaluate the intended open trace.");
            if (scenario.Shape == "matched" && final.Evaluation.Alternatives[0].Obligations.Any(x => x.Verdict != ClaimVerdict.Satisfied))
                throw new InvalidOperationException("Matched workload did not satisfy all observed obligations.");
            if (host is not null && (host.Position != delivery || acknowledgments != delivery || host.HasCaptureGaps))
                throw new InvalidOperationException("Persistence workload did not commit every delivery.");
        }
        // Recovery is a separate measurement, excluded from steady-state service cost.
        double restoreMs = 0;
        long restoreBytes = 0, finalJournalBytes = 0;
        if (host is not null)
        {
            finalJournalBytes = new FileInfo(Path.Combine(directory, "capture.json")).Length;
            host.Dispose(); host = null;
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            using var restored = new FileCaptureHost(directory, "benchmark-source",
                TraceMonitor.Start(fixture.Claim, fixture.Batch([])), maxDeliveries: scenario.Rows, maxJournalBytes: 256 * 1024 * 1024);
            restoreMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            restoreBytes = GC.GetTotalAllocatedBytes(precise: true) - before;
            if (restored.Monitor.Serialize() != final.Serialize()) throw new InvalidOperationException("Recovery changed checkpoint.");
        }
        var total = producer.Sum() + consumer.Sum();
        return new(scenario, repetition, total, scenario.Rows / (total / 1000),
            Percentile(producer, .5), Percentile(producer, .95), Percentile(producer, .99),
            Percentile(consumer, .5), Percentile(consumer, .95), Percentile(consumer, .99),
            consumer.Count == 0 ? 0 : consumer.Take(Math.Max(1, consumer.Count / 4)).Average(),
            consumer.Count == 0 ? 0 : consumer.TakeLast(Math.Max(1, consumer.Count / 4)).Average(),
            allocations, TimeSpan.FromTicks(cpuTicks).TotalMilliseconds, gc, payloadBytes,
            finalJournalBytes, journalBytesWritten, restoreMs, restoreBytes);
    }
    finally { host?.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
static double Percentile(List<double> values, double fraction) => values.Count == 0 ? 0
    : values.Order().ElementAt(Math.Min(values.Count - 1, (int)Math.Ceiling(values.Count * fraction) - 1));

record Case(string Mode, string Shape, int Rows, int Batch, int Padding);
record Run(Case Scenario, int Repetition, double ServiceMilliseconds, double RowsPerServiceSecond,
    double ProducerP50Ms, double ProducerP95Ms, double ProducerP99Ms,
    double ConsumerP50Ms, double ConsumerP95Ms, double ConsumerP99Ms,
    double FirstQuarterConsumerMeanMs, double LastQuarterConsumerMeanMs,
    long AllocatedBytes, double ProcessCpuMs, int[] Collections, long PayloadBytes,
    long FinalJournalBytes, long JournalBytesWritten, double RestoreMilliseconds, long RestoreAllocatedBytes);
sealed class CaptureExporter : BaseExporter<Activity>
{
    internal Activity? Last;
    internal long Count;
    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch) { Last = activity; Count++; }
        return ExportResult.Success;
    }
}
sealed class Fixture
{
    public sealed record Order(string Id, string Padding);
    public sealed record Stock(decimal Reserved, decimal OnHand, string Padding);
    internal static ClaimReference Ref(string id) => new(new("benchmark://" + id), "1", id);
    static readonly ClaimReference Trigger = Ref("paid"), Witness = Ref("shipped"), Mapping = Ref("mapping");
    readonly Case scenario;
    readonly string padding;
    internal ClaimDocument Claim { get; }
    internal Fixture(Case scenario)
    {
        this.scenario = scenario;
        padding = new('x', scenario.Padding);
        var author = new ExpressionAuthoring();
        var declaration = Cohesive.Assurance.Claim.Define("orders", "1", Ref("subject"), Mapping, Ref("source"), Ref("config"));
        Claim = scenario.Shape == "state"
            ? declaration.AtEveryCommittedState(Trigger, InitialStateSelection.Include)
                .Require(author.Input<Stock>("stock"), ClaimInputRole.State, s => s.Reserved <= s.OnHand).Build()
            : declaration.RequireWithin(Trigger, Witness, TimeSpan.FromTicks(10))
                .Require(author.Input<Order>("paid"), ClaimInputRole.Trigger, author.Input<Order>("shipped"), ClaimInputRole.Witness,
                    (paid, shipped) => paid.Id == shipped.Id).Build();
    }
    internal TraceObservationBatch Batch(ImmutableArray<SemanticObservation> rows) => new(
        new(Mapping, Ref("atomic-scope"), [new(Trigger, ["p"]), new(Witness, ["p"])]), Ref("target"), Ref("epoch"),
        [new(Ref("envelope-" + (rows.IsEmpty ? "empty" : rows[0].Occurrence)), rows, [])], EnumerationCompleteness.Complete, Ref("mapping-evidence"));
    internal ImmutableArray<SemanticObservation> Rows(int start)
    {
        var rows = ImmutableArray.CreateBuilder<SemanticObservation>(Math.Min(scenario.Batch, scenario.Rows - start));
        for (var i = start; i < Math.Min(start + scenario.Batch, scenario.Rows); i++)
        {
            var witness = scenario.Shape == "matched" && i % 2 == 1;
            rows.Add(scenario.Shape == "state"
                ? new("state-" + i, "p", i, i, Trigger, SemanticObservationKind.CommittedState,
                    ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty.Add(ClaimInputRole.State,
                        ObservationValue.FromObject(new Stock(5, 10, padding))))
                : new("event-" + i, "p", i, i, witness ? Witness : Trigger, SemanticObservationKind.Event,
                    ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty,
                    ObservationValue.FromObject(new Order("order-" + (scenario.Shape == "matched" ? i / 2 : i), padding))));
        }
        return rows.MoveToImmutable();
    }
}
