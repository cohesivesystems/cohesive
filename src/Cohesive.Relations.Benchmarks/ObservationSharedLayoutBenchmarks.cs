using System.Buffers;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Physical;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Validated JSON ingestion and repeated reads with per-object names or one shared graph-qualified layout.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class ObservationSharedLayoutBenchmarks
{
    GraphShapeId shape;
    Shape definition = null!;
    ObservationLayout layout = null!;
    byte[] json = null!;
    string[] names = null!;
    int[] ordinals = null!;
    ObservationValue generic;
    ObservationValue shared;
    IOrdinalObservationFieldReader ordinalReader = null!;
    long layoutAllocation;
    RelationQueryObservationOccurrence occurrence = null!;

    /// <summary>The number of required Int64 fields per record.</summary>
    [Params(4, 16, 32)]
    public int Width { get; set; } = 16;

    /// <summary>Reads following each parse; zero isolates validated construction.</summary>
    [Params(0, 1, 16, 64)]
    public int Reads { get; set; } = 16;

    /// <summary>Creates one shared layout and binds query names once, checking exact canonical and lookup equivalence.</summary>
    /// <exception cref="InvalidOperationException">The fixture's validated interpretations differ.</exception>
    [GlobalSetup]
    public void Setup()
    {
        definition = new(new("shared-record"), [.. Enumerable.Range(0, Width)
            .Select(index => new FieldDefinition(new("field" + index), new ScalarTypeRef(ScalarTypeKind.Int64)))]);
        ShapeGraph graph = new(new("shared-record-benchmark"), [definition]);
        shape = new(graph, definition.Id);
        var before = GC.GetAllocatedBytesForCurrentThread();
        layout = ObservationLayout.Create(shape);
        layoutAllocation = GC.GetAllocatedBytesForCurrentThread() - before;
        if (!ReferenceEquals(layout, ObservationLayout.Create(shape)))
            throw new InvalidOperationException("The layout was not shared for this graph/shape.");
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            for (var index = Width - 1; index >= 0; index--) writer.WriteNumber("field" + index, index);
            writer.WriteEndObject();
        }
        json = output.WrittenSpan.ToArray();
        occurrence = new(new("shared-record/0"), new("shared-record"), shape.QualifiedId, "shared-record/0");
        names = [new("field0".ToCharArray()), new(("field" + Width / 2).ToCharArray()),
            new(("field" + (Width - 1)).ToCharArray()), new("missing".ToCharArray())];
        ordinals = [.. names.Select(name => layout.TryGetOrdinal(name, out var ordinal) ? ordinal : -1)];
        generic = ParseGeneric();
        shared = ParseShared();
        ordinalReader = (IOrdinalObservationFieldReader)shared.Fields!;
        if (!generic.Equals(shared) || generic.GetHashCode() != shared.GetHashCode()
            || !Canonical(generic).AsSpan().SequenceEqual(Canonical(shared))
            || ReadGeneric() != ReadOrdinal() || ReadGeneric() != ReadSharedNames()
            || ReadGeneric() != ReadOrdinals(IndexedObservationOccurrence.FromJson(shape, occurrence, layout, json)))
            throw new InvalidOperationException("Shared-layout fixture changed values, canonical bytes or query results.");
    }

    /// <summary>Parses a generic object, validates it once against the shape, then reads by name.</summary>
    /// <returns>The sum of read values, or the object kind when construction alone is measured.</returns>
    [Benchmark]
    public long ParseGenericAndRead()
    {
        var value = ParseGeneric();
        return Reads == 0 ? (int)value.Kind : ReadNames(value);
    }

    /// <summary>Parses and validates into owned ordinal storage, then reads through prebound ordinals.</summary>
    /// <returns>The sum of read values, or the object kind when construction alone is measured.</returns>
    [Benchmark]
    public long ParseSharedAndRead()
    {
        var value = ParseShared();
        return Reads == 0 ? (int)value.Kind : ReadOrdinals((IOrdinalObservationFieldReader)value.Fields!);
    }

    /// <summary>Uses the public indexed JSON ingestion boundary, then reads by prebound ordinals.</summary>
    /// <returns>The sum of read values, or field count when construction alone is measured.</returns>
    [Benchmark]
    public long ParseIndexedAndRead()
    {
        var indexed = IndexedObservationOccurrence.FromJson(shape, occurrence, layout, json);
        return Reads == 0 ? layout.Count : ReadOrdinals(indexed);
    }

    /// <summary>Reads existing generic storage by independently owned query names.</summary>
    /// <returns>The sum of all hits; misses contribute zero.</returns>
    [Benchmark]
    public long ReadGeneric() => ReadNames(generic);

    /// <summary>Reads existing shared storage by name, isolating storage from ordinal binding.</summary>
    /// <returns>The sum of all hits; misses contribute zero.</returns>
    [Benchmark]
    public long ReadSharedNames() => ReadNames(shared);

    /// <summary>Reads existing shared storage by ordinals bound to its exact layout during setup.</summary>
    /// <returns>The sum of all hits; invalid ordinals contribute zero.</returns>
    [Benchmark]
    public long ReadOrdinal() => ReadOrdinals(ordinalReader);

    ObservationValue ParseGeneric()
    {
        var reader = new Utf8JsonReader(json);
        reader.Read();
        var value = ObservationJsonReader.ReadValue(ref reader);
        if (reader.Read()) throw new JsonException("Trailing content.");
        if (!ObservationValidator.TryValidateAgainstShape(value, definition, out var error, shape.Graph))
            throw new InvalidOperationException(error);
        return value;
    }

    ObservationValue ParseShared()
    {
        var values = new ObservationValue[layout.Count];
        Span<ulong> presence = stackalloc ulong[1];
        var reader = new Utf8JsonReader(json);
        reader.Read();
        if (!ObservationJsonReader.TryReadShape(ref reader, shape, layout, values, presence, out var error))
            throw new InvalidOperationException(error);
        if (reader.Read()) throw new JsonException("Trailing content.");
        // Benchmark-only composition of existing primitives: validation has completed and the fresh
        // array transfers once. This is not another public ingestion API or semantic authority.
        return ObservationValue.FromOrdinalFields(new OrdinalObservationFields(layout,
            ImmutableCollectionsMarshal.AsImmutableArray(values), BitOperations.PopCount(presence[0])));
    }

    long ReadNames(ObservationValue value)
    {
        long sum = 0;
        for (var index = 0; index < Reads; index++)
            if (value.TryGetProperty(names[index & 3], out var field)) sum += field.Int64;
        return sum;
    }

    long ReadOrdinals(IOrdinalObservationFieldReader value)
    {
        long sum = 0;
        for (var index = 0; index < Reads; index++)
            if (value.TryGetField(ordinals[index & 3], out var field)) sum += field.Int64;
        return sum;
    }

    static byte[] Canonical(ObservationValue value)
    {
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output);
        CanonicalJsonWriter.WriteCanonicalObservationValue(writer, value);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    /// <summary>Measures surviving storage for repeated records while retaining one shared layout.</summary>
    /// <param name="args">Width (4, 16 or 32), route (generic or shared), and count from 1000 through 100000.</param>
    /// <returns>Zero with allocation, live heap and setup allocation counters; two for invalid arguments.</returns>
    /// <exception cref="InvalidOperationException">Fixture parsing, validation or equivalence checks fail.</exception>
    public static int RunRetained(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[0], out var width) || width is not (4 or 16 or 32)
            || args[1] is not ("generic" or "shared") || !int.TryParse(args[2], out var count) || count is < 1000 or > 100000)
        {
            Console.Error.WriteLine("--shared-layout-retained <4|16|32> <generic|shared> <count:1000..100000>");
            return 2;
        }
        var benchmark = new ObservationSharedLayoutBenchmarks { Width = width };
        benchmark.Setup();
        var useShared = args[1] == "shared";
        for (var index = 0; index < 1000; index++) _ = useShared ? benchmark.ParseShared() : benchmark.ParseGeneric();
        var values = new ObservationValue[count];
        var beforeHeap = ObservationBenchmarkMemory.CollectLiveBytes();
        var beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++) values[index] = useShared ? benchmark.ParseShared() : benchmark.ParseGeneric();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation;
        var retained = ObservationBenchmarkMemory.CollectLiveBytes() - beforeHeap;
        GC.KeepAlive(values);
        GC.KeepAlive(benchmark);
        Console.WriteLine(FormattableString.Invariant($"width={width},route={args[1]},count={count},allocated_bytes={allocated},retained_bytes={retained},layout_setup_allocated_bytes={benchmark.layoutAllocation}"));
        return 0;
    }
}
