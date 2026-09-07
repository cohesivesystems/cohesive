using BenchmarkDotNet.Attributes;
using Cohesive.Adapters.SQLite;
using Cohesive.Adapters.SQLite.TestFixtures;
using Cohesive.Model;
using Microsoft.Data.Sqlite;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Current-row materialization, isolating owned payloads from query compilation and execution.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
public class SqliteTypedRowBenchmarks
{
    SqliteTypedRowFixture fixture = null!;
    SqliteCommand command = null!;
    SqliteDataReader reader = null!;
    SqliteRelationQueryRowReader<SqliteTypedRowFixture.Row> rows = null!;
    IOrdinalObservationFieldReader canonical = null!;
    int idOrdinal;
    int labelOrdinal;
    int payloadOrdinal;

    /// <summary>Empty, small and bounded-large byte payloads.</summary>
    [Params(0, 256, 65536)]
    public int PayloadLength { get; set; }

    /// <summary>Compiles a synthetic relation, positions one row and verifies equivalent decoded values.</summary>
    [GlobalSetup]
    public void Setup()
    {
        fixture = new(PayloadLength);
        command = fixture.CreateCommand();
        reader = command.ExecuteReader();
        rows = fixture.Mapping.Bind(reader);
        canonical = new CanonicalReader(rows);
        if (!reader.Read()) throw new InvalidOperationException("Fixture row is missing.");
        int Ordinal(string name) => fixture.Artifact.ResultFields.Single(field => field.Field.Path.ToString() == name).ValueOrdinal;
        idOrdinal = Ordinal(nameof(SqliteTypedRowFixture.Row.Id));
        labelOrdinal = Ordinal(nameof(SqliteTypedRowFixture.Row.Label));
        payloadOrdinal = Ordinal(nameof(SqliteTypedRowFixture.Row.Payload));
        var direct = ReadDirect();
        var typed = rows.ReadCurrent();
        var indirect = fixture.Mapping.Materializer.Materialize(canonical);
        if (direct.Id != typed.Id || direct.Label != typed.Label || !direct.Payload!.AsSpan().SequenceEqual(typed.Payload)
            || !direct.Payload.AsSpan().SequenceEqual(indirect.Payload))
            throw new InvalidOperationException("Materialization strategies disagree.");
    }

    /// <summary>Reads selected fields directly with cached native ordinals.</summary>
    /// <returns>An owned typed row, including its mutable byte array.</returns>
    [Benchmark(Baseline = true)]
    public object DirectOrdinals() => ReadDirect();

    /// <summary>Uses the ordinal core materializer and SQLite's owned-byte read.</summary>
    /// <returns>An owned typed row with validated field encodings and presence.</returns>
    [Benchmark]
    public object TypedRows() => rows.ReadCurrent();

    /// <summary>Forces the canonical field-reader path, including immutable byte snapshot and mutable output copy.</summary>
    /// <returns>An owned typed row with canonical byte ownership boundaries.</returns>
    [Benchmark]
    public object CanonicalOrdinalRows() => fixture.Mapping.Materializer.Materialize(canonical);

    SqliteTypedRowFixture.Row ReadDirect() =>
        new(reader.GetInt64(idOrdinal), reader.GetString(labelOrdinal), (byte[])reader.GetValue(payloadOrdinal));

    /// <summary>Disposes the provider lifetime and removes the synthetic database.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        reader.Dispose();
        command.Dispose();
        fixture.Dispose();
    }

    sealed class CanonicalReader(SqliteRelationQueryRowReader<SqliteTypedRowFixture.Row> rows) : IOrdinalObservationFieldReader
    {
        public QualifiedShapeId ShapeId => rows.ShapeId;
        public ObservationLayout Layout => rows.Layout;
        public bool TryGetField(string fieldIdentity, out ObservationValue field) => rows.TryGetField(fieldIdentity, out field);
        public bool TryGetField(int ordinal, out ObservationValue field) => rows.TryGetField(ordinal, out field);
    }
}
