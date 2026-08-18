using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.Adapters.Postgres;

/// <summary>Durable keyset-paged materialization source over one exact PostgreSQL table placement.</summary>
/// <remarks>
/// Every page executes in one PostgreSQL statement snapshot. A durable continuation starts a later statement and
/// therefore does not retain an MVCC snapshot across pages or pause/resume. The capability profile advertises stable
/// ordering, request-local completeness, and bounded reconciliation; it intentionally omits coordinated snapshots,
/// change delivery, and settlement. The wrapped reader borrows its caller-owned Npgsql data source.
/// </remarks>
public sealed class PostgresMaterializationSource : IMaterializationSource
{
    /// <summary>Stable diagnostic code for a failed Npgsql-backed source read.</summary>
    public const string SourceReadFailedDiagnosticCode =
        "cohesive.adapters.postgres.materialization.sourceReadFailed";

    /// <summary>Stable diagnostic code for an inconclusive Npgsql-backed source read.</summary>
    public const string SourceReadInconclusiveDiagnosticCode =
        "cohesive.adapters.postgres.materialization.sourceReadInconclusive";

    /// <summary>Stable diagnostic code for a read that exhausted its declared Relations boundary before its table scope.</summary>
    public const string ReadBoundaryReachedDiagnosticCode =
        "cohesive.adapters.postgres.materialization.readBoundaryReached";

    const int ContinuationFormatVersion = 2;
    const string ContinuationPrefix = "postgres-keyset/v2/";
    const int MaximumContinuationValueCharacters = 4 * 1024 * 1024;
    const string EvidencePrefix = "cohesive.adapters.postgres/materialization-source/v2";
    static ReadOnlySpan<byte> ContinuationAuthenticationDomain =>
        "cohesive.adapters.postgres/materialization-continuation/v2\0"u8;
    static readonly JsonSerializerOptions CanonicalJsonOptions = MaterializationJsonSerializer.CreateOptions();
    readonly PostgresRelationQuerySourceReader reader;
    readonly PostgresRelationQueryTableBinding table;
    readonly MaterializationAuthenticatedValueCodec continuationCodec;

    /// <summary>Creates a materialization source for one exact canonical source-read placement.</summary>
    /// <param name="reader">Npgsql-backed canonical Relations reader.</param>
    /// <param name="placement">Exact canonical source placement represented by the bound PostgreSQL table.</param>
    /// <param name="continuationAuthenticationKey">
    /// Caller-owned secret key used to authenticate durable continuations. The source copies the key; callers must
    /// provide the same secret after restart while continuations remain resumable and must rotate it deliberately.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> or <paramref name="placement"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Placement affinity conflicts, the table lacks a unique identity, or its identity cannot prove canonical
    /// durable keyset order. Initial v2 keyset support is exact ordinal text and UUID identity, or
    /// <paramref name="continuationAuthenticationKey"/> contains fewer than 32 bytes, or the serialized source profile,
    /// maximum key size, and batch policy would permit a continuation larger than the versioned format bound.
    /// </exception>
    public PostgresMaterializationSource(
        PostgresRelationQuerySourceReader reader,
        RelationQuerySourcePlacementBinding placement,
        ReadOnlySpan<byte> continuationAuthenticationKey)
    {
        this.reader = Guard.RequireNotNull(reader);
        placement = Guard.RequireNotNull(placement);
        if (continuationAuthenticationKey.Length < MaterializationAuthenticatedValueCodec.MinimumAuthenticationKeyBytes)
        {
            throw new ArgumentException(
                $"PostgreSQL continuation authentication requires at least {MaterializationAuthenticatedValueCodec.MinimumAuthenticationKeyBytes} secret bytes.",
                nameof(continuationAuthenticationKey));
        }
        continuationCodec = new(
            ContinuationPrefix,
            ContinuationAuthenticationDomain,
            continuationAuthenticationKey,
            MaximumContinuationValueCharacters);
        RelationQuerySourcePlacementBinding authorized;
        try
        {
            authorized = reader.ResolvePlacement(placement.Id);
        }
        catch (KeyNotFoundException exception)
        {
            throw new ArgumentException(
                "The PostgreSQL reader does not authorize the requested materialization placement.",
                nameof(placement),
                exception);
        }
        if (placement.Source != reader.Descriptor.Source || !authorized.Equals(placement))
        {
            throw new ArgumentException(
                "A PostgreSQL materialization placement must belong to the wrapped reader source.",
                nameof(placement));
        }
        if (!reader.AuthorizesStage(placement.Id, RelationQueryPhysicalStageKind.SourceRead)
            && !reader.AuthorizesStage(placement.Id, RelationQueryPhysicalStageKind.BatchedIdentityLookup)
            && !reader.AuthorizesStage(placement.Id, RelationQueryPhysicalStageKind.BatchedPredicateLookup))
        {
            throw new ArgumentException(
                "The wrapped PostgreSQL reader has no executable source-read stage for the materialization placement.",
                nameof(placement));
        }
        try
        {
            table = reader.StorageBinding.ResolveTable(placement.Id);
        }
        catch (KeyNotFoundException exception)
        {
            throw new ArgumentException(
                "The PostgreSQL storage binding does not contain the requested materialization placement.",
                nameof(placement),
                exception);
        }
        if (table.Source != placement.Source || table.Shape != placement.Shape
            || table.Identity is not { } identity
            || placement.Identity is not { } canonicalIdentity
            || canonicalIdentity.Shape != placement.Shape)
        {
            throw new ArgumentException(
                "The canonical placement and PostgreSQL table disagree on source, shape, or identity.",
                nameof(placement));
        }
        if (!PostgresRelationQueryScalarCatalog.SupportsDurableKeyset(identity))
        {
            throw new ArgumentException(
                "PostgreSQL durable paging requires a UUID identity or exact ordinal text ordering evidence.",
                nameof(placement));
        }
        var partition = reader.ResolvePartition(placement.Id);
        var partitionIdentity = partition is null
            ? "unpartitioned"
            : string.Concat(
                "selector/", Uri.EscapeDataString(partition.Binding.SourceSelector),
                "/logical-scope/sha256/", partition.ScopeDigest);
        Scope = new(
            reader.PhysicalPlan,
            placement,
            new MaterializationSourcePartitionId(
                $"postgres/table/{reader.StorageBinding.Fingerprint.Value}/{Uri.EscapeDataString(placement.Id.Value)}/partition/{partitionIdentity}"),
            new MaterializationOrderingScopeId(
                $"postgres/identity/{(int)identity.ScalarType}/{Uri.EscapeDataString(identity.ColumnName)}/ascending/v1"));
        Descriptor = new(reader, CreateCapabilityProfile(reader, placement, table, partition));
        try
        {
            if (ComputeMaximumContinuationValueCharacters() > MaximumContinuationValueCharacters)
            {
                throw new ArgumentException(
                    "The PostgreSQL source identities, profile, key policy, and batch policy cannot guarantee a bounded portable continuation.",
                    nameof(reader));
            }
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException(
                "The PostgreSQL source continuation bound is outside the supported portable range.",
                nameof(reader),
                exception);
        }
    }

    /// <inheritdoc />
    public MaterializationQuerySourceDescriptor Descriptor { get; }

    /// <summary>Exact source-read placement, partition, and ordering scope accepted by this materialization source.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <inheritdoc />
    public async ValueTask<MaterializationSourcePage> ReadPageAsync(
        OperationContext context,
        MaterializationSourcePageRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.CancellationToken.ThrowIfCancellationRequested();
        if (request.Scope != Scope)
        {
            throw new ArgumentException(
                "The request targets a different PostgreSQL table, partition, or ordering scope.",
                nameof(request));
        }
        MaterializationSourceAcquisitionCatalog.RequireCompatibleRead(request.Read, request.Scope);
        RequireBoundedConstraint(request.Read.Constraint, nameof(request));
        var capability = MaterializationSourceAcquisitionCatalog.GetReadCapability(request.Read.Constraint);
        MaterializationCapabilityLimits.RequireSupportedBounds(
            Descriptor.CapabilityProfile,
            capability,
            MaterializationLimitKind.ReadItems,
            request.MaximumItems,
            MaterializationLimitKind.ReadBytes,
            request.MaximumBytes,
            nameof(request));

        var readFingerprint = MaterializationSourceReadFingerprinter.Compute(request.Read);
        var cursor = DecodeContinuation(request.Continuation, request.Read, nameof(request));
        var readBoundary = reader.EffectiveReadBoundary(request.Read);
        if (cursor.EmittedRows >= readBoundary)
        {
            throw new ArgumentException(
                "The PostgreSQL continuation has already reached the wrapped Relations read boundary.",
                nameof(request));
        }
        var remaining = readBoundary - cursor.EmittedRows;
        var maximumRows = Math.Min(request.MaximumItems, remaining);
        var window = await reader.ReadWindowAsync(
            request.Read,
            cursor.AfterIdentity,
            maximumRows,
            cursor.FanOut,
            context.CancellationToken).ConfigureAwait(false);

        if (window.Read.State is RelationQuerySourceReadState.Failed
            or RelationQuerySourceReadState.Inconclusive
            or RelationQuerySourceReadState.NotFound)
        {
            return new(
                Scope,
                readFingerprint,
                window.Read,
                MaterializationSourcePageState.Exhausted,
                diagnostics: Diagnostics(window.Read));
        }

        var available = window.Read.Observations;
        var selected = ImmutableArray.CreateBuilder<RelationQuerySourceReadObservation>(available.Length);
        long encodedBytes = 0;
        foreach (var observation in available)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var observationBytes = CanonicalByteCount(observation);
            if (observationBytes > request.MaximumBytes)
            {
                if (selected.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Observation '{observation.Identity}' requires {observationBytes} canonical bytes, which exceeds the indivisible item bound of {request.MaximumBytes} bytes.");
                }
                break;
            }
            if (observationBytes > request.MaximumBytes - encodedBytes)
                break;
            selected.Add(observation);
            encodedBytes += observationBytes;
        }

        var pageObservations = selected.Count == selected.Capacity
            ? selected.MoveToImmutable()
            : selected.ToImmutable();
        var emittedRows = checked(cursor.EmittedRows + pageObservations.Length);
        var nextFanOut = window.CorrelationKeys.IsDefaultOrEmpty
            ? cursor.FanOut
            : MergeFanOut(
                cursor.FanOut,
                window.CorrelationKeys.AsSpan(0, pageObservations.Length));
        var byteBoundaryStopped = pageObservations.Length < available.Length;
        var sourceHasMore = window.HasMore || byteBoundaryStopped;
        var readBoundaryReached = sourceHasMore && emittedRows >= readBoundary;
        var hasMore = sourceHasMore && !readBoundaryReached;
        var pageState = hasMore
            ? RelationQuerySourceReadState.Partial
            : readBoundaryReached
                ? RelationQuerySourceReadState.Partial
                : window.Read.State;
        var pageRead = new RelationQuerySourceReadResult(
            pageState,
            pageObservations,
            window.Read.EvidenceReference);
        var continuation = hasMore
            ? CreateContinuation()
            : null;
        context.CancellationToken.ThrowIfCancellationRequested();
        return new(
            Scope,
            readFingerprint,
            pageRead,
            hasMore ? MaterializationSourcePageState.MoreAvailable : MaterializationSourcePageState.Exhausted,
            continuation,
            readBoundaryReached ? BoundaryDiagnostics(pageRead) : []);

        MaterializationSourceContinuation CreateContinuation()
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return new(
                ContinuationFormatVersion,
                readFingerprint,
                Scope,
                EncodeContinuation(
                    readFingerprint,
                    pageObservations[^1].Identity,
                    emittedRows,
                    nextFanOut));
        }
    }

    static MaterializationCapabilityProfile CreateCapabilityProfile(
        PostgresRelationQuerySourceReader reader,
        RelationQuerySourcePlacementBinding placement,
        PostgresRelationQueryTableBinding table,
        ResolvedPartition? partition)
    {
        var policy = reader.Policy;
        var stages = reader.ResolveAuthorizedStages(placement.Id);
        var physicalPlanReference = string.Concat(
            "relations-physical-plan/",
            Uri.EscapeDataString(reader.PhysicalPlan.Algorithm), "/",
            Uri.EscapeDataString(reader.PhysicalPlan.Canonicalization), "/",
            Uri.EscapeDataString(reader.PhysicalPlan.Value));
        var sourceReferences = ImmutableArray.CreateBuilder<string>(
            5 + stages.Length + (reader.RuntimeEvidenceReference is null ? 0 : 1) + (partition is null ? 0 : 1));
        sourceReferences.Add(EvidencePrefix);
        sourceReferences.Add($"postgres-binding/sha256/{reader.StorageBinding.Fingerprint.Value}");
        if (reader.RuntimeEvidenceReference is { } runtimeEvidence)
            sourceReferences.Add(runtimeEvidence);
        sourceReferences.Add($"postgres-policy/batch/{policy.MaximumBatchKeys.ToString(CultureInfo.InvariantCulture)}/rows/{policy.MaximumRowsPerRead.ToString(CultureInfo.InvariantCulture)}/page-items/{policy.MaximumPageItems.ToString(CultureInfo.InvariantCulture)}/page-bytes/{policy.MaximumPageBytes.ToString(CultureInfo.InvariantCulture)}/key-bytes/{policy.MaximumKeyBytes.ToString(CultureInfo.InvariantCulture)}/temporal/{(int)policy.TemporalSemantics}");
        sourceReferences.Add($"relations-source-limits/batch/{reader.Limits.MaximumBatchSize.ToString(CultureInfo.InvariantCulture)}/rows/{reader.Limits.MaximumBufferedRows.ToString(CultureInfo.InvariantCulture)}/fan-out/{reader.Limits.MaximumFanOut.ToString(CultureInfo.InvariantCulture)}/parallelism/{reader.Limits.MaximumConcurrency.ToString(CultureInfo.InvariantCulture)}");
        sourceReferences.Add(physicalPlanReference);
        if (partition is not null)
        {
            sourceReferences.Add(string.Concat(
                "postgres-partition-selector/", Uri.EscapeDataString(partition.Binding.SourceSelector),
                "/column/", Uri.EscapeDataString(partition.Binding.ColumnName),
                "/logical-scope/sha256/", partition.ScopeDigest));
        }
        foreach (var stage in stages)
        {
            sourceReferences.Add(string.Concat(
                "relations-physical-stage/",
                Uri.EscapeDataString(stage.Id.Value),
                "/kind/",
                ((int)stage.Kind).ToString(CultureInfo.InvariantCulture)));
        }
        var normalizedSourceReferences = sourceReferences.MoveToImmutable();
        var guarantees = ImmutableArray.Create(
            MaterializationGuaranteeKind.StableOrdering,
            MaterializationGuaranteeKind.RequestLocalCompleteness,
            MaterializationGuaranteeKind.Reconciliation);
        var maximumPageItems = Math.Min(
            policy.MaximumPageItems,
            checked((int)reader.Limits.MaximumBufferedRows));
        var limits = ImmutableArray.Create(
            new MaterializationOperatingLimit(MaterializationLimitKind.ReadItems, maximumPageItems),
            new MaterializationOperatingLimit(MaterializationLimitKind.ReadBytes, policy.MaximumPageBytes),
            new MaterializationOperatingLimit(
                MaterializationLimitKind.Parallelism,
                reader.Limits.MaximumConcurrency));
        var capabilities = ImmutableArray.CreateBuilder<MaterializationCapabilityKind>(2);
        if (reader.AuthorizesStage(placement.Id, RelationQueryPhysicalStageKind.SourceRead))
        {
            capabilities.Add(MaterializationCapabilityKind.SourceBoundedEnumeration);
        }
        if (reader.AuthorizesStage(placement.Id, RelationQueryPhysicalStageKind.BatchedIdentityLookup))
        {
            capabilities.Add(MaterializationCapabilityKind.SourceBatchedPointRead);
        }
        if (reader.AuthorizesStage(placement.Id, RelationQueryPhysicalStageKind.BatchedPredicateLookup)
            && placement.RelationshipKeys.Any(key => table.RelationshipReferences.Any(reference =>
                reference.Input == key.Input
                && reference.SemanticPath == key.SemanticPath)))
        {
            capabilities.Add(MaterializationCapabilityKind.SourceParameterizedPredicateQuery);
        }
        var evidence = ImmutableArray.CreateBuilder<MaterializationCapabilityEvidence>(capabilities.Count + 1);
        foreach (var capability in capabilities)
        {
            evidence.Add(new(
                new($"cohesive.adapters.postgres/materialization/{(int)capability}/v1"),
                capability,
                CapabilityRealizationKind.Constrained,
                guarantees,
                limits,
                normalizedSourceReferences,
                "One set-oriented PostgreSQL statement with stable identity ordering and explicit item/byte bounds."));
        }
        evidence.Add(new(
            new("cohesive.adapters.postgres/materialization/continuation/v2"),
            MaterializationCapabilityKind.SourceContinuation,
            CapabilityRealizationKind.Constrained,
            [MaterializationGuaranteeKind.StableOrdering, MaterializationGuaranteeKind.Reconciliation],
            [new MaterializationOperatingLimit(MaterializationLimitKind.Parallelism, 1)],
            normalizedSourceReferences,
            "Authenticated, size-bounded exclusive portable keyset continuation; each resumed page uses a new statement snapshot."));
        return new(
            new(string.Concat(
                "cohesive.adapters.postgres/materialization-source/v2/",
                reader.StorageBinding.Fingerprint.Value,
                "/physical-plan/", Uri.EscapeDataString(reader.PhysicalPlan.Algorithm), "-",
                Uri.EscapeDataString(reader.PhysicalPlan.Canonicalization), "-",
                Uri.EscapeDataString(reader.PhysicalPlan.Value),
                "/stages/", stages.IsDefaultOrEmpty
                    ? "placement-derived"
                    : string.Join(",", stages.Select(static stage => Uri.EscapeDataString(stage.Id.Value))),
                "/placement/", Uri.EscapeDataString(placement.Id.Value),
                "/source/", Uri.EscapeDataString(reader.Descriptor.Source.Value),
                "/partition/", partition is null
                    ? "unpartitioned"
                    : string.Concat(
                        "selector-", Uri.EscapeDataString(partition.Binding.SourceSelector),
                        "-sha256-", partition.ScopeDigest),
                "/policy/", policy.MaximumBatchKeys.ToString(CultureInfo.InvariantCulture), "-",
                policy.MaximumRowsPerRead.ToString(CultureInfo.InvariantCulture), "-",
                policy.MaximumPageItems.ToString(CultureInfo.InvariantCulture), "-",
                policy.MaximumPageBytes.ToString(CultureInfo.InvariantCulture), "-",
                policy.MaximumKeyBytes.ToString(CultureInfo.InvariantCulture), "-",
                ((int)policy.TemporalSemantics).ToString(CultureInfo.InvariantCulture),
                "/limits/", reader.Limits.MaximumBatchSize.ToString(CultureInfo.InvariantCulture), "-",
                reader.Limits.MaximumBufferedRows.ToString(CultureInfo.InvariantCulture), "-",
                reader.Limits.MaximumFanOut.ToString(CultureInfo.InvariantCulture), "-",
                reader.Limits.MaximumConcurrency.ToString(CultureInfo.InvariantCulture),
                "/runtime/", Uri.EscapeDataString(
                    reader.RuntimeEvidenceReference ?? "unattested-internal-executor"))),
            MaterializationEndpointRole.Source,
            reader.Descriptor.Source.Value,
            evidence.MoveToImmutable(),
            "Npgsql-backed PostgreSQL rebuild/reconciliation source without a cross-page snapshot claim.");
    }

    Cursor DecodeContinuation(
        MaterializationSourceContinuation? continuation,
        RelationQuerySourceReadRequest read,
        string parameterName)
    {
        if (continuation is null)
            return new(AfterIdentity: null, EmittedRows: 0, FanOut: []);
        if (continuation.FormatVersion != ContinuationFormatVersion
            || continuation.Value.Length > MaximumContinuationValueCharacters)
        {
            throw new ArgumentException(
                "The PostgreSQL continuation version is unsupported or its encoded value exceeds the format bound.",
                parameterName);
        }

        var payloadBytes = continuationCodec.Decode(continuation.Value, parameterName, "PostgreSQL continuation");
        ContinuationPayload payload;
        try
        {
            ValidateContinuationPayloadBounds(payloadBytes, read.Constraint, reader.Policy.MaximumKeyBytes);
            payload = JsonSerializer.Deserialize<ContinuationPayload>(payloadBytes, CanonicalJsonOptions)
                ?? throw new JsonException("The PostgreSQL continuation payload is null.");
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The PostgreSQL continuation is malformed.", parameterName, exception);
        }
        if (!string.Equals(payload.BindingFingerprint, reader.StorageBinding.Fingerprint.Value, StringComparison.Ordinal)
            || !string.Equals(payload.Placement, table.PlacementBinding.Value, StringComparison.Ordinal)
            || !string.Equals(payload.SourceProfile, Descriptor.CapabilityProfile.Id.Value, StringComparison.Ordinal)
            || payload.ReadFingerprint != continuation.ReadFingerprint
            || payload.ScalarType != (int)table.Identity!.ScalarType
            || payload.EmittedRows <= 0
            || !IsCanonicalFanOut(payload.FanOut, read.Constraint, payload.EmittedRows))
        {
            throw new ArgumentException(
                "The PostgreSQL continuation conflicts with the read, binding, placement, scalar domain, or row progress.",
                parameterName);
        }
        object afterIdentity;
        try
        {
            afterIdentity = PostgresRelationQueryScalarCatalog.ParseKey(payload.Identity, table.Identity.ScalarType);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The PostgreSQL continuation identity is invalid.", parameterName, exception);
        }
        if (Encoding.UTF8.GetByteCount(payload.Identity) > reader.Policy.MaximumKeyBytes
            || !string.Equals(
                PostgresRelationQueryScalarCatalog.FormatKey(afterIdentity, table.Identity.ScalarType),
                payload.Identity,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The PostgreSQL continuation identity is not a bounded canonical key.",
                parameterName);
        }
        if (table.Identity is
            {
                ScalarType: PostgresRelationQueryScalarType.Text,
                TextSemantics.OrderingDomain: { } orderingDomain
            }
            && (afterIdentity is not string text || !orderingDomain.IsSatisfiedBy(text)))
        {
            throw new ArgumentException(
                "The PostgreSQL continuation identity violates its durable text ordering domain.",
                parameterName);
        }
        if (!string.Equals(
                EncodeContinuation(
                    continuation.ReadFingerprint,
                    payload.Identity,
                    payload.EmittedRows,
                    payload.FanOut),
                continuation.Value,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("The PostgreSQL continuation is not canonical.", parameterName);
        }
        return new(afterIdentity, payload.EmittedRows, payload.FanOut);
    }

    string EncodeContinuation(
        MaterializationSourceReadFingerprint readFingerprint,
        string identity,
        long emittedRows,
        ImmutableArray<PostgresRelationQueryFanOutCount> fanOut)
    {
        var payload = new ContinuationPayload(
            readFingerprint,
            reader.StorageBinding.Fingerprint.Value,
            table.PlacementBinding.Value,
            Descriptor.CapabilityProfile.Id.Value,
            (int)table.Identity!.ScalarType,
            checked((int)emittedRows),
            identity,
            fanOut);
        return continuationCodec.Encode(JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJsonOptions));
    }

    static ImmutableArray<PostgresRelationQueryFanOutCount> MergeFanOut(
        ImmutableArray<PostgresRelationQueryFanOutCount> prior,
        ReadOnlySpan<string> emittedKeys)
    {
        if (emittedKeys.IsEmpty)
            return prior;
        var counts = prior.ToDictionary(static item => item.Key, static item => item.EmittedRows, StringComparer.Ordinal);
        foreach (var key in emittedKeys)
            counts[key] = checked(counts.GetValueOrDefault(key) + 1);
        return
        [
            .. counts.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new PostgresRelationQueryFanOutCount(pair.Key, pair.Value))
        ];
    }

    bool IsCanonicalFanOut(
        ImmutableArray<PostgresRelationQueryFanOutCount> fanOut,
        RelationQuerySourceReadConstraint constraint,
        int emittedRows)
    {
        if (fanOut.IsDefault)
            return false;
        if (constraint is not RelationQueryRelationshipKeyBatchLookup lookup)
            return fanOut.IsEmpty;
        if (fanOut.IsDefaultOrEmpty || fanOut.Length > lookup.Keys.Length)
            return false;

        HashSet<string> requested = new(lookup.Keys, StringComparer.Ordinal);
        long total = 0;
        foreach (var item in fanOut)
        {
            if (item is null
                || string.IsNullOrEmpty(item.Key)
                || !requested.Contains(item.Key)
                || Encoding.UTF8.GetByteCount(item.Key) > reader.Policy.MaximumKeyBytes
                || item.EmittedRows <= 0
                || item.EmittedRows > reader.Limits.MaximumFanOut
                || item.EmittedRows > emittedRows - total)
            {
                return false;
            }
            total += item.EmittedRows;
        }
        if (total != emittedRows)
            return false;
        for (var index = 1; index < fanOut.Length; index++)
        {
            if (StringComparer.Ordinal.Compare(fanOut[index - 1].Key, fanOut[index].Key) >= 0)
                return false;
        }
        return true;
    }

    static void ValidateContinuationPayloadBounds(
        ReadOnlySpan<byte> payload,
        RelationQuerySourceReadConstraint constraint,
        int maximumKeyBytes)
    {
        var maximumFanOutEntries = constraint is RelationQueryRelationshipKeyBatchLookup lookup
            ? lookup.Keys.Length
            : 0;
        var maximumEncodedKeyBytes = checked(maximumKeyBytes * 6);
        var json = new Utf8JsonReader(payload, isFinalBlock: true, state: default);
        var readIdentity = false;
        var readFanOut = false;
        var fanOutDepth = -1;
        var fanOutEntries = 0;
        while (json.Read())
        {
            if (json.TokenType == JsonTokenType.PropertyName)
            {
                readIdentity = json.ValueTextEquals("identity"u8);
                readFanOut = json.ValueTextEquals("fanOut"u8);
                continue;
            }
            if (readIdentity)
            {
                if (json.TokenType != JsonTokenType.String || json.ValueSpan.Length > maximumEncodedKeyBytes)
                    throw new JsonException("The continuation identity exceeds its physical key bound.");
                readIdentity = false;
            }
            if (readFanOut)
            {
                if (json.TokenType != JsonTokenType.StartArray)
                    throw new JsonException("The continuation fan-out payload is not an array.");
                fanOutDepth = json.CurrentDepth;
                readFanOut = false;
                continue;
            }
            if (fanOutDepth >= 0
                && json.TokenType == JsonTokenType.StartObject
                && json.CurrentDepth == fanOutDepth + 1
                && ++fanOutEntries > maximumFanOutEntries)
            {
                throw new JsonException("The continuation fan-out exceeds the exact request batch.");
            }
            if (fanOutDepth >= 0
                && json.TokenType == JsonTokenType.String
                && json.ValueSpan.Length > maximumEncodedKeyBytes)
            {
                throw new JsonException("A continuation fan-out key exceeds its physical key bound.");
            }
            if (fanOutDepth >= 0
                && json.TokenType == JsonTokenType.EndArray
                && json.CurrentDepth == fanOutDepth)
            {
                fanOutDepth = -1;
            }
        }
    }

    long ComputeMaximumContinuationValueCharacters()
    {
        var maximumFanOutEntries = Math.Min(
            (long)reader.Policy.MaximumBatchKeys,
            reader.Limits.MaximumBatchSize);
        var minimumPayloadCharacters = checked(
            (long)reader.StorageBinding.Fingerprint.Value.Length
            + table.PlacementBinding.Value.Length
            + Descriptor.CapabilityProfile.Id.Value.Length
            + reader.Policy.MaximumKeyBytes
            + maximumFanOutEntries * (reader.Policy.MaximumKeyBytes + 1L));
        if (minimumPayloadCharacters > MaximumContinuationValueCharacters)
            return MaximumContinuationValueCharacters + 1L;
        var maximumEscapedKeyBytes = checked((long)reader.Policy.MaximumKeyBytes * 6);
        var maximumFingerprint = new MaterializationSourceReadFingerprint(
            MaterializationSourceReadFingerprinter.Algorithm,
            MaterializationSourceReadFingerprinter.Canonicalization,
            new string('f', 64));
        var fixedPayload = new ContinuationPayload(
            maximumFingerprint,
            reader.StorageBinding.Fingerprint.Value,
            table.PlacementBinding.Value,
            Descriptor.CapabilityProfile.Id.Value,
            int.MaxValue,
            int.MaxValue,
            Identity: string.Empty,
            FanOut: []);
        var fixedPayloadBytes = JsonSerializer.SerializeToUtf8Bytes(
            fixedPayload,
            CanonicalJsonOptions).LongLength;
        var emptyKeyFanOutEntryBytes = JsonSerializer.SerializeToUtf8Bytes(
            new PostgresRelationQueryFanOutCount(string.Empty, long.MaxValue),
            CanonicalJsonOptions).LongLength;
        var fanOutBytes = maximumFanOutEntries == 0
            ? 0
            : checked(
                maximumFanOutEntries * (emptyKeyFanOutEntryBytes + maximumEscapedKeyBytes)
                + maximumFanOutEntries - 1);
        var payloadBytes = checked(fixedPayloadBytes + maximumEscapedKeyBytes + fanOutBytes);
        var payloadCharacters = checked(((payloadBytes + 2) / 3) * 4);
        return checked(ContinuationPrefix.Length + payloadCharacters + 1 + 43);
    }

    static long CanonicalByteCount(RelationQuerySourceReadObservation observation) =>
        StrictDocumentJson.GetCanonicalBytes(observation, CanonicalJsonOptions).LongLength;

    void RequireBoundedConstraint(
        RelationQuerySourceReadConstraint constraint,
        string parameterName)
    {
        var keys = constraint switch
        {
            RelationQueryIdentityBatchLookup lookup => lookup.Identities,
            RelationQueryRelationshipKeyBatchLookup lookup => lookup.Keys,
            _ => []
        };
        if (keys.IsDefaultOrEmpty)
            return;
        var maximumBatchKeys = checked((int)Math.Min(
            (long)reader.Policy.MaximumBatchKeys,
            reader.Limits.MaximumBatchSize));
        if (keys.Length > maximumBatchKeys)
        {
            throw new ArgumentException(
                "The PostgreSQL materialization read exceeds its key batch or key byte bound.",
                parameterName);
        }
        foreach (var key in keys)
        {
            if (PostgresSqlUtf8.GetByteCount(key, parameterName) <= reader.Policy.MaximumKeyBytes)
                continue;
            throw new ArgumentException(
                "The PostgreSQL materialization read exceeds its key batch or key byte bound.",
                parameterName);
        }
    }

    ImmutableArray<DocumentValidationDiagnostic> Diagnostics(RelationQuerySourceReadResult read)
    {
        if (read.State is not (RelationQuerySourceReadState.Failed or RelationQuerySourceReadState.Inconclusive))
            return [];
        var failed = read.State == RelationQuerySourceReadState.Failed;
        return
        [
            MaterializationContract.CreateDiagnostic(
                failed ? SourceReadFailedDiagnosticCode : SourceReadInconclusiveDiagnosticCode,
                failed ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                failed
                    ? "The PostgreSQL source read failed without producing attributable observations."
                    : "The PostgreSQL source read could not prove a complete bounded result.",
                "$runtime.sourcePage",
                "source-read",
                Descriptor.Source.Value,
                EvidenceReferences(read),
                "one complete bounded PostgreSQL statement result",
                failed ? "failed" : "inconclusive")
        ];
    }

    ImmutableArray<DocumentValidationDiagnostic> BoundaryDiagnostics(RelationQuerySourceReadResult read) =>
    [
        MaterializationContract.CreateDiagnostic(
            ReadBoundaryReachedDiagnosticCode,
            DiagnosticSeverity.Warning,
            "The PostgreSQL table has more rows, but the wrapped Relations request reached its declared acquisition boundary.",
            "$runtime.sourcePage",
            "source-read",
            Descriptor.Source.Value,
            EvidenceReferences(read),
            "authoritative exhaustion inside the declared Relations boundary",
            "additional provider rows exist beyond that boundary")
    ];

    ImmutableArray<string> EvidenceReferences(RelationQuerySourceReadResult read)
    {
        var references = ImmutableArray.CreateBuilder<string>(
            2 + (reader.RuntimeEvidenceReference is null ? 0 : 1) + (read.EvidenceReference is null ? 0 : 1));
        references.Add(EvidencePrefix);
        references.Add($"postgres-binding/sha256/{reader.StorageBinding.Fingerprint.Value}");
        if (reader.RuntimeEvidenceReference is { } runtimeEvidence)
            references.Add(runtimeEvidence);
        if (read.EvidenceReference is { } readEvidence)
            references.Add(readEvidence);
        return references.MoveToImmutable();
    }

    sealed record Cursor(
        object? AfterIdentity,
        int EmittedRows,
        ImmutableArray<PostgresRelationQueryFanOutCount> FanOut);

    sealed record ContinuationPayload(
        MaterializationSourceReadFingerprint ReadFingerprint,
        string BindingFingerprint,
        string Placement,
        string SourceProfile,
        int ScalarType,
        int EmittedRows,
        string Identity,
        ImmutableArray<PostgresRelationQueryFanOutCount> FanOut);
}
