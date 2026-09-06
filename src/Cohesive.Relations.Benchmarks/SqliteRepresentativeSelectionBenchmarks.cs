using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Adapters.Sql;
using Cohesive.Adapters.SQLite;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.TestFixtures;
using Microsoft.Data.Sqlite;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Indexed native selection versus direct SQL and full reference execution, including canonical result decoding.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
public class SqliteRepresentativeSelectionBenchmarks
{
    string path = null!;
    SqliteDatabase database = null!;
    SqliteConnection connection = null!;
    SqliteTransaction transaction = null!;
    SqliteCommandScope scope = null!;
    SqliteRelationQueryCompiledArtifact artifact = null!;
    SqliteCommand handwritten = null!;
    RelationQueryExecutionRequest reference = null!;

    /// <summary>Candidate count, with ten rows per partition.</summary>
    [Params(100, 1000, 10000)]
    public int RowCount { get; set; }

    /// <summary>Creates indexed synthetic data, compiles once, and verifies equal winners before measuring execution.</summary>
    /// <exception cref="InvalidOperationException">Compilation or the result cardinality differs from the fixture contract.</exception>
    [GlobalSetup]
    public void Setup()
    {
        path = Path.Combine(Path.GetTempPath(), "cohesive-native-selection-" + Guid.NewGuid().ToString("N") + ".db");
        database = new(new(path));
        connection = database.OpenConnection();
        using (var schema = database.CreateCommand(connection, null,
                   "CREATE TABLE candidates (Id INTEGER PRIMARY KEY, Key TEXT NOT NULL, KeyPresent INTEGER NOT NULL CHECK(KeyPresent=1), Preference INTEGER NOT NULL, Eligible INTEGER NOT NULL CHECK(Eligible=1)) STRICT; CREATE INDEX candidate_order ON candidates(KeyPresent, Key COLLATE BINARY, Preference DESC, Id);"))
            schema.ExecuteNonQuery();
        var rows = new RepresentativeSelectionFixture.Candidate[RowCount];
        using (var write = connection.BeginTransaction())
        using (var insert = database.CreateCommand(connection, write, "INSERT INTO candidates VALUES ($id,$key,1,$preference,1)"))
        {
            var id = insert.Parameters.AddWithValue("$id", 0L);
            var key = insert.Parameters.AddWithValue("$key", "");
            var preference = insert.Parameters.AddWithValue("$preference", 0L);
            for (var index = 0; index < RowCount; index++)
            {
                var partition = (index / 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
                rows[index] = new(index, ObservationValue.FromString(partition), index % 10);
                id.Value = (long)index;
                key.Value = partition;
                preference.Value = (long)(index % 10);
                insert.ExecuteNonQuery();
            }
            write.Commit();
        }
        var plan = RepresentativeSelectionFixture.Compile(RepresentativeSelectionFixture.Document());
        var source = plan.InputContract.Sources.Single();
        var placed = new RelationQuerySourcePlacementBinding(new("candidates"), source.Input.Id, source.Node, source.Binding,
            source.Shape, new("sqlite/benchmark"), RelationQuerySourcePlacementBindingKind.SourceSet,
            RelationQuerySourceAcquisitionKind.BoundedEnumeration, RelationQuerySourcePlacementOrigin.Explicit,
            new(source.Shape, "Id", RepresentativeSelectionFixture.Id),
            [.. source.Fields.Select(field => new RelationQuerySourceFieldBinding(field.Input.Id, field.Input.Field.Path, field.Input.Field.Path.ToString()))]);
        var placement = new RelationQuerySourcePlacement(RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(plan), SqliteRelationQueryTargetProfile.ConventionSet,
            [new(new("sqlite/benchmark"), new("sqlite/benchmark-database"), SqliteRelationQueryTargetProfile.Default,
                new(10000, 10000, 10000, 1))], [placed]);
        var storage = new SqliteRelationQueryStorageBinding(placement,
            [new(placed.Id, "candidates", "benchmarks/strict-candidate-schema-v1",
                [new(source.Fields.Single(field => field.Input.Field.Path == RepresentativeSelectionFixture.Key).Input.Id, "KeyPresent")])]);
        var feasibility = RelationQueryRealizationCompiler.Compile(plan, SqliteRelationQueryTargetProfile.Default,
            SqliteRelationQueryTargetProfile.Policy, RelationQueryResultObservability.ExactContributors);
        var compiled = new SqliteRelationQueryCompiler().Compile(new(plan, feasibility, placement), storage);
        if (!compiled.IsSuccessful) throw new InvalidOperationException(string.Join("\n", compiled.Diagnostics));
        artifact = compiled.Artifacts.Single();

        // Independent SQL baseline deliberately bypasses semantic lowering, while retaining exactly the same reader and result layout.
        var columns = new string[artifact.BindingPresenceOrdinal + 1 + artifact.OccurrenceColumns.Length];
        foreach (var field in artifact.ResultFields)
        {
            columns[field.ValueOrdinal] = field.Field.Path.ToString();
            columns[field.PresenceOrdinal] = field.Field.Path == RepresentativeSelectionFixture.Key ? "KeyPresent" : "1";
        }
        columns[artifact.BindingPresenceOrdinal] = "1";
        foreach (var occurrence in artifact.OccurrenceColumns) columns[occurrence.Components.Single().Ordinal] = "Id";
        handwritten = database.CreateCommand(connection, null,
            "WITH ranked AS (SELECT *, ROW_NUMBER() OVER (PARTITION BY KeyPresent, Key COLLATE BINARY ORDER BY Preference DESC NULLS LAST, Id ASC NULLS LAST) AS rank FROM candidates) SELECT "
            + string.Join(",", columns) + " FROM ranked WHERE rank=1 ORDER BY Id");
        reference = new(plan, RepresentativeSelectionFixture.Evidence(plan, rows));
        transaction = connection.BeginTransaction(deferred: true);
        scope = new(database, connection, transaction);
        handwritten.Transaction = transaction;
        handwritten.Prepare();
        var expected = Reference();
        if (expected.Status != RelationQueryExecutionStatus.Succeeded || expected.QueryResults.Single().Rows.Length != RowCount / 10
            || CompiledSql().Length != RowCount / 10 || HandwrittenSql().Length != RowCount / 10)
            throw new InvalidOperationException("Representative selection benchmark fixture failed.");
        using var explain = database.CreateCommand(connection, transaction, "EXPLAIN QUERY PLAN " + artifact.Statement.Text);
        foreach (var parameter in artifact.Statement.Bind(SqliteSqlDialect.Instance).Parameters)
            explain.Parameters.AddWithValue(parameter.Placeholder, parameter.Value ?? DBNull.Value);
        using var reader = explain.ExecuteReader();
        List<string> details = [];
        while (reader.Read()) details.Add(reader.GetString(3));
        if (!details.Any(static detail => detail.Contains("candidate_order", StringComparison.Ordinal)))
            throw new InvalidOperationException("Benchmark must use the declared ordering index.");
    }

    /// <summary>Executes direct SQL and decodes the identical canonical result layout.</summary>
    /// <returns>Canonical winner rows with provenance.</returns>
    [Benchmark(Baseline = true)]
    public ImmutableArray<SqliteRelationQueryRow> HandwrittenSql()
    {
        using var reader = handwritten.ExecuteReader();
        return Read(reader);
    }

    /// <summary>Executes the compiled canonical SQLite template with the cached command scope.</summary>
    /// <returns>Canonical winner rows with provenance.</returns>
    [Benchmark]
    public ImmutableArray<SqliteRelationQueryRow> CompiledSql()
    {
        using var reader = scope.ExecuteReader(artifact.Command, default);
        return Read(reader);
    }

    /// <summary>Executes the full canonical reference pipeline over prebuilt evidence.</summary>
    /// <returns>Reference query result with the same winning values and contributors.</returns>
    [Benchmark]
    public RelationQueryExecutionResult Reference() => RelationQueryInMemoryInterpreter.Default.Execute(reference);

    ImmutableArray<SqliteRelationQueryRow> Read(SqliteDataReader reader)
    {
        var result = ImmutableArray.CreateBuilder<SqliteRelationQueryRow>(RowCount / 10);
        while (reader.Read()) result.Add(artifact.ReadCurrentRow(reader));
        return result.MoveToImmutable();
    }

    /// <summary>Releases provider state and removes the temporary database after measurement.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        handwritten?.Dispose();
        scope?.Dispose();
        transaction?.Dispose();
        connection?.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
    }
}
