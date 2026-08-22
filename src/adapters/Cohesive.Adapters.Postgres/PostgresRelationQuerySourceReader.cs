using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Transactions;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Bounded canonical Relations acquisition through the plan-affine PostgreSQL storage binding.</summary>
/// <remarks>
/// The supplied <see cref="NpgsqlDataSource"/> is borrowed, thread-safe infrastructure and must outlive this reader.
/// The reader never disposes it. Every operation creates and disposes its own command and data reader, so one reader
/// instance may serve concurrent calls within the source instance's declared concurrency bound. Reads reject ambient
/// transactions and multi-host data sources so consistency cannot depend on hidden transaction or replica state.
/// </remarks>
public sealed class PostgresRelationQuerySourceReader : IRelationQuerySourceReader
{
    const string EvidencePrefix = "cohesive.adapters.postgres/source-reader/v1";
    const string SourceAlias = "source";
    const string IdentityAlias = "_identity";
    const string RelationshipAlias = "_relationship";
    const string KeysBinding = "keys";
    const string AfterBinding = "after";
    const string PartitionBinding = "partition";
    const string RequestedAlias = "requested";
    const string RequestedKeyAlias = "key";
    const string CandidateAlias = "candidate";
    const string RootPageAlias = "root_page";
    const string ComponentAlias = "component";
    const string OccurrenceAlias = "occurrence";
    const string RootPartitionAlias = "_root_partition";

    readonly RelationQueryPhysicalPlanFingerprint physicalPlan;
    readonly CompiledRelationQueryPhysicalPlan? compiledPhysicalPlan;
    readonly CompiledRelationQueryPlan? semanticPlan;
    readonly RelationQuerySourcePlacement placement;
    readonly RelationQuerySourceInstance source;
    readonly PostgresRelationQueryStorageBinding storage;
    readonly PostgresNpgsqlRuntimeBinding? runtimeBinding;
    readonly PostgresNpgsqlCommandExecutor executeCommand;
    readonly ImmutableDictionary<RelationQuerySourcePlacementBindingId, ResolvedPartition> partitions;

    /// <summary>Creates an Npgsql-backed canonical PostgreSQL source reader.</summary>
    /// <param name="plan">Exact semantic compiled plan referenced by <paramref name="physicalPlan"/>.</param>
    /// <param name="physicalPlan">Exact compiled physical plan authorized by the reader.</param>
    /// <param name="source">Source identity resolved from <paramref name="physicalPlan"/>.</param>
    /// <param name="storage">Canonical plan- and placement-affine PostgreSQL storage binding.</param>
    /// <param name="dataSource">Caller-owned, single-host Npgsql data source used for command execution.</param>
    /// <param name="runtimeBinding">
    /// Explicit runtime attestation binding <paramref name="storage"/>'s database identity to the exact
    /// <paramref name="dataSource"/> instance.
    /// </param>
    /// <param name="policy">Physical source limits, or <see langword="null"/> for <see cref="PostgresRelationQuerySourcePolicy.Default"/>.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The semantic plan, source, storage binding, target profile, physical-plan affinity, table coverage, or Npgsql
    /// topology or runtime attestation is incompatible.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A temporal binding is present but the policy does not attest that Npgsql infinity conversions were disabled
    /// before provider initialization, or the process switch is not currently enabled.
    /// </exception>
    public PostgresRelationQuerySourceReader(
        CompiledRelationQueryPlan plan,
        CompiledRelationQueryPhysicalPlan physicalPlan,
        RelationQuerySourceInstanceId source,
        PostgresRelationQueryStorageBinding storage,
        NpgsqlDataSource dataSource,
        PostgresNpgsqlRuntimeBinding runtimeBinding,
        PostgresRelationQuerySourcePolicy? policy = null)
        : this(
            Register(plan, physicalPlan, source, storage),
            storage,
            runtimeBinding,
            RequireSingleHostDataSource(dataSource, runtimeBinding, storage.Database),
            policy ?? PostgresRelationQuerySourcePolicy.Default)
    {
    }

    PostgresRelationQuerySourceReader(
        Registration registration,
        PostgresRelationQueryStorageBinding storage,
        PostgresNpgsqlRuntimeBinding? runtimeBinding,
        PostgresNpgsqlCommandExecutor executeCommand,
        PostgresRelationQuerySourcePolicy policy)
        : this(
            registration.PhysicalPlan.Fingerprint,
            registration.PhysicalPlan,
            registration.Plan,
            registration.PhysicalPlan.Placement,
            registration.Source,
            storage,
            runtimeBinding,
            executeCommand,
            policy)
    {
    }

    internal PostgresRelationQuerySourceReader(
        CompiledRelationQueryPlan plan,
        CompiledRelationQueryPhysicalPlan physicalPlan,
        RelationQuerySourceInstanceId source,
        PostgresRelationQueryStorageBinding storage,
        PostgresNpgsqlCommandExecutor executeCommand,
        PostgresRelationQuerySourcePolicy policy)
        : this(
            Register(plan, physicalPlan, source, storage),
            storage,
            runtimeBinding: null,
            executeCommand,
            policy)
    {
    }

    internal PostgresRelationQuerySourceReader(
        RelationQueryPhysicalPlanFingerprint physicalPlan,
        RelationQuerySourcePlacement placement,
        RelationQuerySourceInstance source,
        PostgresRelationQueryStorageBinding storage,
        PostgresNpgsqlCommandExecutor executeCommand,
        PostgresRelationQuerySourcePolicy policy)
        : this(
            physicalPlan,
            compiledPhysicalPlan: null,
            semanticPlan: null,
            placement,
            source,
            storage,
            runtimeBinding: null,
            executeCommand,
            policy)
    {
    }

    PostgresRelationQuerySourceReader(
        RelationQueryPhysicalPlanFingerprint physicalPlan,
        CompiledRelationQueryPhysicalPlan? compiledPhysicalPlan,
        CompiledRelationQueryPlan? semanticPlan,
        RelationQuerySourcePlacement placement,
        RelationQuerySourceInstance source,
        PostgresRelationQueryStorageBinding storage,
        PostgresNpgsqlRuntimeBinding? runtimeBinding,
        PostgresNpgsqlCommandExecutor executeCommand,
        PostgresRelationQuerySourcePolicy policy)
    {
        this.physicalPlan = Guard.RequireNotNull(physicalPlan);
        this.compiledPhysicalPlan = compiledPhysicalPlan;
        this.semanticPlan = semanticPlan;
        this.placement = Guard.RequireNotNull(placement);
        this.source = Guard.RequireNotNull(source);
        this.storage = Guard.RequireNotNull(storage);
        this.runtimeBinding = runtimeBinding;
        this.executeCommand = Guard.RequireNotNull(executeCommand);
        Policy = Guard.RequireNotNull(policy);

        if (!source.TargetProfile.HasSameSemantics(PostgresRelationQuerySourceTargetProfile.Default)
            || storage.TargetProfile != PostgresRelationQueryTargetProfile.ProfileId
            || storage.Target != PostgresRelationQueryTargetProfile.Target)
        {
            throw new ArgumentException(
                $"PostgreSQL source readers require source profile '{PostgresRelationQuerySourceTargetProfile.ProfileId.Value}' and a canonical SQL-profile storage binding.",
                nameof(source));
        }
        ValidateStorageAffinity(placement, source, storage, semanticPlan);
        partitions = ResolvePartitions(placement, source, storage, Policy);
        if (compiledPhysicalPlan is not null
            && storage.Tables.Any(table =>
                table.Source == source.Id
                && (table.Identity is { } identity && PostgresNpgsqlExecution.IsTemporal(identity.ScalarType)
                    || table.Partition is { } partition && PostgresNpgsqlExecution.IsTemporal(partition.ScalarType)
                    || table.Fields.Any(static field => PostgresNpgsqlExecution.IsTemporal(field.ScalarType))
                    || table.RelationshipReferences.Any(static reference =>
                        PostgresNpgsqlExecution.IsTemporal(reference.ScalarType)))))
        {
            PostgresNpgsqlExecution.RequireExactTemporalSemantics(Policy.TemporalSemantics);
        }
        if (source.Limits.MaximumBatchSize > Policy.MaximumBatchKeys
            || source.Limits.MaximumBufferedRows > Policy.MaximumRowsPerRead)
        {
            throw new ArgumentException(
                "The source instance advertises batch or row limits wider than the PostgreSQL physical policy.",
                nameof(source));
        }

        Descriptor = new(
            source.Id,
            source.ExecutionDomain,
            source.TargetProfile,
            logicalPartition: Policy.PartitionScope?.LogicalPartition
                ?? RelationQueryLogicalPartitionIdentity.WholeSource,
            partitionBinding: Policy.PartitionScope is { } scope
                ? new(scope.SourceSelector)
                : null);
    }

    /// <inheritdoc />
    public RelationQuerySourceReaderDescriptor Descriptor { get; }

    /// <summary>Exact physical source policy enforced before PostgreSQL I/O.</summary>
    public PostgresRelationQuerySourcePolicy Policy { get; }

    /// <summary>Canonical source-instance limits enforced together with the physical policy.</summary>
    public RelationQuerySourcePlacementLimits Limits => source.Limits;

    /// <summary>Canonical PostgreSQL storage binding interpreted by this reader.</summary>
    public PostgresRelationQueryStorageBinding StorageBinding => storage;

    /// <summary>Exact physical-plan fingerprint authorized by this reader.</summary>
    public RelationQueryPhysicalPlanFingerprint PhysicalPlan => physicalPlan;

    internal PostgresRelationQuerySourceReader WithCommandExecutor(
        PostgresNpgsqlCommandExecutor commandExecutor) => new(
        physicalPlan,
        compiledPhysicalPlan,
        semanticPlan,
        placement,
        source,
        storage,
        runtimeBinding,
        Guard.RequireNotNull(commandExecutor),
        Policy);

    internal PostgresNpgsqlRuntimeBinding? RuntimeBinding => runtimeBinding;

    internal string? RuntimeEvidenceReference => runtimeBinding is null
        ? null
        : string.Concat(
            "npgsql-runtime/authority/",
            Uri.EscapeDataString(runtimeBinding.Authority),
            "/data-source/",
            Uri.EscapeDataString(runtimeBinding.DataSourceFingerprint.Algorithm),
            "/",
            Uri.EscapeDataString(runtimeBinding.DataSourceFingerprint.Canonicalization),
            "/",
            runtimeBinding.DataSourceFingerprint.Value);

    internal ResolvedPartition? ResolvePartition(
        RelationQuerySourcePlacementBindingId placementBinding) =>
        partitions.TryGetValue(placementBinding, out var partition) ? partition : null;

    internal RelationQuerySourcePlacementBinding ResolvePlacement(
        RelationQuerySourcePlacementBindingId placementBinding) =>
        placement.Bindings.SingleOrDefault(binding => binding.Id == placementBinding)
        ?? throw new KeyNotFoundException(
            $"The registered physical plan has no placement '{placementBinding.Value}'.");

    internal bool AuthorizesStage(
        RelationQuerySourcePlacementBindingId placementBinding,
        RelationQueryPhysicalStageKind kind)
    {
        if (compiledPhysicalPlan is not null)
        {
            return compiledPhysicalPlan.Stages.Any(stage =>
                stage.PlacementBinding == placementBinding && stage.Kind == kind);
        }

        var binding = ResolvePlacement(placementBinding);
        return kind switch
        {
            RelationQueryPhysicalStageKind.SourceRead =>
                binding.Kind == RelationQuerySourcePlacementBindingKind.SourceSet
                && binding.Acquisition == RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            RelationQueryPhysicalStageKind.BatchedIdentityLookup =>
                binding.Kind == RelationQuerySourcePlacementBindingKind.RelationshipTraversal
                && binding.Acquisition == RelationQuerySourceAcquisitionKind.BoundedLookup
                && binding.RelationshipKeys.IsDefaultOrEmpty,
            RelationQueryPhysicalStageKind.BatchedPredicateLookup =>
                binding.Kind == RelationQuerySourcePlacementBindingKind.RelationshipTraversal
                && binding.Acquisition == RelationQuerySourceAcquisitionKind.BoundedLookup
                && (!binding.RelationshipKeys.IsDefaultOrEmpty
                    || storage.OwnedCollections.Any(collection =>
                        collection.RootPlacementBinding == binding.Id)),
            _ => false
        };
    }

    internal ImmutableArray<RelationQueryPhysicalStage> ResolveAuthorizedStages(
        RelationQuerySourcePlacementBindingId placementBinding) => compiledPhysicalPlan is null
        ? []
        :
        [
            .. compiledPhysicalPlan.Stages
                .Where(stage => stage.PlacementBinding == placementBinding)
                .OrderBy(static stage => stage.Id.Value, StringComparer.Ordinal)
        ];

    static Registration Register(
        CompiledRelationQueryPlan plan,
        CompiledRelationQueryPhysicalPlan physicalPlan,
        RelationQuerySourceInstanceId source,
        PostgresRelationQueryStorageBinding storage)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(physicalPlan);
        ArgumentNullException.ThrowIfNull(storage);
        var semanticPlanFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(
            RelationQueryCompiledPlanReference.From(plan));
        var physicalPlanFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(
            physicalPlan.Plan);
        if (!Equals(semanticPlanFingerprint, physicalPlanFingerprint))
        {
            throw new ArgumentException(
                "The semantic compiled plan does not belong to the registered physical plan.",
                nameof(plan));
        }
        if (string.IsNullOrWhiteSpace(source.Value))
        {
            throw new ArgumentException("A PostgreSQL source reader requires a source identity.", nameof(source));
        }

        var instance = physicalPlan.Placement.SourceInstances.SingleOrDefault(candidate => candidate.Id == source)
            ?? throw new ArgumentException(
                $"Physical plan '{physicalPlan.Fingerprint.Value}' has no source '{source.Value}'.",
                nameof(source));
        return new(plan, physicalPlan, instance);
    }

    static void ValidateStorageAffinity(
        RelationQuerySourcePlacement placement,
        RelationQuerySourceInstance source,
        PostgresRelationQueryStorageBinding storage,
        CompiledRelationQueryPlan? plan)
    {
        var expectedPlan = RelationQueryCompiledPlanReferenceFingerprinter.Compute(placement.Plan);
        if (!Equals(storage.CompiledPlanFingerprint, expectedPlan)
            || !Equals(storage.PlacementFingerprint, placement.Fingerprint))
        {
            throw new ArgumentException(
                "The PostgreSQL storage binding does not belong to the registered compiled plan and placement.",
                nameof(storage));
        }

        var acquired = placement.Bindings
            .Where(static binding => binding.Acquisition != RelationQuerySourceAcquisitionKind.Supplied)
            .OrderBy(static binding => binding.Id.Value, StringComparer.Ordinal)
            .ToArray();
        if (acquired.Length != storage.Tables.Length)
        {
            throw new ArgumentException(
                "The PostgreSQL storage binding must cover every and only externally acquired placement.",
                nameof(storage));
        }
        foreach (var binding in acquired)
        {
            var table = storage.Tables.SingleOrDefault(candidate => candidate.PlacementBinding == binding.Id);
            if (GetTableCoverageMismatch(binding, table, storage.OwnedCollections) is { } mismatch)
            {
                throw new ArgumentException(
                    $"PostgreSQL table coverage conflicts with placement '{binding.Id.Value}': {mismatch}",
                    nameof(storage));
            }
        }
        if (!acquired.Any(binding => binding.Source == source.Id))
        {
            throw new ArgumentException(
                $"The registered source '{source.Id.Value}' has no externally acquired PostgreSQL placement.",
                nameof(source));
        }
        if (plan is not null)
        {
            var fields = plan.InputContract.Sources
                .SelectMany(static contract => contract.Fields)
                .Concat(plan.InputContract.Traversals.SelectMany(static contract => contract.Fields))
                .ToArray();
            var semanticErrors = PostgresRelationQueryBindingSemanticValidator.ValidateRegistration(
                plan,
                storage.Tables,
                placement.Bindings.ToDictionary(static binding => binding.Input),
                fields,
                plan.InputContract.Traversals,
                acquired.Select(static binding => binding.Binding).ToHashSet());
            if (!semanticErrors.IsDefaultOrEmpty)
            {
                throw new ArgumentException(
                    $"The PostgreSQL storage binding is not semantically exact: {semanticErrors[0]}",
                    nameof(storage));
            }
        }
    }

    static ImmutableDictionary<RelationQuerySourcePlacementBindingId, ResolvedPartition> ResolvePartitions(
        RelationQuerySourcePlacement placement,
        RelationQuerySourceInstance source,
        PostgresRelationQueryStorageBinding storage,
        PostgresRelationQuerySourcePolicy policy)
    {
        var scope = policy.PartitionScope;
        var builder = ImmutableDictionary.CreateBuilder<RelationQuerySourcePlacementBindingId, ResolvedPartition>();
        foreach (var canonical in placement.Bindings.Where(binding =>
                     binding.Source == source.Id
                     && binding.Acquisition != RelationQuerySourceAcquisitionKind.Supplied))
        {
            var table = storage.ResolveTable(canonical.Id);
            if (canonical.Partition is null && table.Partition is null)
            {
                if (scope is not null)
                {
                    throw new ArgumentException(
                        "A fixed PostgreSQL partition scope cannot serve an unpartitioned placement.",
                        nameof(policy));
                }
                continue;
            }
            if (canonical.Partition is not { } canonicalPartition
                || table.Partition is not { } physicalPartition
                || scope is null)
            {
                throw new ArgumentException(
                    "A partitioned PostgreSQL placement requires matching physical column evidence and one exact runtime scope.",
                    nameof(policy));
            }
            if (!string.Equals(canonicalPartition.SourceSelector, physicalPartition.SourceSelector, StringComparison.Ordinal)
                || !string.Equals(canonicalPartition.SourceSelector, scope.SourceSelector, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The PostgreSQL placement, table binding, and runtime partition scope use different selectors.",
                    nameof(policy));
            }
            if (Encoding.UTF8.GetByteCount(scope.CanonicalValue) > policy.MaximumKeyBytes)
            {
                throw new ArgumentException(
                    "The PostgreSQL partition value exceeds the canonical key byte bound.",
                    nameof(policy));
            }

            object value;
            try
            {
                value = PostgresRelationQueryScalarCatalog.ParseKey(
                    scope.CanonicalValue,
                    physicalPartition.ScalarType);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    "The PostgreSQL partition value has no exact scalar representation.",
                    nameof(policy),
                    exception);
            }
            if (!string.Equals(
                    PostgresRelationQueryScalarCatalog.FormatKey(value, physicalPartition.ScalarType),
                    scope.CanonicalValue,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The PostgreSQL partition value is not in canonical scalar form.",
                    nameof(policy));
            }
            builder.Add(
                canonical.Id,
                new(physicalPartition, value, scope.ComputeDigest(physicalPartition)));
        }
        return builder.ToImmutable();
    }

    static string? GetTableCoverageMismatch(
        RelationQuerySourcePlacementBinding binding,
        PostgresRelationQueryTableBinding? table,
        ImmutableArray<PostgresRelationQueryOwnedCollectionBinding> ownedCollections)
    {
        if (table is null)
        {
            return "no table binding exists";
        }

        if (table.Input != binding.Input)
        {
            return "the compiled input identity differs";
        }

        if (table.Source != binding.Source)
        {
            return "the physical source identity differs";
        }

        if (table.Shape != binding.Shape)
        {
            return "the semantic shape differs";
        }

        if ((binding.Partition is { } canonicalPartition
             && (table.Partition is not { } physicalPartition
                 || !string.Equals(
                     physicalPartition.SourceSelector,
                     canonicalPartition.SourceSelector,
                     StringComparison.Ordinal)))
            || (binding.Partition is null && table.Partition is not null))
        {
            return "the logical partition selector lacks exact physical column evidence";
        }

        if (binding.Identity is not { } canonicalIdentity || canonicalIdentity.Shape != binding.Shape)
        {
            return "the placement lacks shape-affine identity evidence";
        }

        if (table.Identity is null)
        {
            return "the table lacks identity-column evidence";
        }

        var owned = ownedCollections
            .Where(collection => collection.RootPlacementBinding == binding.Id)
            .ToArray();
        if (table.Fields.Length + owned.Length != binding.Fields.Length)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the root and owned-component tables map {table.Fields.Length + owned.Length} fields while the placement requires {binding.Fields.Length}");
        }
        var missingField = binding.Fields.FirstOrDefault(field =>
            !table.Fields.Any(candidate =>
                candidate.Input == field.Input
                && candidate.SemanticPath == field.SemanticPath)
            && !owned.Any(candidate =>
                candidate.CollectionInput == field.Input
                && candidate.CollectionPath == field.SemanticPath));
        if (missingField is not null)
        {
            return $"field '{missingField.Input.Value}' at '{missingField.SemanticPath}' is absent";
        }

        var missingRelationship = binding.RelationshipKeys.FirstOrDefault(key =>
            !table.RelationshipReferences.Any(candidate =>
                candidate.Input == key.Input
                && candidate.SemanticPath == key.SemanticPath));
        return missingRelationship is null
            ? null
            : $"relationship reference '{missingRelationship.Input.Value}' at '{missingRelationship.SemanticPath}' is absent";
    }

    /// <inheritdoc />
    public async ValueTask<RelationQuerySourceReadResult> ReadAsync(
        RelationQuerySourceReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var maximumRows = EffectiveReadBoundary(request);
        var window = await ReadWindowAsync(
            request,
            afterIdentity: null,
            maximumRows,
            priorFanOut: [],
            cancellationToken).ConfigureAwait(false);
        return window.Read;
    }

    internal async ValueTask<PostgresRelationQueryReadWindow> ReadWindowAsync(
        RelationQuerySourceReadRequest request,
        object? afterIdentity,
        int maximumRows,
        ImmutableArray<PostgresRelationQueryFanOutCount> priorFanOut,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumRows <= 0 || maximumRows > Policy.MaximumRowsPerRead)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRows),
                maximumRows,
                "A PostgreSQL read window must fit the physical row policy.");
        }

        try
        {
            if (ValidateRequest(
                    request,
                    out var table,
                    out var projection,
                    out var ownedCollection,
                    out var relationship,
                    out var stage) is { } invalid)
            {
                return FailedWindow(request, invalid);
            }

            if (Transaction.Current is not null)
            {
                return FailedWindow(request, "ambient-transaction-not-supported");
            }

            if (BatchBoundaryExceeded(request.Constraint, stage))
            {
                return InconclusiveWindow(request, "batch-boundary-exceeded");
            }

            var command = ownedCollection is null
                ? BuildCommand(
                    request,
                    table!,
                    projection,
                    relationship,
                    afterIdentity,
                    checked(maximumRows + 1))
                : BuildOwnedCollectionCommand(
                    request,
                    table!,
                    projection,
                    ownedCollection,
                    relationship,
                    afterIdentity,
                    checked(maximumRows + 1));
            var result = await executeCommand(command, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ownedCollection is null
                ? Materialize(
                    request,
                    table!,
                    projection,
                    relationship,
                    result.Rows,
                    maximumRows,
                    afterIdentity is not null,
                    priorFanOut,
                    cancellationToken)
                : MaterializeOwnedCollection(
                    request,
                    table!,
                    projection,
                    ownedCollection,
                    result.Rows,
                    maximumRows,
                    afterIdentity is not null,
                    cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException exception)
        {
            return FailedWindow(request, $"provider-sqlstate/{Uri.EscapeDataString(exception.SqlState)}");
        }
        catch (NpgsqlException exception)
        {
            return FailedWindow(
                request,
                $"provider-npgsql/{Uri.EscapeDataString(exception.GetType().Name)}");
        }
        catch (PostgresNpgsqlResultByteLimitExceededException)
        {
            return FailedWindow(request, "provider-result-byte-boundary-exceeded");
        }
        catch (TimeoutException)
        {
            return FailedWindow(request, "provider-timeout");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return FailedWindow(
                request,
                $"provider-read-failed/{Uri.EscapeDataString(exception.GetType().Name)}");
        }
    }

    internal int EffectiveReadBoundary(RelationQuerySourceReadRequest request)
    {
        var constraintBound = request.Constraint is RelationQueryBoundedEnumeration enumeration
            ? enumeration.MaximumRows
            : long.MaxValue;
        return checked((int)Math.Min(
            constraintBound,
            Math.Min(
                request.MaximumBufferedRows,
                Math.Min(source.Limits.MaximumBufferedRows, Policy.MaximumRowsPerRead))));
    }

    string? ValidateRequest(
        RelationQuerySourceReadRequest request,
        out PostgresRelationQueryTableBinding? table,
        out ImmutableArray<PostgresRelationQueryProjectionBinding> projection,
        out PostgresRelationQueryOwnedCollectionProjectionBinding? ownedCollection,
        out PostgresRelationQueryRelationshipReferenceBinding? relationship,
        out RelationQueryPhysicalStage? stage)
    {
        table = null;
        projection = [];
        ownedCollection = null;
        relationship = null;
        stage = null;
        if (request.PhysicalPlan != physicalPlan)
        {
            return "physical-plan-mismatch";
        }

        if (request.Source != source.Id)
        {
            return "source-mismatch";
        }

        var canonical = placement.Bindings.SingleOrDefault(binding => binding.Id == request.PlacementBinding);
        if (canonical is null || canonical.Source != source.Id || canonical.Shape != request.Shape)
        {
            return "placement-binding-mismatch";
        }

        var expectedStageKind = request.Constraint switch
        {
            RelationQueryBoundedEnumeration => RelationQueryPhysicalStageKind.SourceRead,
            RelationQueryIdentityBatchLookup => RelationQueryPhysicalStageKind.BatchedIdentityLookup,
            RelationQueryRelationshipKeyBatchLookup => RelationQueryPhysicalStageKind.BatchedPredicateLookup,
            RelationQueryCollectionElementKeyBatchLookup => RelationQueryPhysicalStageKind.BatchedPredicateLookup,
            _ => (RelationQueryPhysicalStageKind?)null
        };
        if (expectedStageKind is null
            || request.Constraint is RelationQueryBoundedEnumeration
                && (canonical.Kind != RelationQuerySourcePlacementBindingKind.SourceSet
                    || canonical.Acquisition != RelationQuerySourceAcquisitionKind.BoundedEnumeration)
            || request.Constraint is RelationQueryIdentityBatchLookup
                && (canonical.Kind != RelationQuerySourcePlacementBindingKind.RelationshipTraversal
                    || canonical.Acquisition != RelationQuerySourceAcquisitionKind.BoundedLookup)
            || request.Constraint is RelationQueryRelationshipKeyBatchLookup
                && (canonical.Kind != RelationQuerySourcePlacementBindingKind.RelationshipTraversal
                    || canonical.Acquisition != RelationQuerySourceAcquisitionKind.BoundedLookup)
            || request.Constraint is RelationQueryCollectionElementKeyBatchLookup
                && (canonical.Kind != RelationQuerySourcePlacementBindingKind.RelationshipTraversal
                    || canonical.Acquisition != RelationQuerySourceAcquisitionKind.BoundedLookup))
        {
            return "constraint-placement-mismatch";
        }
        if (!AuthorizesStage(canonical.Id, expectedStageKind.Value))
        {
            return "physical-stage-mismatch";
        }

        if (compiledPhysicalPlan is not null)
        {
            stage = compiledPhysicalPlan.Stages.SingleOrDefault(candidate => candidate.Id == request.Stage);
            if (stage is null
                || stage.Kind != expectedStageKind
                || stage.PlacementBinding != canonical.Id)
            {
                return "physical-stage-mismatch";
            }
            if (!HasExactRequestedFields(request.Fields, stage.RequestedFields))
            {
                return "physical-stage-fields-mismatch";
            }
        }

        try
        {
            table = storage.ResolveTable(request.PlacementBinding);
        }
        catch (KeyNotFoundException)
        {
            return "placement-binding-mismatch";
        }
        if (table.Source != source.Id || table.Shape != request.Shape)
        {
            return "table-affinity-mismatch";
        }

        if (table.Identity is not { } identity)
        {
            return "identity-binding-missing";
        }

        if (canonical.Identity is not { } canonicalIdentity
            || !string.Equals(request.IdentitySelector, canonicalIdentity.SourceSelector, StringComparison.Ordinal))
        {
            return "identity-selector-mismatch";
        }

        if (identity.ScalarType == PostgresRelationQueryScalarType.Text
            && identity.TextSemantics?.Equality != PostgresRelationQueryTextEqualitySemantics.Ordinal)
        {
            return "identity-text-equality-unproven";
        }

        var fields = ImmutableArray.CreateBuilder<PostgresRelationQueryProjectionBinding>(request.Fields.Length);
        foreach (var requested in request.Fields)
        {
            var canonicalSemantic = requested.Input is { } input
                ? canonical.Fields.SingleOrDefault(candidate =>
                    candidate.Input == input
                    && candidate.SemanticPath == requested.SemanticPath
                    && string.Equals(candidate.SourceSelector, requested.SourceSelector, StringComparison.Ordinal))
                : null;
            var semantic = canonicalSemantic is null
                ? null
                : table.Fields.SingleOrDefault(candidate =>
                    candidate.Input == canonicalSemantic.Input
                    && candidate.SemanticPath == canonicalSemantic.SemanticPath);
            var owned = canonicalSemantic is null
                ? null
                : storage.OwnedCollections.SingleOrDefault(candidate =>
                    candidate.RootPlacementBinding == canonical.Id
                    && candidate.CollectionInput == canonicalSemantic.Input
                    && candidate.CollectionPath == canonicalSemantic.SemanticPath);
            var canonicalCorrelation = canonical.RelationshipKeys.SingleOrDefault(candidate =>
                candidate.SemanticPath == requested.SemanticPath
                && string.Equals(candidate.SourceSelector, requested.SourceSelector, StringComparison.Ordinal));
            var correlation = canonicalCorrelation is null
                ? null
                : table.RelationshipReferences.SingleOrDefault(candidate =>
                    candidate.Input == canonicalCorrelation.Input
                    && candidate.SemanticPath == canonicalCorrelation.SemanticPath);
            var valid = requested.Purpose switch
            {
                RelationQuerySourceReadFieldPurpose.SemanticInput => semantic is not null || owned is not null,
                RelationQuerySourceReadFieldPurpose.Correlation => correlation is not null,
                RelationQuerySourceReadFieldPurpose.SemanticInputAndCorrelation =>
                    semantic is not null && correlation is not null
                    && string.Equals(semantic.ColumnName, correlation.ColumnName, StringComparison.Ordinal)
                    && semantic.ScalarType == correlation.ScalarType
                    && semantic.MissingValueEncoding == correlation.MissingValueEncoding
                    && semantic.NullValueEncoding == correlation.NullValueEncoding
                    && Equals(semantic.TextSemantics, correlation.TextSemantics),
                _ => false
            };
            if (!valid)
            {
                return "field-selector-mismatch";
            }

            if (owned is not null)
            {
                if (ownedCollection is not null)
                {
                    return "multiple-owned-collections-unsupported";
                }
                ownedCollection = new(requested, owned);
            }
            else
            {
                fields.Add(semantic is not null
                    ? new(
                    requested,
                    semantic.ColumnName,
                    semantic.ScalarType,
                    semantic.MissingValueEncoding,
                    semantic.NullValueEncoding,
                    semantic.TextSemantics)
                    : new(
                    requested,
                    correlation!.ColumnName,
                    correlation.ScalarType,
                    correlation.MissingValueEncoding,
                    correlation.NullValueEncoding,
                    correlation.TextSemantics));
            }
        }
        projection = fields.Count == fields.Capacity
            ? fields.MoveToImmutable()
            : fields.ToImmutable();

        if (request.Constraint is RelationQueryRelationshipKeyBatchLookup lookup)
        {
            var canonicalRelationship = canonical.RelationshipKeys.SingleOrDefault(candidate =>
                candidate.SemanticPath == lookup.RelationshipReference
                && string.Equals(candidate.SourceSelector, lookup.SourceSelector, StringComparison.Ordinal));
            relationship = canonicalRelationship is null
                ? null
                : table.RelationshipReferences.SingleOrDefault(candidate =>
                    candidate.Input == canonicalRelationship.Input
                    && candidate.SemanticPath == canonicalRelationship.SemanticPath);
            if (canonicalRelationship is null || relationship is null)
            {
                return "relationship-selector-mismatch";
            }

            if (relationship.ScalarType == PostgresRelationQueryScalarType.Text
                && relationship.TextSemantics?.Equality != PostgresRelationQueryTextEqualitySemantics.Ordinal)
            {
                return "relationship-text-equality-unproven";
            }
        }

        PostgresRelationQueryOwnedCollectionElementFieldBinding? collectionElement = null;
        if (request.Constraint is RelationQueryCollectionElementKeyBatchLookup collectionLookup)
        {
            if (ownedCollection is null
                || ownedCollection.Binding.CollectionInput != collectionLookup.CollectionInput
                || ownedCollection.Binding.CollectionPath != collectionLookup.CollectionPath)
            {
                return "owned-collection-occurrence-mismatch";
            }

            try
            {
                collectionElement = ownedCollection.Binding.ResolveField(collectionLookup.ElementReference);
            }
            catch (KeyNotFoundException)
            {
                return "owned-collection-element-reference-mismatch";
            }

            if (collectionElement.ScalarType == PostgresRelationQueryScalarType.Text
                && collectionElement.TextSemantics?.Equality != PostgresRelationQueryTextEqualitySemantics.Ordinal)
            {
                return "owned-collection-element-text-equality-unproven";
            }
        }

        var keyValidationFailure = request.Constraint switch
        {
            RelationQueryIdentityBatchLookup identityLookup =>
                ValidateCanonicalKeys(identityLookup.Identities, identity.ScalarType, Policy.MaximumKeyBytes),
            RelationQueryRelationshipKeyBatchLookup relationshipLookup =>
                ValidateCanonicalKeys(relationshipLookup.Keys, relationship!.ScalarType, Policy.MaximumKeyBytes),
            RelationQueryCollectionElementKeyBatchLookup collectionKeys =>
                ValidateCanonicalKeys(collectionKeys.Keys, collectionElement!.ScalarType, Policy.MaximumKeyBytes),
            _ => null
        };
        if (keyValidationFailure is not null)
        {
            return keyValidationFailure;
        }

        return null;
    }

    PostgresNpgsqlCommand BuildCommand(
        RelationQuerySourceReadRequest request,
        PostgresRelationQueryTableBinding table,
        ImmutableArray<PostgresRelationQueryProjectionBinding> projection,
        PostgresRelationQueryRelationshipReferenceBinding? relationship,
        object? afterIdentity,
        int probeLimit)
    {
        var identity = table.Identity!;
        var partition = ResolvePartition(table.PlacementBinding);
        if (relationship is not null
            && request.Constraint is RelationQueryRelationshipKeyBatchLookup relationshipLookup)
        {
            return BuildBoundedRelationshipCommand(
                table,
                projection,
                identity,
                relationship,
                partition,
                ParseKeys(relationshipLookup.Keys, relationship.ScalarType),
                afterIdentity,
                probeLimit,
                Policy.MaximumPageBytes);
        }

        var identityExpression = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
            PostgresSqlExpression.Column(SourceAlias, identity.ColumnName),
            identity.ScalarType,
            identity.TextSemantics);
        var builder = new PostgresSqlSelectBuilder(
                new PostgresSqlQualifiedTable(table.SchemaName, table.TableName),
                SourceAlias)
            .Select(PostgresSqlExpression.Column(SourceAlias, identity.ColumnName), IdentityAlias);
        for (var index = 0; index < projection.Length; index++)
        {
            var item = projection[index];
            builder.Select(
                PostgresSqlExpression.Column(SourceAlias, item.ColumnName),
                $"_field{index.ToString(CultureInfo.InvariantCulture)}");
        }

        if (partition is not null)
        {
            var partitionExpression = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
                PostgresSqlExpression.Column(SourceAlias, partition.Binding.ColumnName),
                partition.Binding.ScalarType,
                partition.Binding.TextSemantics);
            builder.Where(PostgresSqlExpression.Binary(
                PostgresSqlBinaryOperator.Equal,
                partitionExpression,
                PostgresSqlExpression.RuntimeParameter(PartitionBinding)));
        }

        ImmutableArray<object> parsedKeys = [];
        PostgresRelationQueryScalarType? keyType = null;
        PostgresRelationQueryTextSemantics? keyText = null;
        switch (request.Constraint)
        {
            case RelationQueryIdentityBatchLookup lookup:
                parsedKeys = ParseKeys(lookup.Identities, identity.ScalarType);
                keyType = identity.ScalarType;
                keyText = identity.TextSemantics;
                break;
        }
        if (keyType is { } scalarType)
        {
            var columnName = relationship?.ColumnName ?? identity.ColumnName;
            var operand = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
                PostgresSqlExpression.Column(SourceAlias, columnName),
                scalarType,
                keyText);
            builder.Where(PostgresSqlExpression.EqualAny(operand, KeysBinding));
        }
        if (afterIdentity is not null)
        {
            builder.Where(PostgresSqlExpression.KeysetAfter(
            [
                new(
                    identityExpression,
                    PostgresSqlExpression.RuntimeParameter(AfterBinding),
                    PostgresSqlSortDirection.Ascending,
                    PostgresSqlNullPlacement.Last)
            ]));
        }

        var template = builder
            .OrderBy(identityExpression)
            .Limit(probeLimit)
            .BuildTemplate();
        var parameters = ImmutableArray.CreateBuilder<PostgresNpgsqlParameter>(template.Parameters.Length);
        foreach (var parameter in template.Parameters)
        {
            parameters.Add(parameter.Binding switch
            {
                KeysBinding when keyType is { } type => new(
                    PostgresRelationQueryScalarCatalog.CreateArray(parsedKeys, type),
                    type,
                    IsArray: true),
                AfterBinding => new(afterIdentity!, identity.ScalarType, IsArray: false),
                PartitionBinding when partition is not null => new(
                    partition.Value,
                    partition.Binding.ScalarType,
                    IsArray: false),
                _ => throw new InvalidOperationException(
                    $"Unexpected PostgreSQL source parameter '{parameter.Binding ?? "<constant>"}'.")
            });
        }

        var resultTypes = ImmutableArray.CreateBuilder<PostgresRelationQueryScalarType>(1 + projection.Length);
        resultTypes.Add(identity.ScalarType);
        foreach (var item in projection)
        {
            resultTypes.Add(item.ScalarType);
        }

        return new(
            template.Text,
            parameters.MoveToImmutable(),
            resultTypes.MoveToImmutable(),
            Policy.MaximumPageBytes);
    }

    PostgresNpgsqlCommand BuildOwnedCollectionCommand(
        RelationQuerySourceReadRequest request,
        PostgresRelationQueryTableBinding table,
        ImmutableArray<PostgresRelationQueryProjectionBinding> projection,
        PostgresRelationQueryOwnedCollectionProjectionBinding ownedProjection,
        PostgresRelationQueryRelationshipReferenceBinding? relationship,
        object? afterIdentity,
        int probeLimit)
    {
        var identity = table.Identity!;
        var partition = ResolvePartition(table.PlacementBinding)
            ?? throw new InvalidOperationException(
                "A decomposed owned collection requires an exact tenant partition scope.");
        var owned = ownedProjection.Binding;
        if (owned.ParentRoot.SemanticPath != identity.SemanticPath
            || owned.ParentRoot.ScalarType != identity.ScalarType
            || !Equals(owned.ParentRoot.TextSemantics, identity.TextSemantics)
            || owned.Partition.SemanticPath != partition.Binding.SemanticPath
            || owned.Partition.ScalarType != partition.Binding.ScalarType
            || !Equals(owned.Partition.TextSemantics, partition.Binding.TextSemantics)
            || !string.Equals(
                owned.Partition.SourceSelector,
                partition.Binding.SourceSelector,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The PostgreSQL component parent or partition binding does not preserve the root key domain.");
        }

        var rootIdentity = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
            PostgresSqlExpression.Column(SourceAlias, identity.ColumnName),
            identity.ScalarType,
            identity.TextSemantics);
        var rootPartition = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
            PostgresSqlExpression.Column(SourceAlias, partition.Binding.ColumnName),
            partition.Binding.ScalarType,
            partition.Binding.TextSemantics);
        var rootPage = new PostgresSqlSelectBuilder(
                new PostgresSqlQualifiedTable(table.SchemaName, table.TableName),
                SourceAlias)
            .Select(PostgresSqlExpression.Column(SourceAlias, identity.ColumnName), IdentityAlias)
            .Select(
                PostgresSqlExpression.Column(SourceAlias, partition.Binding.ColumnName),
                RootPartitionAlias);
        for (var index = 0; index < projection.Length; index++)
        {
            rootPage.Select(
                PostgresSqlExpression.Column(SourceAlias, projection[index].ColumnName),
                $"_field{index.ToString(CultureInfo.InvariantCulture)}");
        }
        rootPage.Where(PostgresSqlExpression.Binary(
            PostgresSqlBinaryOperator.Equal,
            rootPartition,
            PostgresSqlExpression.RuntimeParameter(PartitionBinding)));

        ImmutableArray<object> parsedKeys = [];
        PostgresRelationQueryScalarType? keyType = null;
        PostgresRelationQueryTextSemantics? keyText = null;
        string? keyColumn = null;
        PostgresSqlExpression? collectionPredicate = null;
        switch (request.Constraint)
        {
            case RelationQueryIdentityBatchLookup lookup:
                parsedKeys = ParseKeys(lookup.Identities, identity.ScalarType);
                keyType = identity.ScalarType;
                keyText = identity.TextSemantics;
                keyColumn = identity.ColumnName;
                break;
            case RelationQueryRelationshipKeyBatchLookup lookup when relationship is not null:
                parsedKeys = ParseKeys(lookup.Keys, relationship.ScalarType);
                keyType = relationship.ScalarType;
                keyText = relationship.TextSemantics;
                keyColumn = relationship.ColumnName;
                break;
            case RelationQueryCollectionElementKeyBatchLookup lookup:
                if (owned.CollectionInput != lookup.CollectionInput
                    || owned.CollectionPath != lookup.CollectionPath)
                {
                    throw new InvalidOperationException(
                        "The collection-element predicate does not belong to the projected owned collection.");
                }
                var element = owned.ResolveField(lookup.ElementReference);
                parsedKeys = ParseKeys(lookup.Keys, element.ScalarType);
                keyType = element.ScalarType;
                keyText = element.TextSemantics;
                var occurrenceParent = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
                    PostgresSqlExpression.Column(OccurrenceAlias, owned.ParentRoot.ColumnName),
                    owned.ParentRoot.ScalarType,
                    owned.ParentRoot.TextSemantics);
                var occurrencePartition = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
                    PostgresSqlExpression.Column(OccurrenceAlias, owned.Partition.ColumnName),
                    owned.Partition.ScalarType,
                    owned.Partition.TextSemantics);
                var occurrenceReference = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
                    PostgresSqlExpression.Column(OccurrenceAlias, element.ColumnName),
                    element.ScalarType,
                    element.TextSemantics);
                var occurrenceQuery = new PostgresSqlSelectBuilder(
                        new PostgresSqlQualifiedTable(owned.SchemaName, owned.TableName),
                        OccurrenceAlias)
                    .Select(
                        PostgresSqlExpression.Column(OccurrenceAlias, owned.ParentRoot.ColumnName),
                        "_exists")
                    .Where(PostgresSqlExpression.Binary(
                        PostgresSqlBinaryOperator.Equal,
                        occurrenceParent,
                        rootIdentity))
                    .Where(PostgresSqlExpression.Binary(
                        PostgresSqlBinaryOperator.Equal,
                        occurrencePartition,
                        rootPartition))
                    .Where(PostgresSqlExpression.EqualAny(occurrenceReference, KeysBinding))
                    .BuildQuery();
                collectionPredicate = PostgresSqlExpression.Exists(occurrenceQuery);
                break;
        }
        if (collectionPredicate is not null)
        {
            rootPage.Where(collectionPredicate);
        }
        else if (keyType is { } scalarType)
        {
            var keyExpression = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
                PostgresSqlExpression.Column(SourceAlias, keyColumn!),
                scalarType,
                keyText);
            rootPage.Where(PostgresSqlExpression.EqualAny(keyExpression, KeysBinding));
        }

        if (afterIdentity is not null)
        {
            rootPage.Where(PostgresSqlExpression.KeysetAfter(
            [
                new(
                    rootIdentity,
                    PostgresSqlExpression.RuntimeParameter(AfterBinding),
                    PostgresSqlSortDirection.Ascending,
                    PostgresSqlNullPlacement.Last)
            ]));
        }
        var boundedRoots = rootPage
            .OrderBy(rootIdentity)
            .Limit(probeLimit)
            .BuildQuery();

        var pageIdentity = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
            PostgresSqlExpression.Column(RootPageAlias, IdentityAlias),
            identity.ScalarType,
            identity.TextSemantics);
        var componentParent = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
            PostgresSqlExpression.Column(ComponentAlias, owned.ParentRoot.ColumnName),
            owned.ParentRoot.ScalarType,
            owned.ParentRoot.TextSemantics);
        var pagePartition = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
            PostgresSqlExpression.Column(RootPageAlias, RootPartitionAlias),
            partition.Binding.ScalarType,
            partition.Binding.TextSemantics);
        var componentPartition = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
            PostgresSqlExpression.Column(ComponentAlias, owned.Partition.ColumnName),
            owned.Partition.ScalarType,
            owned.Partition.TextSemantics);
        var join = PostgresSqlExpression.Binary(
            PostgresSqlBinaryOperator.And,
            PostgresSqlExpression.Binary(
                PostgresSqlBinaryOperator.Equal,
                pageIdentity,
                componentParent),
            PostgresSqlExpression.Binary(
                PostgresSqlBinaryOperator.Equal,
                pagePartition,
                componentPartition));
        var builder = new PostgresSqlSelectBuilder(boundedRoots, RootPageAlias)
            .Select(PostgresSqlExpression.Column(RootPageAlias, IdentityAlias), IdentityAlias);
        for (var index = 0; index < projection.Length; index++)
        {
            var alias = $"_field{index.ToString(CultureInfo.InvariantCulture)}";
            builder.Select(PostgresSqlExpression.Column(RootPageAlias, alias), alias);
        }
        builder.Join(
            new PostgresSqlQualifiedTable(owned.SchemaName, owned.TableName),
            ComponentAlias,
            PostgresSqlJoinKind.Left,
            join);
        for (var index = 0; index < owned.Fields.Length; index++)
        {
            builder.Select(
                PostgresSqlExpression.Column(ComponentAlias, owned.Fields[index].ColumnName),
                $"_owned{index.ToString(CultureInfo.InvariantCulture)}");
        }

        var ordinal = owned.ResolveField(owned.OrdinalPath);
        var localIdentity = owned.ResolveField(owned.LocalIdentityPath);
        var statement = builder
            .OrderBy(pageIdentity)
            .OrderBy(PostgresRelationQueryScalarCatalog.ApplyTextCollation(
                PostgresSqlExpression.Column(ComponentAlias, ordinal.ColumnName),
                ordinal.ScalarType,
                ordinal.TextSemantics))
            .OrderBy(PostgresRelationQueryScalarCatalog.ApplyTextCollation(
                PostgresSqlExpression.Column(ComponentAlias, localIdentity.ColumnName),
                localIdentity.ScalarType,
                localIdentity.TextSemantics))
            .BuildTemplate();
        var parameters = ImmutableArray.CreateBuilder<PostgresNpgsqlParameter>(statement.Parameters.Length);
        foreach (var parameter in statement.Parameters)
        {
            parameters.Add(parameter.Binding switch
            {
                KeysBinding when keyType is { } type => new(
                    PostgresRelationQueryScalarCatalog.CreateArray(parsedKeys, type),
                    type,
                    IsArray: true),
                AfterBinding => new(afterIdentity!, identity.ScalarType, IsArray: false),
                PartitionBinding => new(
                    partition.Value,
                    partition.Binding.ScalarType,
                    IsArray: false),
                _ => throw new InvalidOperationException(
                    $"Unexpected PostgreSQL owned-collection parameter '{parameter.Binding ?? "<constant>"}'.")
            });
        }

        var resultTypes = ImmutableArray.CreateBuilder<PostgresRelationQueryScalarType>(
            1 + projection.Length + owned.Fields.Length);
        resultTypes.Add(identity.ScalarType);
        foreach (var item in projection)
            resultTypes.Add(item.ScalarType);
        foreach (var field in owned.Fields)
            resultTypes.Add(field.ScalarType);
        return new(
            statement.Text,
            parameters.MoveToImmutable(),
            resultTypes.MoveToImmutable(),
            Policy.MaximumPageBytes);
    }

    static PostgresNpgsqlCommand BuildBoundedRelationshipCommand(
        PostgresRelationQueryTableBinding table,
        ImmutableArray<PostgresRelationQueryProjectionBinding> projection,
        PostgresRelationQueryIdentityBinding identity,
        PostgresRelationQueryRelationshipReferenceBinding relationship,
        ResolvedPartition? partition,
        ImmutableArray<object> parsedKeys,
        object? afterIdentity,
        int probeLimit,
        long maximumResultBytes)
    {
        var identityExpression = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
            PostgresSqlExpression.Column(SourceAlias, identity.ColumnName),
            identity.ScalarType,
            identity.TextSemantics);
        var relationshipExpression = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
            PostgresSqlExpression.Column(SourceAlias, relationship.ColumnName),
            relationship.ScalarType,
            relationship.TextSemantics);
        var requestedKeyExpression = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
            PostgresSqlExpression.Column(RequestedAlias, RequestedKeyAlias),
            relationship.ScalarType,
            relationship.TextSemantics);
        var candidateBuilder = new PostgresSqlSelectBuilder(
                new PostgresSqlQualifiedTable(table.SchemaName, table.TableName),
                SourceAlias)
            .Select(PostgresSqlExpression.Column(SourceAlias, identity.ColumnName), IdentityAlias);
        for (var index = 0; index < projection.Length; index++)
        {
            candidateBuilder.Select(
                PostgresSqlExpression.Column(SourceAlias, projection[index].ColumnName),
                $"_field{index.ToString(CultureInfo.InvariantCulture)}");
        }
        candidateBuilder
            .Select(
                PostgresSqlExpression.Column(SourceAlias, relationship.ColumnName),
                RelationshipAlias)
            .Where(PostgresSqlExpression.Binary(
                PostgresSqlBinaryOperator.Equal,
                relationshipExpression,
                requestedKeyExpression));
        if (partition is not null)
        {
            var partitionExpression = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
                PostgresSqlExpression.Column(SourceAlias, partition.Binding.ColumnName),
                partition.Binding.ScalarType,
                partition.Binding.TextSemantics);
            candidateBuilder.Where(PostgresSqlExpression.Binary(
                PostgresSqlBinaryOperator.Equal,
                partitionExpression,
                PostgresSqlExpression.RuntimeParameter(PartitionBinding)));
        }
        if (afterIdentity is not null)
        {
            candidateBuilder.Where(PostgresSqlExpression.KeysetAfter(
            [
                new(
                    identityExpression,
                    PostgresSqlExpression.RuntimeParameter(AfterBinding),
                    PostgresSqlSortDirection.Ascending,
                    PostgresSqlNullPlacement.Last)
            ]));
        }
        var candidates = candidateBuilder
            .OrderBy(identityExpression)
            .Limit(probeLimit)
            .BuildQuery();

        var outerIdentity = PostgresRelationQueryScalarCatalog.ApplyTextCollation(
            PostgresSqlExpression.Column(CandidateAlias, IdentityAlias),
            identity.ScalarType,
            identity.TextSemantics);
        var builder = new PostgresSqlSelectBuilder(KeysBinding, RequestedAlias, RequestedKeyAlias)
            .Select(PostgresSqlExpression.Column(CandidateAlias, IdentityAlias), IdentityAlias);
        for (var index = 0; index < projection.Length; index++)
        {
            builder.Select(
                PostgresSqlExpression.Column(
                    CandidateAlias,
                    $"_field{index.ToString(CultureInfo.InvariantCulture)}"),
                $"_field{index.ToString(CultureInfo.InvariantCulture)}");
        }
        var template = builder
            .Select(PostgresSqlExpression.Column(CandidateAlias, RelationshipAlias), RelationshipAlias)
            .CrossJoinLateral(candidates, CandidateAlias)
            .OrderBy(outerIdentity)
            .Limit(probeLimit)
            .BuildTemplate();
        var parameters = ImmutableArray.CreateBuilder<PostgresNpgsqlParameter>(template.Parameters.Length);
        foreach (var parameter in template.Parameters)
        {
            parameters.Add(parameter.Binding switch
            {
                KeysBinding => new(
                    PostgresRelationQueryScalarCatalog.CreateArray(parsedKeys, relationship.ScalarType),
                    relationship.ScalarType,
                    IsArray: true),
                AfterBinding => new(afterIdentity!, identity.ScalarType, IsArray: false),
                PartitionBinding when partition is not null => new(
                    partition.Value,
                    partition.Binding.ScalarType,
                    IsArray: false),
                _ => throw new InvalidOperationException(
                    $"Unexpected PostgreSQL relationship-source parameter '{parameter.Binding ?? "<constant>"}'.")
            });
        }

        var resultTypes = ImmutableArray.CreateBuilder<PostgresRelationQueryScalarType>(2 + projection.Length);
        resultTypes.Add(identity.ScalarType);
        foreach (var item in projection)
        {
            resultTypes.Add(item.ScalarType);
        }

        resultTypes.Add(relationship.ScalarType);
        return new(
            template.Text,
            parameters.MoveToImmutable(),
            resultTypes.MoveToImmutable(),
            maximumResultBytes);
    }

    PostgresRelationQueryReadWindow Materialize(
        RelationQuerySourceReadRequest request,
        PostgresRelationQueryTableBinding table,
        ImmutableArray<PostgresRelationQueryProjectionBinding> projection,
        PostgresRelationQueryRelationshipReferenceBinding? relationship,
        ImmutableArray<ImmutableArray<object?>> rows,
        int maximumRows,
        bool resumed,
        ImmutableArray<PostgresRelationQueryFanOutCount> priorFanOut,
        CancellationToken cancellationToken)
    {
        var selectedCount = Math.Min(maximumRows, rows.Length);
        var observations = ImmutableArray.CreateBuilder<RelationQuerySourceReadObservation>(selectedCount);
        HashSet<string> identities = new(StringComparer.Ordinal);
        var identityBinding = table.Identity!;
        HashSet<string>? requestedIdentities = request.Constraint is RelationQueryIdentityBatchLookup identityLookup
            ? CanonicalKeys(identityLookup.Identities, identityBinding.ScalarType)
            : null;
        HashSet<string>? relationshipKeys = request.Constraint is RelationQueryRelationshipKeyBatchLookup relationshipLookup
            ? CanonicalKeys(relationshipLookup.Keys, relationship!.ScalarType)
            : null;
        IReadOnlyDictionary<string, long>? priorFanOutByKey = relationship is null
            ? null
            : priorFanOut.ToDictionary(static item => item.Key, static item => item.EmittedRows, StringComparer.Ordinal);
        Dictionary<string, long>? windowFanOut = relationship is null
            ? null
            : new(StringComparer.Ordinal);
        var correlationKeys = ImmutableArray.CreateBuilder<string>(relationship is null ? 0 : selectedCount);

        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[rowIndex];
            if (row.Length != 1 + projection.Length + (relationship is null ? 0 : 1)
                || row[0] is not { } rawIdentity)
            {
                return FailedWindow(request, "projected-row-shape-invalid");
            }
            string identity;
            try
            {
                identity = PostgresRelationQueryScalarCatalog.FormatKey(rawIdentity, identityBinding.ScalarType);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return FailedWindow(request, "observation-identity-invalid");
            }
            if (string.IsNullOrWhiteSpace(identity) || !identities.Add(identity))
            {
                return FailedWindow(request, "duplicate-or-empty-observation-identity");
            }

            if (requestedIdentities is not null && !requestedIdentities.Contains(identity))
            {
                return FailedWindow(request, "identity-query-returned-unrequested-row");
            }

            if (relationship is not null)
            {
                var rawReference = row[projection.Length + 1];
                if (rawReference is null)
                {
                    return FailedWindow(request, "relationship-query-returned-null-reference");
                }

                var reference = PostgresRelationQueryScalarCatalog.FormatKey(rawReference, relationship.ScalarType);
                if (!relationshipKeys!.Contains(reference))
                {
                    return FailedWindow(request, "relationship-query-returned-unrequested-row");
                }

                var previouslyEmitted = priorFanOutByKey!.GetValueOrDefault(reference);
                var observedInWindow = windowFanOut!.GetValueOrDefault(reference);
                if (previouslyEmitted >= source.Limits.MaximumFanOut
                    || observedInWindow >= source.Limits.MaximumFanOut - previouslyEmitted)
                {
                    return InconclusiveWindow(request, "relationship-fan-out-boundary-exceeded");
                }
                windowFanOut![reference] = observedInWindow + 1;
                if (rowIndex < selectedCount)
                {
                    correlationKeys.Add(reference);
                }
            }

            if (rowIndex >= selectedCount)
            {
                continue;
            }

            var fields = ImmutableArray.CreateBuilder<RelationQuerySourceReadFieldResult>(projection.Length);
            for (var fieldIndex = 0; fieldIndex < projection.Length; fieldIndex++)
            {
                fields.Add(ProjectField(
                    request,
                    projection[fieldIndex],
                    row[fieldIndex + 1]));
            }
            observations.Add(new(identity, request.Shape, fields.MoveToImmutable()));
        }

        var hasMore = rows.Length > maximumRows;
        var selected = observations.MoveToImmutable();
        var state = hasMore
            ? RelationQuerySourceReadState.Partial
            : selected.IsDefaultOrEmpty && !resumed
                ? RelationQuerySourceReadState.NotFound
                : RelationQuerySourceReadState.Complete;
        return new(
            new RelationQuerySourceReadResult(
                state,
                selected,
                Evidence(request, hasMore ? "read-partial" : state == RelationQuerySourceReadState.NotFound
                    ? "read-not-found"
                    : "read-complete")),
            hasMore,
            correlationKeys.MoveToImmutable());
    }

    PostgresRelationQueryReadWindow MaterializeOwnedCollection(
        RelationQuerySourceReadRequest request,
        PostgresRelationQueryTableBinding table,
        ImmutableArray<PostgresRelationQueryProjectionBinding> projection,
        PostgresRelationQueryOwnedCollectionProjectionBinding ownedProjection,
        ImmutableArray<ImmutableArray<object?>> rows,
        int maximumRows,
        bool resumed,
        CancellationToken cancellationToken)
    {
        var owned = ownedProjection.Binding;
        var expectedRowLength = 1 + projection.Length + owned.Fields.Length;
        var localIdentityIndex = owned.Fields.IndexOf(owned.ResolveField(owned.LocalIdentityPath));
        if (localIdentityIndex < 0)
        {
            return FailedWindow(request, "owned-collection-identity-binding-missing");
        }

        var identityBinding = table.Identity!;
        HashSet<string>? requestedIdentities = request.Constraint is RelationQueryIdentityBatchLookup identityLookup
            ? CanonicalKeys(identityLookup.Identities, identityBinding.ScalarType)
            : null;
        HashSet<string> observedRoots = new(StringComparer.Ordinal);
        List<PostgresRelationQueryOwnedCollectionRootAccumulator> roots = [];
        PostgresRelationQueryOwnedCollectionRootAccumulator? current = null;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Length != expectedRowLength || row[0] is not { } rawRootIdentity)
            {
                return FailedWindow(request, "owned-collection-row-shape-invalid");
            }

            string rootIdentity;
            try
            {
                rootIdentity = PostgresRelationQueryScalarCatalog.FormatKey(
                    rawRootIdentity,
                    identityBinding.ScalarType);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return FailedWindow(request, "observation-identity-invalid");
            }

            if (string.IsNullOrWhiteSpace(rootIdentity))
            {
                return FailedWindow(request, "duplicate-or-empty-observation-identity");
            }
            if (requestedIdentities is not null && !requestedIdentities.Contains(rootIdentity))
            {
                return FailedWindow(request, "identity-query-returned-unrequested-row");
            }

            if (current is null || !string.Equals(current.Identity, rootIdentity, StringComparison.Ordinal))
            {
                if (!observedRoots.Add(rootIdentity))
                {
                    return FailedWindow(request, "owned-collection-root-order-invalid");
                }

                current = new(rootIdentity, row);
                roots.Add(current);
            }

            var componentOffset = 1 + projection.Length;
            var rawLocalIdentity = row[componentOffset + localIdentityIndex];
            if (rawLocalIdentity is null)
            {
                var hasComponentValue = false;
                for (var index = componentOffset; index < row.Length; index++)
                {
                    if (row[index] is not null)
                    {
                        hasComponentValue = true;
                        break;
                    }
                }
                if (hasComponentValue)
                {
                    return FailedWindow(request, "owned-collection-null-identity-invalid");
                }
                continue;
            }

            if (current.Components.Count >= source.Limits.MaximumFanOut)
            {
                return InconclusiveWindow(request, "owned-collection-fan-out-boundary-exceeded");
            }

            string localIdentity;
            try
            {
                localIdentity = PostgresRelationQueryScalarCatalog.FormatKey(
                    rawLocalIdentity,
                    owned.Fields[localIdentityIndex].ScalarType);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return FailedWindow(request, "owned-collection-identity-invalid");
            }
            if (string.IsNullOrWhiteSpace(localIdentity)
                || !current.ComponentIdentities.Add(localIdentity))
            {
                return FailedWindow(request, "owned-collection-identity-duplicate");
            }

            Dictionary<string, ObservationValue> component = new(
                capacity: owned.Fields.Length,
                comparer: StringComparer.Ordinal);
            for (var fieldIndex = 0; fieldIndex < owned.Fields.Length; fieldIndex++)
            {
                var binding = owned.Fields[fieldIndex];
                if (!TryProjectOwnedValue(
                        binding,
                        row[componentOffset + fieldIndex],
                        out var value))
                {
                    return FailedWindow(request, "owned-collection-value-invalid");
                }

                component.Add(binding.SemanticPath.Segments[0].Segment!, value);
            }
            current.Components.Add(ObservationValue.FromObject(component));
        }

        var hasMore = roots.Count > maximumRows;
        var selectedCount = Math.Min(maximumRows, roots.Count);
        var observations = ImmutableArray.CreateBuilder<RelationQuerySourceReadObservation>(selectedCount);
        for (var rootIndex = 0; rootIndex < selectedCount; rootIndex++)
        {
            var root = roots[rootIndex];
            var fields = ImmutableArray.CreateBuilder<RelationQuerySourceReadFieldResult>(request.Fields.Length);
            foreach (var requested in request.Fields)
            {
                if (requested == ownedProjection.Field)
                {
                    fields.Add(new(
                        requested,
                        RelationQuerySourceReadFieldState.Value,
                        ObservationValue.FromImmutableArray([.. root.Components]),
                        Evidence(
                            request,
                            $"field/{Uri.EscapeDataString(requested.SemanticPath.ToString())}")));
                    continue;
                }

                var projectionIndex = -1;
                for (var index = 0; index < projection.Length; index++)
                {
                    if (projection[index].Field == requested)
                    {
                        projectionIndex = index;
                        break;
                    }
                }
                if (projectionIndex < 0)
                {
                    return FailedWindow(request, "owned-collection-projection-mismatch");
                }
                fields.Add(ProjectField(
                    request,
                    projection[projectionIndex],
                    root.RootRow[projectionIndex + 1]));
            }
            observations.Add(new(
                root.Identity,
                request.Shape,
                fields.MoveToImmutable()));
        }

        var selected = observations.MoveToImmutable();
        var state = hasMore
            ? RelationQuerySourceReadState.Partial
            : selected.IsDefaultOrEmpty && !resumed
                ? RelationQuerySourceReadState.NotFound
                : RelationQuerySourceReadState.Complete;
        return new(
            new RelationQuerySourceReadResult(
                state,
                selected,
                Evidence(
                    request,
                    hasMore
                        ? "owned-collection-read-partial"
                        : state == RelationQuerySourceReadState.NotFound
                            ? "owned-collection-read-not-found"
                            : "owned-collection-read-complete")),
            HasMore: hasMore,
            CorrelationKeys: []);
    }

    static bool TryProjectOwnedValue(
        PostgresRelationQueryOwnedCollectionElementFieldBinding binding,
        object? raw,
        out ObservationValue value)
    {
        if (raw is null)
        {
            if (binding.MissingValueEncoding == PostgresRelationQueryMissingValueEncoding.SqlNull)
            {
                value = ObservationValue.Undefined;
                return true;
            }
            if (binding.NullValueEncoding == PostgresRelationQueryNullValueEncoding.SqlNull)
            {
                value = ObservationValue.Null;
                return true;
            }

            value = default;
            return false;
        }

        try
        {
            value = PostgresRelationQueryScalarCatalog.ToObservationValue(raw, binding.ScalarType);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            value = default;
            return false;
        }
    }

    RelationQuerySourceReadFieldResult ProjectField(
        RelationQuerySourceReadRequest request,
        PostgresRelationQueryProjectionBinding binding,
        object? value)
    {
        var evidence = Evidence(
            request,
            $"field/{Uri.EscapeDataString(binding.Field.SemanticPath.ToString())}");
        if (value is null)
        {
            if (binding.MissingValueEncoding == PostgresRelationQueryMissingValueEncoding.SqlNull)
            {
                return new(binding.Field, RelationQuerySourceReadFieldState.Missing, evidenceReference: evidence);
            }

            if (binding.NullValueEncoding == PostgresRelationQueryNullValueEncoding.SqlNull)
            {
                return new(binding.Field, RelationQuerySourceReadFieldState.Null, evidenceReference: evidence);
            }

            return new(binding.Field, RelationQuerySourceReadFieldState.Failed, evidenceReference: evidence);
        }

        try
        {
            return new(
                binding.Field,
                RelationQuerySourceReadFieldState.Value,
                PostgresRelationQueryScalarCatalog.ToObservationValue(value, binding.ScalarType),
                evidence);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new(binding.Field, RelationQuerySourceReadFieldState.Failed, evidenceReference: evidence);
        }
    }

    bool BatchBoundaryExceeded(
        RelationQuerySourceReadConstraint constraint,
        RelationQueryPhysicalStage? stage)
    {
        var maximumBatchSize = Math.Min(source.Limits.MaximumBatchSize, Policy.MaximumBatchKeys);
        if (stage?.BatchSize is { } compiledBatchSize)
        {
            maximumBatchSize = Math.Min(maximumBatchSize, compiledBatchSize);
        }

        return constraint switch
        {
            RelationQueryIdentityBatchLookup identity => identity.Identities.Length > maximumBatchSize,
            RelationQueryRelationshipKeyBatchLookup relationship => relationship.Keys.Length > maximumBatchSize,
            RelationQueryCollectionElementKeyBatchLookup collection => collection.Keys.Length > maximumBatchSize,
            _ => false
        };
    }

    static bool HasExactRequestedFields(
        ImmutableArray<RelationQuerySourceReadField> fields,
        ImmutableArray<RelationQueryInputId> requestedFields)
    {
        var requestedIndex = 0;
        foreach (var field in fields)
        {
            if (field.Input is not { } input)
            {
                continue;
            }

            if (requestedIndex >= requestedFields.Length || input != requestedFields[requestedIndex])
            {
                return false;
            }

            requestedIndex++;
        }
        return requestedIndex == requestedFields.Length;
    }

    static ImmutableArray<object> ParseKeys(
        ImmutableArray<string> keys,
        PostgresRelationQueryScalarType scalarType)
    {
        var parsed = ImmutableArray.CreateBuilder<object>(keys.Length);
        HashSet<string> canonical = new(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var value = PostgresRelationQueryScalarCatalog.ParseKey(key, scalarType);
            var formatted = PostgresRelationQueryScalarCatalog.FormatKey(value, scalarType);
            if (!string.Equals(formatted, key, StringComparison.Ordinal) || !canonical.Add(formatted))
            {
                throw new ArgumentException(
                    "PostgreSQL source keys must use their exact canonical scalar encoding.",
                    nameof(keys));
            }
            parsed.Add(value);
        }
        return parsed.MoveToImmutable();
    }

    static string? ValidateCanonicalKeys(
        ImmutableArray<string> keys,
        PostgresRelationQueryScalarType scalarType,
        int maximumKeyBytes)
    {
        foreach (var key in keys)
        {
            int byteCount;
            try
            {
                byteCount = PostgresSqlUtf8.GetByteCount(key, nameof(keys));
            }
            catch (ArgumentException)
            {
                return "key-encoding-noncanonical";
            }
            if (byteCount > maximumKeyBytes)
            {
                return "key-boundary-exceeded";
            }
        }

        try
        {
            _ = ParseKeys(keys, scalarType);
            return null;
        }
        catch (ArgumentException)
        {
            return "key-encoding-noncanonical";
        }
    }

    static HashSet<string> CanonicalKeys(
        ImmutableArray<string> keys,
        PostgresRelationQueryScalarType scalarType)
    {
        HashSet<string> canonical = new(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var parsed = PostgresRelationQueryScalarCatalog.ParseKey(key, scalarType);
            canonical.Add(PostgresRelationQueryScalarCatalog.FormatKey(parsed, scalarType));
        }
        return canonical;
    }

    PostgresRelationQueryReadWindow FailedWindow(
        RelationQuerySourceReadRequest request,
        string reason) => new(
        new(
            RelationQuerySourceReadState.Failed,
            evidenceReference: Evidence(request, reason)),
        HasMore: false,
        CorrelationKeys: []);

    PostgresRelationQueryReadWindow InconclusiveWindow(
        RelationQuerySourceReadRequest request,
        string reason) => new(
        new(
            RelationQuerySourceReadState.Inconclusive,
            evidenceReference: Evidence(request, reason)),
        HasMore: false,
        CorrelationKeys: []);

    string Evidence(RelationQuerySourceReadRequest request, string reason) => string.Concat(
        EvidencePrefix,
        "/database/",
        Uri.EscapeDataString(storage.Database.Value),
        "/binding/sha256/",
        storage.Fingerprint.Value,
        runtimeBinding is null ? "/runtime/unattested-internal-executor" : "/runtime/attested",
        runtimeBinding is null ? string.Empty : "/authority/",
        runtimeBinding is null ? string.Empty : Uri.EscapeDataString(runtimeBinding.Authority),
        runtimeBinding is null ? string.Empty : "/data-source/",
        runtimeBinding is null ? string.Empty : Uri.EscapeDataString(runtimeBinding.DataSourceFingerprint.Algorithm),
        runtimeBinding is null ? string.Empty : "/",
        runtimeBinding is null ? string.Empty : Uri.EscapeDataString(runtimeBinding.DataSourceFingerprint.Canonicalization),
        runtimeBinding is null ? string.Empty : "/",
        runtimeBinding is null ? string.Empty : runtimeBinding.DataSourceFingerprint.Value,
        "/source/",
        Uri.EscapeDataString(source.Id.Value),
        "/policy/batch/",
        Policy.MaximumBatchKeys.ToString(CultureInfo.InvariantCulture),
        "/rows/",
        Policy.MaximumRowsPerRead.ToString(CultureInfo.InvariantCulture),
        "/page-items/",
        Policy.MaximumPageItems.ToString(CultureInfo.InvariantCulture),
        "/page-bytes/",
        Policy.MaximumPageBytes.ToString(CultureInfo.InvariantCulture),
        "/key-bytes/",
        Policy.MaximumKeyBytes.ToString(CultureInfo.InvariantCulture),
        "/temporal/",
        ((int)Policy.TemporalSemantics).ToString(CultureInfo.InvariantCulture),
        "/physical-plan/",
        Uri.EscapeDataString(request.PhysicalPlan.Algorithm),
        "/",
        Uri.EscapeDataString(request.PhysicalPlan.Canonicalization),
        "/",
        Uri.EscapeDataString(request.PhysicalPlan.Value),
        "/stage/",
        Uri.EscapeDataString(request.Stage.Value),
        "/placement/",
        Uri.EscapeDataString(request.PlacementBinding.Value),
        "/",
        reason);

    static PostgresNpgsqlCommandExecutor RequireSingleHostDataSource(
        NpgsqlDataSource dataSource,
        PostgresNpgsqlRuntimeBinding runtimeBinding,
        PostgresRelationQueryDatabaseId database)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(runtimeBinding);
        if (dataSource is NpgsqlMultiHostDataSource)
        {
            throw new ArgumentException(
                "Durable PostgreSQL source reads require a single-host data source; multi-host replica selection is not an attributable consistency boundary.",
                nameof(dataSource));
        }
        if (runtimeBinding.Database != database)
        {
            throw new ArgumentException(
                $"The Npgsql runtime binding attests database '{runtimeBinding.Database.Value}', not storage database '{database.Value}'.",
                nameof(runtimeBinding));
        }
        if (!runtimeBinding.Matches(dataSource))
        {
            throw new ArgumentException(
                "The supplied Npgsql data source is not the exact single-host instance covered by the runtime binding.",
                nameof(dataSource));
        }
        return (command, cancellationToken) => PostgresNpgsqlExecution.ExecuteAsync(
            dataSource,
            command,
            cancellationToken);
    }

    sealed record Registration(
        CompiledRelationQueryPlan Plan,
        CompiledRelationQueryPhysicalPlan PhysicalPlan,
        RelationQuerySourceInstance Source);
}

internal sealed record ResolvedPartition(
    PostgresRelationQueryPartitionBinding Binding,
    object Value,
    string ScopeDigest);

internal sealed record PostgresRelationQueryProjectionBinding(
    RelationQuerySourceReadField Field,
    string ColumnName,
    PostgresRelationQueryScalarType ScalarType,
    PostgresRelationQueryMissingValueEncoding MissingValueEncoding,
    PostgresRelationQueryNullValueEncoding NullValueEncoding,
    PostgresRelationQueryTextSemantics? TextSemantics);

internal sealed record PostgresRelationQueryOwnedCollectionProjectionBinding(
    RelationQuerySourceReadField Field,
    PostgresRelationQueryOwnedCollectionBinding Binding);

internal sealed class PostgresRelationQueryOwnedCollectionRootAccumulator(
    string identity,
    ImmutableArray<object?> rootRow)
{
    public string Identity { get; } = identity;

    public ImmutableArray<object?> RootRow { get; } = rootRow;

    public List<ObservationValue> Components { get; } = [];

    public HashSet<string> ComponentIdentities { get; } = new(StringComparer.Ordinal);
}

internal sealed record PostgresRelationQueryReadWindow(
    RelationQuerySourceReadResult Read,
    bool HasMore,
    ImmutableArray<string> CorrelationKeys);

internal sealed record PostgresRelationQueryFanOutCount(
    string Key,
    long EmittedRows);
