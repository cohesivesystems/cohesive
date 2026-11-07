using BenchmarkDotNet.Attributes;
using Cohesive.Storage;
using Cohesive.Tests.Postgres;
using Npgsql;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Real PostgreSQL execution, entity admission, provenance reconstruction and DTO materialization.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class PostgresOrdinalIngestionBenchmarks
{
    PostgresOrdinalIngestionFixture fixture = null!;
    NpgsqlCommand command = null!;

    /// <summary>Number of native rows retained, including entity metadata and owned DTOs.</summary>
    [Params(1, 32, 1024)]
    public int RowCount { get; set; }

    /// <summary>Creates isolated fixture storage and verifies values and provenance across realizations.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var connection = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Supply a disposable PostgreSQL connection through COHESIVE_POSTGRES_TEST_CONNECTION_STRING.");
        fixture = new(connection, RowCount);
        command = fixture.CreateCommand();
        var canonical = Pipeline(ordinalStorage: false);
        var ordinal = Pipeline(ordinalStorage: true);
        for (var index = 0; index < RowCount; index++)
        {
            var a = canonical[index]; var b = ordinal[index];
            if (a.Snapshot.Entity.EntityId != b.Snapshot.Entity.EntityId || a.Snapshot.Entity.Version != b.Snapshot.Entity.Version
                || a.Snapshot.PartitionKey != b.Snapshot.PartitionKey || a.Snapshot.ConcurrencyToken != b.Snapshot.ConcurrencyToken
                || a.Snapshot.Entity.Observation.Value != b.Snapshot.Entity.Observation.Value
                || a.Dto.Id != b.Dto.Id || a.Dto.Amount != b.Dto.Amount)
                throw new InvalidOperationException("Native ingestion lost value or provenance equivalence.");
        }
    }

    /// <summary>Reference dictionary admission using the identical native decoder and snapshot validation.</summary>
    /// <returns>Owned entity snapshots with identity, version, partition and concurrency token, together with DTOs.</returns>
    [Benchmark(Baseline = true)]
    public object CanonicalIngestion() => Pipeline(ordinalStorage: false);

    /// <summary>Existing native ordinal ingestion with all provenance and validation retained.</summary>
    /// <returns>The same owned snapshot and DTO contract as reference ingestion.</returns>
    [Benchmark]
    public object OrdinalIngestion() => Pipeline(ordinalStorage: true);

    (EntitySnapshot Snapshot, PostgresOrdinalIngestionFixture.Row Dto)[] Pipeline(bool ordinalStorage)
    {
        using var reader = command.ExecuteReader();
        var result = new (EntitySnapshot, PostgresOrdinalIngestionFixture.Row)[RowCount];
        var index = 0;
        while (reader.Read())
        {
            var snapshot = fixture.Repository.ReadSnapshot(reader, selectedFields: null, ordinalStorage: ordinalStorage);
            result[index++] = (snapshot, fixture.Materializer.Materialize(snapshot.Entity.Observation));
        }
        if (index != result.Length) throw new InvalidOperationException("Unexpected native row count.");
        return result;
    }

    /// <summary>Drops owned fixture storage and releases provider resources.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        command.Dispose();
        fixture.Dispose();
    }
}
