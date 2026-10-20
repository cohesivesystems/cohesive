using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Benchmark-only shared child layouts against existing fully admitted nested ingestion.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class ObservationNestedLayoutBenchmarks
{
    GraphShapeId shape;
    ObservationLayout rootLayout = null!;
    NestedLayoutCandidate candidate = null!;
    NestedLayoutCandidate adaptive = null!;
    ObservationValue densityShared;
    byte[] json = null!;
    ObservationValue existing;
    ObservationValue shared;
    string[] names = null!;
    int[] ordinals = null!;
    long preparationBytes;
    long adaptivePreparationBytes;

    /// <summary>Declared scalar fields in each repeated leaf record.</summary>
    [Params(4, 16, 32, 256)]
    public int Width { get; set; } = 16;

    /// <summary>Rows in an array; single-record scenarios use one.</summary>
    [Params(1, 128, 4096)]
    public int Count { get; set; } = 128;

    /// <summary>Inline record, nested chain, repeated inline/named records, or sparse/reordered arrays.</summary>
    [Params("record", "nested", "array", "named-array", "sparse-array", "reordered-array", "case-array", "mixed-array")]
    public string Scenario { get; set; } = "array";

    /// <summary>Percentage of declared fields present in density fixtures; mixed arrays alternate quarter/full rows.</summary>
    [Params(6, 25, 50, 75, 100)]
    public int PresentPercent { get; set; } = 100;

    /// <summary>Prepares schemas and both interpretations and verifies complete value and canonical equivalence.</summary>
    /// <exception cref="InvalidOperationException">The interpretations, validation, hashing or reads disagree.</exception>
    [GlobalSetup]
    public void Setup()
    {
        ImmutableArray<ObjectFieldTypeDef> fields = [.. Enumerable.Range(0, Width).Select(index =>
            new ObjectFieldTypeDef("field" + index, new ScalarTypeRef(ScalarTypeKind.Int64),
                presence: index == 0 ? FieldPresence.Required : FieldPresence.Optional,
                nullability: FieldNullability.Nullable))];
        TypeRef leaf = new ObjectTypeRef(fields);
        ImmutableArray<TypeDefinition> types = [];
        if (Scenario == "named-array")
        {
            TypeDefinition.Structural named = new(new("row"), [.. fields.Select(field =>
                new StructuralField(new(field.Name), field.Type, field.Cardinality, field.Presence, field.Nullability))]);
            types = [named];
            leaf = new NamedTypeRef(named.Id);
        }
        if (Scenario == "nested")
            for (var depth = 0; depth < 4; depth++) leaf = new ObjectTypeRef([new("child", leaf)]);
        Shape definition = new(new("envelope"), [new(new("payload"), leaf,
            cardinality: IsArray ? FieldCardinality.Many : FieldCardinality.Single)]);
        ShapeGraph graph = new(new("nested-layout-benchmark"), [definition], types);
        Initialize(new(graph, definition.Id), MakeJson());
    }

    void Initialize(GraphShapeId governingShape, byte[] input)
    {
        shape = governingShape;
        rootLayout = ObservationLayout.Create(shape);
        var before = GC.GetAllocatedBytesForCurrentThread();
        candidate = new(shape);
        preparationBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        adaptive = new(shape, NestedLayoutStorage.DensityAware);
        adaptivePreparationBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        json = input;
        names = ["field0", "field" + Width / 2, "field" + (Width - 1), "missing"];
        ordinals = [0, Width / 2, Width - 1, -1];
        existing = ParseExisting();
        shared = candidate.Parse(json, out var fellBack);
        if (fellBack != (Scenario == "case-array")) throw new InvalidOperationException("Unexpected fixture fallback.");
        Equivalent(existing, shared);
        Equivalent(existing, candidate.ParseSeparated(json, out _));
        densityShared = adaptive.ParseSeparated(json, out _);
        Equivalent(existing, densityShared);
        if (Read(existing, ordinal: false) != Read(densityShared, ordinal: true)) throw new InvalidOperationException("Adaptive reads differ.");
        if (ReadExisting() != ReadSharedNames() || (!fellBack && ReadExisting() != ReadOrdinals()))
            throw new InvalidOperationException("Nested field reads differ.");
    }

    bool IsArray => Scenario is not ("record" or "nested");

    byte[] MakeJson()
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WritePropertyName("payload");
            if (IsArray) writer.WriteStartArray();
            var rows = IsArray ? Count : 1;
            for (var row = 0; row < rows; row++)
            {
                if (Scenario == "nested")
                    for (var depth = 0; depth < 4; depth++) { writer.WriteStartObject(); writer.WritePropertyName("child"); }
                writer.WriteStartObject();
                for (var offset = 0; offset < Width; offset++)
                {
                    var field = Scenario == "reordered-array" ? Width - 1 - offset : offset;
                    if (Scenario == "sparse-array" && field % 4 != 0) continue;
                    var percentage = Scenario == "mixed-array" ? (row % 2 == 0 ? 25 : 100) : PresentPercent;
                    if (Scenario is "array" or "mixed-array" && field * 100 / Width >= percentage) continue;
                    writer.WriteNumber((Scenario == "case-array" ? "FIELD" : "field") + field, row + field);
                }
                writer.WriteEndObject();
                if (Scenario == "nested") for (var depth = 0; depth < 4; depth++) writer.WriteEndObject();
            }
            if (IsArray) writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return output.WrittenSpan.ToArray();
    }

    /// <summary>Parses and fully admits the existing typed nested representation.</summary>
    /// <returns>A freshly owned complete record tree.</returns>
    [Benchmark]
    public ObservationValue ParseExisting() => NestedLayoutCandidate.ParseExisting(shape, rootLayout, json);

    /// <summary>Parses and fully admits benchmark-only shared nested storage or its semantic fallback.</summary>
    /// <returns>A freshly owned complete record tree.</returns>
    [Benchmark]
    public ObservationValue ParseShared() => candidate.Parse(json, out _);

    /// <summary>Runs fixed vectors with fallback separated from stack-allocated admission.</summary>
    /// <returns>A freshly owned admitted tree.</returns>
    [Benchmark]
    public ObservationValue ParseSharedSeparated() => candidate.ParseSeparated(json, out _);

    /// <summary>Selects compact or ordinal storage from actual nested record occupancy.</summary>
    /// <returns>A freshly owned admitted tree; pooled scratch never escapes.</returns>
    [Benchmark]
    public ObservationValue ParseDensityAware() => adaptive.ParseSeparated(json, out _);

    /// <summary>Reads ordinals where retained storage supports them, otherwise existing exact names.</summary>
    /// <returns>The equivalent complete checksum.</returns>
    [Benchmark]
    public long ReadDensityAware() => Read(densityShared, ordinal: true);

    /// <summary>Includes fixed-vector parsing, admission and prebound reads with separated fallback.</summary>
    /// <returns>The complete checksum.</returns>
    [Benchmark]
    public long ParseSharedSeparatedAndRead() => Read(ParseSharedSeparated(), ordinal: true);

    /// <summary>Includes occupancy selection, admission and supported prebound reads.</summary>
    /// <returns>The complete checksum.</returns>
    [Benchmark]
    public long ParseDensityAwareAndRead() => Read(ParseDensityAware(), ordinal: true);

    /// <summary>Profiles the fallback call chain without executing the speculative prefix.</summary>
    /// <returns>The existing fully admitted result.</returns>
    [Benchmark]
    public ObservationValue ParseForcedFallback() => candidate.ParseForcedFallback(json);

    /// <summary>Measures only the speculative prefix; not an ingestion operation.</summary>
    /// <returns>Whether the prefix can parse the fixture without fallback.</returns>
    [Benchmark]
    public bool ProbeShared() => candidate.Probe(json);

    /// <summary>Performs sixteen name reads per retained leaf, including missing fields.</summary>
    /// <returns>The checksum of all successful integer reads.</returns>
    [Benchmark]
    public long ReadExisting() => Read(existing, ordinal: false);

    /// <summary>Performs equivalent name reads on shared field storage.</summary>
    /// <returns>The same checksum.</returns>
    [Benchmark]
    public long ReadSharedNames() => Read(shared, ordinal: false);

    /// <summary>Performs equivalent reads using ordinals bound during setup.</summary>
    /// <returns>The same checksum.</returns>
    [Benchmark]
    public long ReadOrdinals() => Read(shared, ordinal: true);

    /// <summary>Includes construction, full admission and sixteen field reads per leaf.</summary>
    /// <returns>The checksum of the freshly parsed tree.</returns>
    [Benchmark]
    public long ParseExistingAndRead() => Read(ParseExisting(), ordinal: false);

    /// <summary>Includes shared construction, full admission and prebound reads.</summary>
    /// <returns>The equivalent checksum.</returns>
    [Benchmark]
    public long ParseSharedAndRead() => Read(ParseShared(), ordinal: true);

    long Read(ObservationValue value, bool ordinal)
    {
        var payload = value.GetProperty("payload");
        if (!IsArray)
        {
            if (Scenario == "nested") for (var depth = 0; depth < 4; depth++) payload = payload.GetProperty("child");
            return ReadRow(payload, ordinal);
        }
        long sum = 0;
        for (var row = 0; row < payload.Array.Length; row++) sum += ReadRow(payload.Array[row], ordinal);
        return sum;
    }

    long ReadRow(ObservationValue row, bool ordinal)
    {
        long sum = 0;
        if (ordinal && row.Fields is IOrdinalObservationFieldReader reader)
        {
            for (var index = 0; index < 16; index++)
                if (reader.TryGetField(ordinals[index & 3], out var field)) sum += field.Int64;
        }
        else
            for (var index = 0; index < 16; index++)
                if (row.TryGetProperty(names[index & 3], out var field)) sum += field.Int64;
        return sum;
    }

    static void Equivalent(ObservationValue expected, ObservationValue actual)
    {
        if (!expected.Equals(actual) || expected.GetHashCode() != actual.GetHashCode()
            || !Canonical(expected).AsSpan().SequenceEqual(Canonical(actual)))
            throw new InvalidOperationException("Complete nested interpretations differ.");
    }

    static byte[] Canonical(ObservationValue value)
    {
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output);
        CanonicalJsonWriter.WriteCanonicalObservationValue(writer, value);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    /// <summary>Checks preserved nested semantics and deterministic allocation/ownership before timing.</summary>
    /// <returns>Zero after successful differential checks; throws on divergence.</returns>
    /// <exception cref="InvalidOperationException">The candidate changes a semantic outcome or ownership bound.</exception>
    public static int RunConformance() => NestedLayoutConformance.Run();

    /// <summary>Measures live retained trees, excluding prepared metadata and the retention array.</summary>
    /// <param name="args">Width, row count, scenario, existing/shared/density route, retained tree count, and optional present percentage.</param>
    /// <returns>Zero with counters; two for invalid arguments.</returns>
    /// <exception cref="InvalidOperationException">Fixture interpretations disagree.</exception>
    public static int RunRetained(string[] args)
    {
        if (args.Length is not (5 or 6) || !int.TryParse(args[0], out var width) || width is < 1 or > 256
            || !int.TryParse(args[1], out var rows) || rows is < 1 or > 4096
            || args[2] is not ("record" or "nested" or "array" or "named-array" or "sparse-array" or "reordered-array" or "mixed-array")
            || args[3] is not ("existing" or "shared" or "density") || !int.TryParse(args[4], out var count) || count is < 1 or > 20000)
            return 2;
        var percent = 100;
        if (args.Length == 6 && (!int.TryParse(args[5], out percent) || percent is < 1 or > 100)) return 2;
        var benchmark = new ObservationNestedLayoutBenchmarks { Width = width, Count = rows, Scenario = args[2], PresentPercent = percent };
        benchmark.Setup();
        Func<ObservationValue> parse = args[3] switch { "shared" => benchmark.ParseSharedSeparated,
            "density" => benchmark.ParseDensityAware, _ => benchmark.ParseExisting };
        for (var index = 0; index < 100; index++) _ = parse();
        var values = new ObservationValue[count];
        var beforeHeap = ObservationBenchmarkMemory.CollectLiveBytes();
        var beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++) values[index] = parse();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation;
        var retained = ObservationBenchmarkMemory.CollectLiveBytes() - beforeHeap;
        GC.KeepAlive(values); GC.KeepAlive(benchmark);
        Console.WriteLine(FormattableString.Invariant($"width={width},rows={rows},scenario={args[2]},present_percent={percent},route={args[3]},trees={count},allocated_bytes={allocated},retained_bytes={retained},preparation_allocated_bytes={(args[3] == "density" ? benchmark.adaptivePreparationBytes : benchmark.preparationBytes)},projected_layouts={benchmark.candidate.ProjectedLayoutCount}"));
        return 0;
    }
}
