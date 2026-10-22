using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Adapters.Edi;
using Cohesive.Adapters.Edi.X12;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

if (args.Length != 3 || !int.TryParse(args[1], out var stops) || stops < 2 || stops > 64
    || !int.TryParse(args[2], out var iterations) || iterations < 1 || iterations > 1000)
{
    Console.Error.WriteLine("Usage: EdiDataPlane ARI_ROOT STOP_COUNT(2..64) ITERATIONS(1..1000)");
    return 1;
}

var root = Path.GetFullPath(args[0]);
var raw = File.ReadAllText(Path.Combine(root,
    "datasets/ari/edi-transportation/acceptance/204-004010-ecf/v1/inputs/original.edi"));
// Scale the two-stop acceptance input, retaining header/party authorities and valid segment counts.
var firstStop = raw.IndexOf("S5*", StringComparison.Ordinal);
var trailer = raw.IndexOf("L3*", firstStop, StringComparison.Ordinal);
var segments = raw[firstStop..trailer].Split('~', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
var secondStop = Array.FindIndex(segments, 1, segment => segment.StartsWith("S5*", StringComparison.Ordinal));
var scaled = new StringBuilder(raw[..firstStop]);
for (var index = 0; index < stops; index++)
{
    var block = index % 2 == 0 ? segments.AsSpan(0, secondStop) : segments.AsSpan(secondStop);
    foreach (var segment in block)
    {
        if (segment.StartsWith("S5*", StringComparison.Ordinal))
        {
            var fields = segment.Split('*');
            fields[1] = (index + 1).ToString(CultureInfo.InvariantCulture);
            scaled.AppendJoin('*', fields);
        }
        else scaled.Append(segment);
        scaled.Append("~\n");
    }
}
scaled.Append(raw[trailer..]);
raw = scaled.ToString();
var segmentCount = raw.Split('~', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length;
var se = raw.IndexOf("SE*", StringComparison.Ordinal);
raw = raw[..se] + $"SE*{segmentCount.ToString(CultureInfo.InvariantCulture)}*0001~\n";

Console.WriteLine("stage,phase,stops,iterations,allocated_bytes,elapsed_ms,input_bytes,output_bytes,sha256");
var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
var started = Stopwatch.GetTimestamp();
var source = ShapeGraphDocument.FromGraph(X12Edi204v4010ShapeGraph.Default);
var target = JsonSerializer.Deserialize<ShapeGraphDocument>(File.ReadAllText(Path.Combine(root,
    "src/Ari.Training.Seeding/Definitions/edi-204-canonical.shape.json")), StrictDocumentJson.CreateOptions())!;
var relationDocument = RelationQueryJsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root,
    "src/Ari.Training.Scenarios/Transportation/Definitions/edi-204-v4010-to-canonical.relation.generated.json")));
var definition = (RelationDefinition)relationDocument.Definition;
var sourceShape = definition.Body.Nodes.OfType<SourceQueryNode>().Single().Shape.ShapeId;
var parser = new EdiParser();
var evaluator = RelationQueryEvaluator.CreateSuppliedOnly();
var compilation = new RelationQueryEvaluationBuilder(definition, new("benchmark/prepare"), [source, target]).Build().Compilation;
var output = new ArrayBufferWriter<byte>();
string? expectedOutputHash = null;
Report("prepare", "cold", 1, allocatedBefore, started, 0, "");

// First execution includes lazy parser/query preparation; do not prewarm it in verification.
await Measure("pipeline", "cold", 1);
for (var warm = 0; warm < 20; warm++) _ = await Execute();
for (var sample = 0; sample < 7; sample++) await Measure("pipeline", "warm", iterations);

// Diagnostic retained-heap sample: all N independent outputs stay alive across full collections.
// This is not an exact object-size census and is deliberately outside throughput measurements.
await RetainedSample();
return 0;

async Task<byte[]> Execute()
{
    var observation = parser.Parse(raw, new(source.Graph, sourceShape),
        new() { RejectUnexpectedSegments = true, ValidateNumericLexemes = true });
    var evaluation = new RelationQueryEvaluationBuilder(compilation, new("benchmark/edi204"))
        .Supply([observation], _ => "benchmark-row", evidenceReference: "benchmark/edi204").Build();
    var outcome = await evaluator.EvaluateAsync(evaluation);
    if (!outcome.IsSuccessful)
        throw new InvalidOperationException(string.Join("; ", outcome.Result?.Diagnostics.Select(d => d.Code + ": " + d.Message) ?? []));
    var rows = ((RelationQueryExecutionResult)outcome.Result!).Relation!.Rows;
    if (rows.Length != 1) throw new InvalidOperationException("Expected one mapped load.");
    var value = rows[0].Value;
    if (value.GetProperty("Stops").Array.Length != stops)
        throw new InvalidOperationException("Mapped stop count differs from the input.");
    output.Clear();
    using var writer = new Utf8JsonWriter(output);
    value.WriteTo(writer);
    writer.Flush();
    // The returned wire buffer is retained-result allocation, not temporary working allocation.
    return output.WrittenSpan.ToArray();
}

async Task Measure(string stage, string phase, int count)
{
    var before = GC.GetTotalAllocatedBytes(precise: true);
    var start = Stopwatch.GetTimestamp();
    byte[] result = [];
    for (var i = 0; i < count; i++) result = await Execute();
    var elapsed = Stopwatch.GetElapsedTime(start);
    var bytes = GC.GetTotalAllocatedBytes(precise: true) - before;
    var hash = Convert.ToHexStringLower(SHA256.HashData(result));
    if (expectedOutputHash is not null && hash != expectedOutputHash)
        throw new InvalidOperationException("The same input produced different output bytes.");
    expectedOutputHash = hash;
    Write(stage, phase, count, bytes, elapsed, result.Length, hash);
}

void Report(string stage, string phase, int count, long before, long start, int outputBytes, string hash) =>
    Write(stage, phase, count, GC.GetTotalAllocatedBytes(precise: true) - before, Stopwatch.GetElapsedTime(start), outputBytes, hash);

void Write(string stage, string phase, int count, long bytes, TimeSpan elapsed, int outputBytes, string hash) =>
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{stage},{phase},{stops},{count},{bytes},{elapsed.TotalMilliseconds:F3},{Encoding.UTF8.GetByteCount(raw)},{outputBytes},{hash}"));

[MethodImpl(MethodImplOptions.NoInlining)]
async Task RetainedSample()
{
    var results = new byte[32][];
    var before = GC.GetTotalMemory(forceFullCollection: true);
    for (var i = 0; i < results.Length; i++) results[i] = await Execute();
    var retained = GC.GetTotalMemory(forceFullCollection: true) - before;
    Console.Error.WriteLine($"Retained heap delta with {results.Length} wire outputs alive: {retained} bytes; includes surviving runtime caches, excludes native memory.");
    GC.KeepAlive(results);

    var observations = new Observation[32];
    before = GC.GetTotalMemory(forceFullCollection: true);
    for (var i = 0; i < observations.Length; i++)
        observations[i] = parser.Parse(raw, new(source.Graph, sourceShape),
            new() { RejectUnexpectedSegments = true, ValidateNumericLexemes = true });
    retained = GC.GetTotalMemory(forceFullCollection: true) - before;
    Console.Error.WriteLine($"Retained heap delta with {observations.Length} parsed observations alive: {retained} bytes; includes surviving runtime caches, excludes native memory.");
    GC.KeepAlive(observations);
}
