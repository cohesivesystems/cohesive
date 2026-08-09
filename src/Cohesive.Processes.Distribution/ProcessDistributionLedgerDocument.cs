using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;

namespace Cohesive.Processes.Distribution;

/// <summary>Last placement ordinal retained for one fairness identity in a pool ledger.</summary>
public sealed record ProcessDistributionFairnessPosition
{
    /// <summary>Creates a retained fairness position.</summary>
    /// <param name="key">Tenant or workload fairness identity; empty represents work without a key.</param>
    /// <param name="ordinal">Non-negative placement ordinal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is negative.</exception>
    [JsonConstructor]
    public ProcessDistributionFairnessPosition(string key, long ordinal)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (ordinal < 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "A fairness ordinal cannot be negative.");
        Key = key;
        Ordinal = ordinal;
    }

    /// <summary>Tenant or workload fairness identity; empty represents work without a key.</summary>
    public string Key { get; }

    /// <summary>Non-negative placement ordinal.</summary>
    [JsonConverter(typeof(Cohesive.Model.Serialization.StringEncodedInt64JsonConverter))]
    public long Ordinal { get; }
}

/// <summary>One pool definition and its durable deterministic fairness cursor.</summary>
public sealed record ProcessDistributionPoolLedger
{
    /// <summary>Creates one pool-ledger snapshot.</summary>
    /// <param name="definition">Exact logical pool definition.</param>
    /// <param name="nextFairnessOrdinal">Greatest placement ordinal assigned so far.</param>
    /// <param name="fairness">Last placement ordinal by fairness identity in canonical key order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nextFairnessOrdinal"/> is negative.</exception>
    /// <exception cref="ArgumentException">Fairness evidence is null, duplicated, unordered, or exceeds the cursor.</exception>
    [JsonConstructor]
    public ProcessDistributionPoolLedger(
        ProcessWorkerPoolDefinition definition,
        long nextFairnessOrdinal,
        ImmutableArray<ProcessDistributionFairnessPosition> fairness)
    {
        if (nextFairnessOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextFairnessOrdinal),
                nextFairnessOrdinal,
                "A fairness cursor cannot be negative.");
        }

        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        var normalized = fairness.IsDefault ? [] : fairness;
        string? prior = null;
        foreach (var position in normalized)
        {
            if (position is null
                || position.Ordinal > nextFairnessOrdinal
                || prior is not null && StringComparer.Ordinal.Compare(prior, position.Key) >= 0)
            {
                throw new ArgumentException(
                    "Fairness positions must be valid, unique, and ordered by key.",
                    nameof(fairness));
            }
            prior = position.Key;
        }

        NextFairnessOrdinal = nextFairnessOrdinal;
        Fairness = normalized;
    }

    /// <summary>Exact logical pool definition.</summary>
    public ProcessWorkerPoolDefinition Definition { get; }

    /// <summary>Greatest placement ordinal assigned so far.</summary>
    [JsonConverter(typeof(Cohesive.Model.Serialization.StringEncodedInt64JsonConverter))]
    public long NextFairnessOrdinal { get; }

    /// <summary>Last placement ordinal by fairness identity in canonical key order.</summary>
    public ImmutableArray<ProcessDistributionFairnessPosition> Fairness { get; }
}

/// <summary>Complete provider-neutral durable state of one distribution ledger authority.</summary>
/// <remarks>
/// Providers may persist this document as one atomic aggregate or project it into equivalent transactional rows.
/// The document is the reference interpreter's durable conformance format, not a requirement that production
/// providers use one physical row.
/// </remarks>
public sealed record ProcessDistributionLedgerDocument
{
    /// <summary>Creates one complete distribution-ledger document.</summary>
    /// <param name="schemaVersion">Exact portable distribution schema version.</param>
    /// <param name="pools">Pool definitions and fairness cursors in canonical identity order.</param>
    /// <param name="workers">Worker registrations in canonical incarnation order.</param>
    /// <param name="work">Work records in canonical logical identity order.</param>
    /// <exception cref="ArgumentException">
    /// The schema is unsupported or a collection contains null, duplicate, or unordered entries.
    /// </exception>
    [JsonConstructor]
    public ProcessDistributionLedgerDocument(
        ExecutionIrSchemaVersion schemaVersion,
        ImmutableArray<ProcessDistributionPoolLedger> pools,
        ImmutableArray<ProcessWorkerRegistration> workers,
        ImmutableArray<ProcessWorkRecord> work)
    {
        if (schemaVersion != ProcessDistributionWireNames.CurrentSchemaVersion)
            throw new ArgumentException("Unsupported Process distribution schema version.", nameof(schemaVersion));

        Pools = ValidateOrdered(
            pools,
            static item => item.Definition.Id.Value,
            nameof(pools));
        Workers = ValidateOrdered(
            workers,
            static item => item.Offer.Worker.Value,
            nameof(workers));
        Work = ValidateOrdered(
            work,
            static item => item.Submission.Id.Value,
            nameof(work));
        SchemaVersion = schemaVersion;
    }

    /// <summary>Exact portable distribution schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Pool definitions and fairness cursors in canonical identity order.</summary>
    public ImmutableArray<ProcessDistributionPoolLedger> Pools { get; }

    /// <summary>Worker registrations in canonical incarnation order.</summary>
    public ImmutableArray<ProcessWorkerRegistration> Workers { get; }

    /// <summary>Work records in canonical logical identity order.</summary>
    public ImmutableArray<ProcessWorkRecord> Work { get; }

    /// <summary>Creates an empty current-version ledger.</summary>
    /// <returns>An empty provider-neutral distribution authority.</returns>
    public static ProcessDistributionLedgerDocument Empty() => new(
        ProcessDistributionWireNames.CurrentSchemaVersion,
        pools: [],
        workers: [],
        work: []);

    static ImmutableArray<T> ValidateOrdered<T>(
        ImmutableArray<T> values,
        Func<T, string> identity,
        string parameterName)
        where T : class
    {
        var normalized = values.IsDefault ? [] : values;
        string? prior = null;
        foreach (var value in normalized)
        {
            if (value is null)
                throw new ArgumentException("Ledger collections cannot contain null entries.", parameterName);
            var current = identity(value);
            if (string.IsNullOrWhiteSpace(current)
                || prior is not null && StringComparer.Ordinal.Compare(prior, current) >= 0)
            {
                throw new ArgumentException(
                    "Ledger collections must be unique and ordered by canonical identity.",
                    parameterName);
            }
            prior = current;
        }
        return normalized;
    }
}
