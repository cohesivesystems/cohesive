using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Explicit offline placement, CDC and execution policy for one PostgreSQL materialization partition.</summary>
public sealed record PostgresMaterializationStorageOptions
{
    /// <summary>Field mapped as the partition on every entity.</summary>
    public required string PartitionField { get; init; }
    /// <summary>Physical relation planning limits.</summary>
    public required RelationQueryPhysicalPlanningPolicy Planning { get; init; }
    /// <summary>Read limits and mandatory logical partition scope.</summary>
    public required PostgresRelationQuerySourcePolicy Reads { get; init; }
    /// <summary>Rebuild and activation limits.</summary>
    public required MaterializationRebuildLimits Rebuild { get; init; }
    /// <summary>Dedicated pgoutput publication.</summary>
    public required string Publication { get; init; }
    /// <summary>Stable slot naming recipe, evaluated once per input during declaration.</summary>
    public required Func<RelationQueryInputId, string> Slot { get; init; }
    /// <summary>Native replication read policy.</summary>
    public required PostgresLogicalReplicationSourcePolicy Replication { get; init; }
    /// <summary>Configured progress authority and native schema.</summary>
    public required PostgresMaterializationStateStoreOptions Checkpoints { get; init; }
    /// <summary>Stable provider interpretation identity.</summary>
    public required string Provider { get; init; }
    /// <summary>Explicit host concurrency evidence.</summary>
    public required MaterializationPhysicalCapacityDomain Capacity { get; init; }
    /// <summary>Caller-owned producer and source provenance.</summary>
    public required ExecutionProvenance Provenance { get; init; }
}

/// <summary>Storage-model contribution deriving readers, CDC inputs and rebuild bindings from one declaration.</summary>
/// <remarks>Construction performs no native IO. Bind borrows clients; InitializeSourcesAsync is separately opt-in.</remarks>
public sealed class PostgresMaterializationStorage
{
    readonly PostgresPersistenceBindings bindings;
    readonly PostgresMaterializationRelation relation;
    readonly PostgresMaterializationStorageOptions options;
    readonly ImmutableDictionary<RelationQueryInputId, string> slots;

    /// <summary>Prepares immutable native placement and retains explicit operational policy.</summary>
    /// <param name="bindings">Existing entity mappings; no parallel resource catalog.</param>
    /// <param name="materialization">Admitted semantic declaration.</param>
    /// <param name="options">Native configuration and operating guarantees.</param>
    /// <exception cref="ArgumentException">A policy, mapping, partition or slot declaration is invalid.</exception>
    public PostgresMaterializationStorage(PostgresPersistenceBindings bindings, PreparedMaterialization materialization,
        PostgresMaterializationStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(materialization);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Reads);
        ArgumentNullException.ThrowIfNull(options.Planning);
        ArgumentNullException.ThrowIfNull(options.Rebuild);
        ArgumentNullException.ThrowIfNull(options.Replication);
        ArgumentNullException.ThrowIfNull(options.Checkpoints);
        ArgumentNullException.ThrowIfNull(options.Capacity);
        ArgumentNullException.ThrowIfNull(options.Provenance);
        ArgumentNullException.ThrowIfNull(options.Slot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Provider);
        _ = PostgresSqlDialect.Identifier(options.Publication);
        if (options.Reads.PartitionScope is null)
            throw new ArgumentException("Materialization reads require an explicit partition scope.", nameof(options));
        this.bindings = bindings;
        this.options = options;
        Materialization = materialization;
        relation = new(bindings, materialization, options.PartitionField, options.Planning);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var declared = ImmutableDictionary.CreateBuilder<RelationQueryInputId, string>();
        foreach (var input in materialization.Document.Definition.Sources)
        {
            var name = options.Slot(input.Input);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 63 || name.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) || !names.Add(name))
                throw new ArgumentException("Replication slots must be distinct PostgreSQL lowercase slot identifiers.", nameof(options));
            declared.Add(input.Input, name);
        }
        slots = declared.ToImmutable();
    }

    /// <summary>Exact prepared semantic authority.</summary>
    public PreparedMaterialization Materialization { get; }

    /// <summary>Explicitly initializes declared source publication, slots and checkpoint storage.</summary>
    /// <param name="database">Borrowed native pool for the declared database.</param>
    /// <param name="cancellationToken">Cancels initialization.</param>
    /// <returns>Completion after native initialization; no target or live route is changed.</returns>
    public async Task InitializeSourcesAsync(NpgsqlDataSource database, CancellationToken cancellationToken = default)
    {
        var state = new PostgresMaterializationStateStore(database, options.Checkpoints);
        var tables = relation.Reads.Impact.Placement.Bindings.Select(binding => bindings.Mapping(binding.Shape).Table).Distinct();
        await PostgresLogicalReplicationPreparation.InitializeAsync(database, tables, options.Publication,
            slots.Values, state, cancellationToken).ConfigureAwait(false);
        await state.EnsureCreatedAsync(Cohesive.Prelude.OperationContext.Create(cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Preflights borrowed native clients and resolves the declared materialization; performs no DDL.</summary>
    /// <param name="database">Borrowed native pool; database identity and authority come from this declaration.</param>
    /// <param name="replicationConnectionFactory">Creates caller-configured native replication connections, owned per operation.</param>
    /// <param name="target">Admitted target, owned by the host.</param>
    /// <param name="authenticationKey">Persistent position-authentication key.</param>
    /// <param name="slotGeneration">Explicit slot incarnation retained across restart.</param>
    /// <param name="cancellationToken">Cancels native admission.</param>
    /// <returns>Shared bound runtime borrowing all native resources.</returns>
    /// <exception cref="ArgumentException">Native identity or declared policy is inconsistent.</exception>
    public async Task<BoundMaterialization> BindAsync(NpgsqlDataSource database, PostgresLogicalReplicationConnectionFactory replicationConnectionFactory, IMaterializationTarget target,
        ReadOnlyMemory<byte> authenticationKey, string slotGeneration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(replicationConnectionFactory);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotGeneration);
        var native = new PostgresNpgsqlRuntimeBinding(bindings.Database, database, options.Checkpoints.AuthorityId,
            logicalReplicationConnectionFactory: replicationConnectionFactory);
        var partition = await relation.BindAsync(Materialization.Document, native.DataSource, native, options.Reads,
            options.Reads.PartitionScope!.CanonicalValue, authenticationKey, input => new(options.Publication, slots[input], slotGeneration,
                expectedReplicaIdentity: new(PostgresLogicalReplicationReplicaIdentityKind.Full),
                beforeImageRequirement: PostgresLogicalReplicationBeforeImageRequirement.Required),
            options.Replication, cancellationToken).ConfigureAwait(false);
        var compilation = MaterializationPartitionedRebuildCompiler.Compile(Materialization.Document, relation.Plan,
            options.Rebuild, options.Provider, target.Descriptor, [partition], Materialization.Impact, options.Capacity, options.Provenance);
        var state = new PostgresMaterializationStateStore(native.DataSource, options.Checkpoints);
        return new(compilation, target, state, state);
    }
}
