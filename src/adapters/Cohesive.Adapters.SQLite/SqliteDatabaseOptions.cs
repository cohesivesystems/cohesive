using System.Collections.Immutable;
using Cohesive.Storage.Realization;

namespace Cohesive.Adapters.SQLite;

/// <summary>Durability policy for a file database using SQLite write-ahead logging.</summary>
public enum SqliteDurability
{
    /// <summary>Sync the WAL on commit; durability still depends on truthful local filesystem and hardware behavior.</summary>
    Full = 0,
    /// <summary>Preserve atomicity but permit recent committed transactions to be lost after power or operating-system failure.</summary>
    Normal = 1
}

/// <summary>Immutable effective configuration for one local, same-host SQLite file database.</summary>
public sealed class SqliteDatabaseOptions
{
    /// <summary>Default provider lock retry timeout in whole seconds.</summary>
    public const int DefaultBusyTimeoutSeconds = 5;

    /// <summary>Resolves explicit settings and the file/WAL convention profile.</summary>
    /// <param name="path">Database file path, resolved to an absolute path at construction; its directory must exist.</param>
    /// <param name="durability">Explicit WAL synchronization policy, or null for Full.</param>
    /// <param name="busyTimeoutSeconds">Positive provider lock retry timeout, at most 300 seconds, or null for five seconds.</param>
    /// <exception cref="ArgumentException">The path is empty, a SQLite URI, or an in-memory database.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A policy is unknown or the timeout is outside 1–300 seconds.</exception>
    public SqliteDatabaseOptions(string path, SqliteDurability? durability = null, int? busyTimeoutSeconds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path == ":memory:" || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This adapter profile requires a filesystem path, not a SQLite URI or memory database.", nameof(path));
        Path = System.IO.Path.GetFullPath(path);
        Durability = durability ?? SqliteDurability.Full;
        if (!Enum.IsDefined(Durability))
            throw new ArgumentOutOfRangeException(nameof(durability));
        BusyTimeoutSeconds = busyTimeoutSeconds ?? DefaultBusyTimeoutSeconds;
        if (BusyTimeoutSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(busyTimeoutSeconds), "Use a bounded timeout from 1 through 300 seconds.");
        var conventions = ImmutableArray.CreateBuilder<string>(2);
        if (durability is null) conventions.Add(nameof(Durability));
        if (busyTimeoutSeconds is null) conventions.Add(nameof(BusyTimeoutSeconds));
        ConventionSuppliedSettings = conventions.ToImmutable();
    }

    /// <summary>Absolute database path; the application attests that it resides on a supported local filesystem.</summary>
    public string Path { get; }
    /// <summary>Resolved synchronization policy.</summary>
    public SqliteDurability Durability { get; }
    /// <summary>Bounded provider lock retry timeout; this is not a wall-clock query execution deadline.</summary>
    public int BusyTimeoutSeconds { get; }
    /// <summary>Settings supplied by conventions rather than explicit constructor arguments.</summary>
    public ImmutableArray<string> ConventionSuppliedSettings { get; }
    /// <summary>Exact adapter/profile identity describing the effective durability boundary.</summary>
    public StorageRealizationTarget Target => new("cohesive.sqlite", Durability == SqliteDurability.Full ? "sqlite.file-wal-full/v1" : "sqlite.file-wal-normal/v1");
    /// <summary>Root and component records can commit within one database transaction.</summary>
    public StorageAggregateAtomicityKind AggregateAtomicity => StorageAggregateAtomicityKind.TransactionAcrossRecords;
    /// <summary>Maximum number of simultaneous SQLite writers in this database.</summary>
    public int MaximumConcurrentWriters => 1;
    /// <summary>Whether this profile provides transactions across independent database files or hosts.</summary>
    public bool SupportsDistributedTransactions => false;
}
