using BenchmarkDotNet.Attributes;
using Cohesive.Adapters.SQLite;
using Microsoft.Data.Sqlite;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Separates native handle reuse from verified-profile overhead under the same read/write workload.</summary>
[MemoryDiagnoser]
public class SqliteConnectionBenchmarks
{
    string directory = null!;
    string legacyConnectionString = null!;
    SqliteDatabase pooled = null!;
    SqliteDatabase unpooled = null!;

    /// <summary>Selects an immediate transactional write or a deferred transactional read.</summary>
    [Params(false, true)]
    public bool Write { get; set; }

    /// <summary>Creates an isolated file and equivalent connection policies outside the measured operations.</summary>
    [GlobalSetup]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "cohesive-sqlite-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "data.db");
        pooled = new(new(path, pooling: SqliteConnectionPooling.Enabled));
        unpooled = new(new(path, pooling: SqliteConnectionPooling.Disabled));
        legacyConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = true, DefaultTimeout = 5
        }.ToString();
        using var connection = unpooled.OpenConnection();
        using var command = unpooled.CreateCommand(connection, null,
            "CREATE TABLE sample (id INTEGER PRIMARY KEY, value INTEGER NOT NULL); INSERT INTO sample VALUES (1, 0);");
        command.ExecuteNonQuery();
    }

    /// <summary>Runs an operation with direct provider pooling and the baseline PRAGMA setup.</summary>
    /// <returns>The stored integer read or updated in this transaction.</returns>
    [Benchmark(Baseline = true)]
    public long LegacyPooled()
    {
        using var connection = new SqliteConnection(legacyConnectionString);
        connection.Open();
        using (var configure = connection.CreateCommand())
        {
            configure.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000;";
            configure.ExecuteNonQuery();
        }
        return Execute(connection);
    }

    /// <summary>Runs an operation with a pooled native handle and verified adapter profile.</summary>
    /// <returns>The stored integer read or updated in this transaction.</returns>
    [Benchmark]
    public long VerifiedPooled()
    {
        using var connection = pooled.OpenConnection();
        return Execute(connection);
    }

    /// <summary>Runs an operation with a fresh native handle and verified adapter profile.</summary>
    /// <returns>The stored integer read or updated in this transaction.</returns>
    [Benchmark]
    public long VerifiedUnpooled()
    {
        using var connection = unpooled.OpenConnection();
        return Execute(connection);
    }

    long Execute(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: !Write);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Write
            ? "UPDATE sample SET value = value + 1 WHERE id = 1 RETURNING value;"
            : "SELECT value FROM sample WHERE id = 1;";
        var value = (long)command.ExecuteScalar()!;
        transaction.Commit();
        return value;
    }

    /// <summary>Closes the fixture's idle pooled handles and removes its database after measured operations finish.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        pooled.ClearPool();
        using var legacy = new SqliteConnection(legacyConnectionString);
        SqliteConnection.ClearPool(legacy);
        Directory.Delete(directory, recursive: true);
    }
}
