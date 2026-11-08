using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Tests.Postgres;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Native PostgreSQL joins, strict admission, full contributor reconstruction and owned DTO output.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class PostgresContributingRowsBenchmarks
{
    PostgresContributingRowsFixture fixture = null!;
    ObservationMaterializer<PostgresContributingRowsFixture.Row> materializer = null!;
    static readonly IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters = new Dictionary<QueryParameterId, ObservationValue>();

    /// <summary>Number of left source rows; fan-out may produce more result rows.</summary>
    [Params(1, 32, 1024)]
    public int RowCount { get; set; }

    /// <summary>Native join semantics, including unmatched right bindings in left joins.</summary>
    [Params(JoinKind.Inner, JoinKind.Left)]
    public JoinKind Join { get; set; }

    /// <summary>Creates isolated tables and compiles exact contributors once; verifies actual and reference results.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        var connection = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Supply disposable PostgreSQL via COHESIVE_POSTGRES_TEST_CONNECTION_STRING.");
        fixture = new(connection, RowCount, Join);
        materializer = ObservationMaterializer.For<PostgresContributingRowsFixture.Row>(fixture.Shape).Compile(fixture.Reader.Layout!);
        var actual = await fixture.Reader.ReadRowsAsync(parameters);
        var reference = fixture.Reference.QueryResults.Single().Rows;
        string Key(RelationQueryOutputRow row) => row.Value.Fields!["Id"].GetInt64() + ":" + string.Join("|",
            row.InputOccurrences.Select(item => item.Binding.Value + "/" + item.ObservationIdentity).Order(StringComparer.Ordinal));
        if (!actual.OrderBy(Key).Select(Key).SequenceEqual(reference.OrderBy(Key).Select(Key))
            || !actual.OrderBy(Key).Select(row => row.Value).SequenceEqual(reference.OrderBy(Key).Select(row => row.Value)))
            throw new InvalidOperationException("Native values or join contributors differ from the reference interpreter.");
        await CanonicalIngestion();
        await OrdinalIngestion();
    }

    /// <summary>Streams provider columns into dictionary admission, retaining the same contributor rows and DTOs.</summary>
    /// <returns>Owned output rows and DTOs; query execution, byte bounds, validation and lineage reconstruction are included.</returns>
    [Benchmark(Baseline = true)]
    public Task<object> CanonicalIngestion() => Pipeline(ordinalStorage: false);

    /// <summary>Streams provider columns into ordinal admission, retaining all join contributors.</summary>
    /// <returns>The same owned rows and DTOs as the dictionary reference.</returns>
    [Benchmark]
    public Task<object> OrdinalIngestion() => Pipeline(ordinalStorage: true);

    async Task<object> Pipeline(bool ordinalStorage)
    {
        var rows = await fixture.Reader.ReadRowsCoreAsync(parameters, ordinalStorage);
        var result = new (RelationQueryOutputRow Source, PostgresContributingRowsFixture.Row Dto)[rows.Length];
        for (var index = 0; index < rows.Length; index++)
            result[index] = (rows[index], materializer.Materialize(new AdmittedFields(rows[index], fixture.Reader.Layout!)));
        return result;
    }

    // Equal-cost post-admission view on both sides; it performs no revalidation or storage conversion.
    sealed class AdmittedFields(RelationQueryOutputRow row, ObservationLayout layout) : IOrdinalObservationFieldReader
    {
        readonly IReadOnlyDictionary<string, ObservationValue> fields = row.Value.Fields!;
        readonly IOrdinalObservationFieldReader? ordinal = row.Value.Fields as IOrdinalObservationFieldReader;
        public QualifiedShapeId ShapeId => row.Shape;
        public ObservationLayout Layout => layout;
        public bool TryGetField(string fieldIdentity, out ObservationValue field) => fields.TryGetValue(fieldIdentity, out field);
        public bool TryGetField(int slot, out ObservationValue field)
        {
            if (ordinal is not null) return ordinal.TryGetField(slot, out field);
            field = default;
            return (uint)slot < (uint)layout.Count && fields.TryGetValue(layout.FieldIdentities[slot], out field);
        }
    }

    /// <summary>Drops uniquely owned fixture tables and disposes the data source.</summary>
    [GlobalCleanup]
    public void Cleanup() => fixture.Dispose();
}
