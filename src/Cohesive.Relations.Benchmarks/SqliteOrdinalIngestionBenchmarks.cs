using BenchmarkDotNet.Attributes;
using Cohesive.Adapters.SQLite.TestFixtures;
using Cohesive.Model;
using Microsoft.Data.Sqlite;

namespace Cohesive.Relations.Benchmarks;

/// <summary>SQLite command execution, decoding, complete shape admission and owned DTO construction.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class SqliteOrdinalIngestionBenchmarks
{
    SqliteTypedRowFixture fixture = null!;
    SqliteCommand command = null!;

    /// <summary>Rows read and retained per operation.</summary>
    [Params(1, 32, 1024)]
    public int RowCount { get; set; }

    /// <summary>Populates valid rows covering present, null and missing optional byte fields.</summary>
    [GlobalSetup]
    public void Setup()
    {
        fixture = new(payloadLength: 256);
        using var populate = fixture.Connection.CreateCommand();
        populate.CommandText = """
            DELETE FROM Rows;
            WITH RECURSIVE numbers(id) AS (SELECT 1 UNION ALL SELECT id+1 FROM numbers WHERE id < $count)
            INSERT INTO Rows SELECT id, 'row-' || id,
                CASE WHEN id % 3 = 1 THEN zeroblob(256) ELSE NULL END,
                CASE WHEN id % 3 = 0 THEN 0 ELSE 1 END FROM numbers;
            """;
        populate.Parameters.AddWithValue("$count", RowCount);
        populate.ExecuteNonQuery();
        command = fixture.CreateCommand();
        var reference = (SqliteTypedRowFixture.Row[])CanonicalIngestion();
        var ordinal = (SqliteTypedRowFixture.Row[])OrdinalIngestion();
        if (reference.Length != RowCount || ordinal.Length != RowCount)
            throw new InvalidOperationException("Ingestion lost rows.");
        for (var index = 0; index < RowCount; index++)
            if (reference[index].Id != ordinal[index].Id || reference[index].Label != ordinal[index].Label
                || !((reference[index].Payload is null && ordinal[index].Payload is null)
                    || (reference[index].Payload is { } bytes && ordinal[index].Payload is { } other && bytes.AsSpan().SequenceEqual(other))))
                throw new InvalidOperationException("Ingestion results differ.");
    }

    /// <summary>Reference dictionary ingestion using the same scalar and presence decoder, complete admission and materializer.</summary>
    /// <returns>Owned DTOs; includes command execution, row advancement and provider disposal.</returns>
    [Benchmark(Baseline = true)]
    public object CanonicalIngestion()
    {
        using var reader = command.ExecuteReader();
        var source = fixture.Mapping.Bind(reader);
        var result = new SqliteTypedRowFixture.Row[RowCount];
        var index = 0;
        while (reader.Read())
        {
            // Fixture bindings are present. Check the same native discriminator as the ordinal path.
            if (reader.GetValue(fixture.Artifact.BindingPresenceOrdinal) is not 1L)
                throw new InvalidOperationException("Fixture output binding is absent or malformed.");
            var fields = new Dictionary<string, ObservationValue>(source.Layout.Count, StringComparer.Ordinal);
            for (var slot = 0; slot < source.Layout.Count; slot++)
                if (source.TryGetField(slot, out var value)) fields.Add(source.Layout.FieldIdentities[slot], value);
            var admitted = Observation.Create(fixture.Shape, fields);
            result[index++] = fixture.Mapping.Materializer.Materialize(admitted);
        }
        if (index != result.Length) throw new InvalidOperationException("Unexpected row count.");
        return result;
    }

    /// <summary>Native scalar decoding directly into retained ordinal storage with complete shape admission.</summary>
    /// <returns>Owned DTOs with the same execution and validation boundary as reference ingestion.</returns>
    [Benchmark]
    public object OrdinalIngestion()
    {
        using var reader = command.ExecuteReader();
        var source = fixture.Mapping.Bind(reader);
        var result = new SqliteTypedRowFixture.Row[RowCount];
        var index = 0;
        while (reader.Read())
        {
            if (!source.TryReadObservation(out var admitted)) throw new InvalidOperationException("Fixture binding is absent.");
            result[index++] = fixture.Mapping.Materializer.Materialize(admitted);
        }
        if (index != result.Length) throw new InvalidOperationException("Unexpected row count.");
        return result;
    }

    /// <summary>Disposes command and provider resources and removes fixture storage.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        command.Dispose();
        fixture.Dispose();
    }
}
