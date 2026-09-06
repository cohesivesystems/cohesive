using BenchmarkDotNet.Attributes;
using Cohesive.Adapters.Sql;
using Cohesive.Adapters.SQLite;
using Microsoft.Data.Sqlite;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Compares one-shot, operation-scoped and multi-row native writes within the same transaction boundary.</summary>
[MemoryDiagnoser]
public class SqliteCommandReuseBenchmarks
{
    static readonly SqliteCommandTemplate Insert = new(new SqlInsertBuilder(new("sample"))
        .Value("id", SqlExpression.RuntimeParameter("id"))
        .Value("payload", SqlExpression.RuntimeParameter("payload"))
        .Value("note", SqlExpression.RuntimeParameter("note"))
        .BuildTemplate(SqliteSqlDialect.Instance));
    readonly byte[] payload = new byte[512];
    const string Note = "encoded observation";
    string directory = null!;
    string multiRowSql = null!;
    SqliteDatabase database = null!;
    SqliteConnection connection = null!;

    /// <summary>Number of homogeneous rows in one caller-owned transaction.</summary>
    [Params(1, 100, 1_000)]
    public int Rows { get; set; }

    /// <summary>Creates a real file and caches construction outside timed execution.</summary>
    [GlobalSetup]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "cohesive-command-reuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        database = new(new(Path.Combine(directory, "data.db")));
        connection = database.OpenConnection();
        using var schema = database.CreateCommand(connection, null,
            "CREATE TABLE sample (id INTEGER PRIMARY KEY, payload BLOB NOT NULL, note TEXT NOT NULL) STRICT;");
        schema.ExecuteNonQuery();
        // Benchmark-only candidate for a grammar not yet in the shared builder. All emitted tokens
        // are fixed identifiers or generated parameter names; runtime data remains bound.
        multiRowSql = "INSERT INTO sample (id, payload, note) VALUES " + string.Join(", ",
            Enumerable.Range(0, Rows).Select(row => $"($id{row}, $payload{row}, $note{row})"));
    }

    /// <summary>Creates fresh native commands from cached shared SQL for each row.</summary>
    /// <returns>The number of inserted rows, rolled back as the operation exits.</returns>
    [Benchmark(Baseline = true)]
    public int FreshCommands()
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var count = 0;
        for (var row = 0; row < Rows; row++)
        {
            using var command = database.CreateCommand(connection, transaction, Insert,
                ("id", row), ("payload", payload), ("note", Note));
            count += command.ExecuteNonQuery();
        }
        return count;
    }

    /// <summary>Reuses one prepared command and its parameters for the operation's rows.</summary>
    /// <returns>The number of inserted rows, rolled back as the operation exits.</returns>
    [Benchmark]
    public int OperationScope()
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        using var commands = new SqliteCommandScope(database, connection, transaction);
        var count = 0;
        for (var row = 0; row < Rows; row++)
            count += commands.ExecuteNonQuery(Insert, default, ("id", row), ("payload", payload), ("note", Note));
        return count;
    }

    /// <summary>Measures a native multi-row candidate with cached SQL text and one preparation per operation.</summary>
    /// <returns>The number of inserted rows, rolled back as the operation exits.</returns>
    [Benchmark]
    public int NativeMultiRowCandidate()
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = database.CreateCommand(connection, transaction, multiRowSql);
        for (var row = 0; row < Rows; row++)
        {
            command.Parameters.AddWithValue($"$id{row}", row);
            command.Parameters.AddWithValue($"$payload{row}", payload);
            command.Parameters.AddWithValue($"$note{row}", Note);
        }
        return command.ExecuteNonQuery();
    }

    /// <summary>Releases the single connection and removes the isolated fixture file.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        connection.Dispose();
        Directory.Delete(directory, recursive: true);
    }
}
