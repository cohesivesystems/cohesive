using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Observability;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Bounded canonical observation-envelope acquisition over one Cosmos container.</summary>
/// <remarks>
/// This reader retrieves identity plus the exact semantic and correlation fields requested by a physical plan.
/// Semantic filtering, joining, projection, aggregation, and paging remain owned by the relation/query interpreter.
/// Provider affinity, selector policy, partition policy, and all row/key/chunk boundaries are validated before I/O.
/// </remarks>
/// <remarks>
/// Every query orders by the configured identity path to make provider acquisition deterministic. The Cosmos
/// container indexing policy must therefore provide a compatible range index for that path; an SDK or service
/// rejection is returned as failed source evidence. Partition selectors and fixed keys are caller assertions under
/// <see cref="CosmosRelationQuerySourcePolicy"/>, not facts discovered from container metadata.
/// </remarks>
public sealed class CosmosRelationQuerySourceReader : IRelationQuerySourceReader
{
    /// <summary>Conventional entity-envelope observation identity property.</summary>
    public const string ObservationIdentitySourceSelector = "observationId";

    /// <summary>Conventional entity-envelope scalar partition-coordinate property.</summary>
    public const string ObservationPartitionSourceSelector = "partitionKey";

    /// <summary>Conventional entity-envelope semantic observation property.</summary>
    public const string ObservationEnvelopeSourceSelector = "observation";

    /// <summary>Entity-envelope document discriminator property.</summary>
    public const string DocumentKindSourceSelector = "documentKind";

    /// <summary>Entity-envelope observation-type property.</summary>
    public const string ObservationTypeSourceSelector = "observationType";

    /// <summary>Observation-envelope logical stream property.</summary>
    public const string StreamNameSourceSelector = "streamName";

    const string EvidencePrefix = "cohesive.adapters.cosmos/entity-source/v1";
    const string IdentityAlias = "_identity";
    const string RelationshipAlias = "_relationship";
    const string PartitionAlias = "_partition";
    const string RootAlias = "c";

    readonly RelationQuerySourceInstance source;
    readonly CosmosJsonQueryFeedReader feedReader;
    readonly FieldPath identityPath;
    readonly string accountFingerprint;

    /// <summary>Conventional physical limits for a Cosmos entity source.</summary>
    public static RelationQuerySourcePlacementLimits DefaultLimits { get; } = new(
        maximumBatchSize: 500,
        maximumBufferedRows: 10_000,
        maximumFanOut: 500,
        maximumConcurrency: 4);

    /// <summary>
    /// Exact primitive acquisition capabilities implemented by the Cosmos entity reader. Effective partition policy
    /// remains an independently inspectable runtime gate; reader construction rejects policy that would make every
    /// advertised acquisition inconclusive.
    /// </summary>
    public static RelationQueryTargetCapabilityProfile TargetProfile { get; } = CreateTargetProfile();

    /// <summary>The current default entity-document discriminator used by the Cosmos observation repository.</summary>
    public const string DefaultEntityDocumentKind =
        CosmosObservationOutboxRepositoryOptions.DefaultEntityDocumentKind;

    /// <summary>Creates a canonical Cosmos observation-envelope reader.</summary>
    /// <param name="shape">Exact graph-qualified semantic shape projected by the reader.</param>
    /// <param name="source">Canonical source identity, capability profile, and execution limits.</param>
    /// <param name="container">Cosmos SDK container used for bounded acquisition.</param>
    /// <param name="databaseId">Exact Cosmos database identity retained as binding provenance.</param>
    /// <param name="containerId">Exact Cosmos container identity retained as binding provenance.</param>
    /// <param name="policy">Explicit partition and physical query limits.</param>
    /// <param name="identitySourceSelector">
    /// Physical property-only observation-identity path, or <see langword="null"/> for <c>observationId</c>. Every
    /// returned document must contain a nonempty JSON string at the effective path.
    /// </param>
    /// <param name="fieldSourceSelector">
    /// Semantic-to-physical field policy, or <see langword="null"/> to select below <c>observation</c>. A custom
    /// policy is evaluated for each requested semantic path and its result is validated as a property-only Cosmos
    /// path before that request performs I/O; it is not exhaustively validated by this constructor.
    /// </param>
    /// <param name="relationshipKeySourceSelector">
    /// Semantic-to-physical relationship-reference policy, or <see langword="null"/> to select below
    /// <c>observation</c>. A custom policy is evaluated and path-validated per requested relationship reference,
    /// not exhaustively by this constructor.
    /// </param>
    /// <param name="entityDocumentKind">
    /// Entity-document discriminator, or <see langword="null"/> for <see cref="DefaultEntityDocumentKind"/>.
    /// </param>
    /// <param name="persistedObservationType">
    /// Exact graph-qualified observation type stored in matching envelopes, or <see langword="null"/> to use
    /// <paramref name="shape"/>. This may differ from the projected shape when, for example, an outbox envelope
    /// projects an embedded entity observation and message metadata into a dedicated message shape.
    /// </param>
    /// <param name="persistedStreamName">
    /// Optional exact top-level stream name required of matching envelopes. This keeps an outbox baseline query
    /// identical to a stream-filtered managed change binding.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="source"/>, <paramref name="container"/>, <paramref name="databaseId"/>,
    /// <paramref name="containerId"/>, or <paramref name="policy"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An affinity identity is incomplete or disagrees with the SDK container; the effective identity selector is
    /// not a property-only path; a document discriminator, persisted observation type, or persisted stream name is
    /// invalid; cross-partition reads are prohibited without a fixed partition scope; or <paramref name="source"/> does not use
    /// <see cref="TargetProfile"/>, advertises limits wider than <paramref name="policy"/> can honor, or advertises a
    /// row buffer larger than the runtime can materialize.
    /// </exception>
    public CosmosRelationQuerySourceReader(
        QualifiedShapeId shape,
        RelationQuerySourceInstance source,
        Container container,
        string databaseId,
        string containerId,
        CosmosRelationQuerySourcePolicy policy,
        string? identitySourceSelector = null,
        RelationQueryPlacementFieldSelector? fieldSourceSelector = null,
        RelationQueryPlacementFieldSelector? relationshipKeySourceSelector = null,
        string? entityDocumentKind = null,
        QualifiedShapeId? persistedObservationType = null,
        string? persistedStreamName = null)
        : this(
            shape: shape,
            source: source,
            feedReader: new CosmosJsonQueryFeedReader(ValidateContainer(container, databaseId, containerId)),
            accountEndpoint: CosmosPhysicalAffinity.CanonicalAccountEndpointText(container.Database.Client.Endpoint),
            databaseId: databaseId,
            containerId: containerId,
            policy: policy,
            identitySourceSelector: identitySourceSelector,
            fieldSourceSelector: fieldSourceSelector,
            relationshipKeySourceSelector: relationshipKeySourceSelector,
            entityDocumentKind: entityDocumentKind,
            clientConsistencyLevel: container.Database.Client.ClientOptions.ConsistencyLevel,
            persistedObservationType: persistedObservationType,
            persistedStreamName: persistedStreamName)
    {
    }

    internal CosmosRelationQuerySourceReader(
        QualifiedShapeId shape,
        RelationQuerySourceInstance source,
        CosmosJsonQueryFeedReader feedReader,
        string accountEndpoint,
        string databaseId,
        string containerId,
        CosmosRelationQuerySourcePolicy policy,
        string? identitySourceSelector = null,
        RelationQueryPlacementFieldSelector? fieldSourceSelector = null,
        RelationQueryPlacementFieldSelector? relationshipKeySourceSelector = null,
        string? entityDocumentKind = null,
        ConsistencyLevel? clientConsistencyLevel = null,
        QualifiedShapeId? persistedObservationType = null,
        string? persistedStreamName = null)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("A Cosmos entity reader requires a graph-qualified shape.", nameof(shape));

        this.source = Guard.RequireNotNull(source);
        this.feedReader = Guard.RequireNotNull(feedReader);
        Policy = Guard.RequireNotNull(policy);
        if (Policy.CrossPartitionPolicy == CosmosRelationQueryCrossPartitionPolicy.Prohibit
            && Policy.FixedPartitionKey is null)
        {
            throw new ArgumentException(
                "A Cosmos entity reader that prohibits cross-partition queries requires a fixed partition scope; otherwise every advertised acquisition would be inconclusive.",
                nameof(policy));
        }
        var accountEndpointText = Guard.RequireNotNullOrWhiteSpace(accountEndpoint);
        if (!Uri.TryCreate(accountEndpointText, UriKind.Absolute, out var accountEndpointUri))
            throw new ArgumentException("A Cosmos account endpoint must be an absolute URI.", nameof(accountEndpoint));
        var normalizedAccountEndpoint = CosmosPhysicalAffinity.NormalizeAccountEndpoint(accountEndpointUri);
        AccountEndpoint = CosmosPhysicalAffinity.CanonicalAccountEndpointText(normalizedAccountEndpoint);
        accountFingerprint = CosmosPhysicalAffinity.Fingerprint(AccountEndpoint);
        DatabaseId = Guard.RequireNotNullOrWhiteSpace(databaseId);
        ContainerId = Guard.RequireNotNullOrWhiteSpace(containerId);
        if (!string.Equals(
                feedReader.AccountEndpoint.AbsoluteUri,
                normalizedAccountEndpoint.AbsoluteUri,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Cosmos feed account '{feedReader.AccountEndpoint}' does not match declared account '{normalizedAccountEndpoint}'.",
                nameof(accountEndpoint));
        }
        if (!string.Equals(feedReader.DatabaseName, DatabaseId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Cosmos feed database '{feedReader.DatabaseName}' does not match declared database '{DatabaseId}'.",
                nameof(databaseId));
        }
        if (!string.Equals(feedReader.ContainerName, ContainerId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Cosmos feed container '{feedReader.ContainerName}' does not match declared container '{ContainerId}'.",
                nameof(containerId));
        }
        if (!source.TargetProfile.HasSameSemantics(TargetProfile))
        {
            throw new ArgumentException(
                $"Cosmos entity readers require target profile '{TargetProfile.Id.Value}'.",
                nameof(source));
        }
        if (source.Limits.MaximumBufferedRows >= Array.MaxLength)
        {
            throw new ArgumentException(
                $"A Cosmos entity reader cannot retain more than {Array.MaxLength - 1} canonical rows because one additional physical row is required to prove a complete boundary.",
                nameof(source));
        }
        if (!Equals(Policy.GetEffectivePlacementLimits(source.Limits), source.Limits))
        {
            throw new ArgumentException(
                "A Cosmos entity source must advertise placement limits already constrained by its physical policy.",
                nameof(source));
        }

        Shape = shape;
        Descriptor = new(source.Id, source.ExecutionDomain, source.TargetProfile);
        IdentitySourceSelector = identitySourceSelector is null
            ? ObservationIdentitySourceSelector
            : Guard.RequireNotNullOrWhiteSpace(identitySourceSelector);
        identityPath = CosmosRelationQuerySourceSelectors.RequirePropertyPath(
            IdentitySourceSelector,
            nameof(identitySourceSelector));
        FieldSourceSelector = fieldSourceSelector ?? GetObservationFieldSourceSelector;
        RelationshipKeySourceSelector = relationshipKeySourceSelector ?? GetObservationFieldSourceSelector;
        EntityDocumentKind = entityDocumentKind is null
            ? DefaultEntityDocumentKind
            : Guard.RequireNotNullOrWhiteSpace(entityDocumentKind);
        var effectivePersistedObservationType = persistedObservationType ?? shape;
        if (string.IsNullOrWhiteSpace(effectivePersistedObservationType.GraphId.Value)
            || string.IsNullOrWhiteSpace(effectivePersistedObservationType.ShapeId.Value))
        {
            throw new ArgumentException(
                "A Cosmos reader requires a graph-qualified persisted observation type.",
                nameof(persistedObservationType));
        }

        PersistedObservationType = effectivePersistedObservationType;
        if (persistedStreamName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(persistedStreamName);
        }

        PersistedStreamName = persistedStreamName;
        ClientConsistencyLevel = clientConsistencyLevel;
    }

    /// <summary>Exact graph-qualified shape returned by this reader.</summary>
    public QualifiedShapeId Shape { get; }

    /// <inheritdoc />
    public RelationQuerySourceReaderDescriptor Descriptor { get; }

    /// <summary>Exact Cosmos database identity retained as physical binding provenance.</summary>
    public string DatabaseId { get; }

    /// <summary>Normalized Cosmos account endpoint retained as non-secret physical binding provenance.</summary>
    public string AccountEndpoint { get; }

    /// <summary>Exact Cosmos container identity retained as physical binding provenance.</summary>
    public string ContainerId { get; }

    /// <summary>
    /// Explicit physical partition assertions and cross-partition, row, key, chunk, and SDK-page policy.
    /// </summary>
    public CosmosRelationQuerySourcePolicy Policy { get; }

    /// <summary>
    /// Explicit consistency override configured on the production Cosmos client, or <see langword="null"/> when
    /// the client inherits its account policy. Materialization sources reject an explicitly weaker override.
    /// </summary>
    internal ConsistencyLevel? ClientConsistencyLevel { get; }

    /// <summary>Effective source limits already constrained by the Cosmos physical policy.</summary>
    internal RelationQuerySourcePlacementLimits Limits => source.Limits;

    /// <summary>Entity-envelope discriminator required by every source query.</summary>
    public string EntityDocumentKind { get; }

    /// <summary>
    /// Exact graph-qualified observation type stored in envelopes selected by this reader. It may differ from
    /// <see cref="Shape"/>, which is the semantic shape projected from those envelopes.
    /// </summary>
    public QualifiedShapeId PersistedObservationType { get; }

    /// <summary>Optional exact top-level stream name required of every selected Cosmos envelope.</summary>
    public string? PersistedStreamName { get; }

    /// <summary>
    /// Stable property-only path interpreted as a nonempty JSON-string semantic observation identity and used as
    /// the deterministic Cosmos <c>ORDER BY</c> path.
    /// </summary>
    public string IdentitySourceSelector { get; }

    /// <summary>
    /// Deterministic semantic-to-physical field selector evaluated and property-path-validated for each request.
    /// </summary>
    public RelationQueryPlacementFieldSelector FieldSourceSelector { get; }

    /// <summary>
    /// Deterministic semantic-to-physical relationship-reference selector evaluated and property-path-validated
    /// for each request.
    /// </summary>
    public RelationQueryPlacementFieldSelector RelationshipKeySourceSelector { get; }

    /// <summary>Projects one semantic field path into the conventional Cosmos observation-envelope selector.</summary>
    /// <param name="semanticPath">Non-empty semantic field path to place below <c>observation</c>.</param>
    /// <returns>The deterministic physical selector rooted at <c>observation</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="semanticPath"/> is empty.</exception>
    public static string GetObservationFieldSourceSelector(FieldPath semanticPath) => new FieldPath(
        [FieldPathSegment.ForField(ObservationEnvelopeSourceSelector), .. semanticPath.Segments]).ToString();

    /// <inheritdoc />
    public ValueTask<RelationQuerySourceReadResult> ReadAsync(
        RelationQuerySourceReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return CosmosRelationQueryTelemetry.Emitter.IsEnabled
            ? ReadObservedAsync(request, cancellationToken)
            : ReadCoreAsync(request, cancellationToken);
    }

    /// <summary>
    /// Reads one complete buffered materialization acquisition while preserving aggregate provider evidence and
    /// provider failures for the owning materialization adapter.
    /// </summary>
    /// <param name="request">Exact canonical non-enumeration Relations source request.</param>
    /// <param name="cancellationToken">Caller cancellation observed before I/O and throughout projection.</param>
    /// <returns>The canonical read plus aggregate request charge and final provider status.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="request"/> is a bounded enumeration.</exception>
    /// <exception cref="CosmosRelationQueryMaterializationProviderException">
    /// The provider rejects an acquisition request; the exception retains the provider failure and request charge
    /// from completed responses for the owning materialization adapter.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    internal async ValueTask<CosmosRelationQueryMaterializationRead> ReadMaterializationBufferedAsync(
        RelationQuerySourceReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Constraint is RelationQueryBoundedEnumeration)
        {
            throw new ArgumentException(
                "Buffered materialization acquisition requires an identity or relationship-key batch.",
                nameof(request));
        }

        CosmosRelationQueryMaterializationAccumulator accumulator = new();
        try
        {
            var read = await ReadCoreAsync(
                request,
                cancellationToken,
                accumulator,
                preserveProviderFailure: true).ConfigureAwait(false);
            return new(read, accumulator.RequestCharge, accumulator.StatusCode);
        }
        catch (CosmosProviderProtocolException exception)
        {
            throw new CosmosRelationQueryMaterializationProtocolException(
                exception,
                AddUnaccountedResponseCharge(
                    accumulator.RequestCharge,
                    exception.RequestCharge,
                    exception.ResponseChargeAccounted),
                exception.StatusCode ?? accumulator.StatusCode,
                CombineProviderEvidence(
                    accumulator.ProviderEvidenceReference,
                    exception.ProviderEvidenceReference));
        }
        catch (CosmosException exception)
        {
            throw new CosmosRelationQueryMaterializationProviderException(
                exception,
                accumulator.RequestCharge);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            var responseCancellation = exception as CosmosProviderResponseCanceledException;
            throw new CosmosRelationQueryMaterializationCanceledException(
                exception,
                AddUnaccountedResponseCharge(
                    accumulator.RequestCharge,
                    responseCancellation?.RequestCharge,
                    responseCancellation?.ResponseChargeAccounted ?? true),
                responseCancellation?.StatusCode ?? accumulator.StatusCode,
                CombineProviderEvidence(
                    accumulator.ProviderEvidenceReference,
                    responseCancellation?.ProviderEvidenceReference),
                cancellationToken);
        }
    }

    /// <summary>
    /// Reads one complete provider page for a bounded-enumeration materialization without interpreting or
    /// truncating the Cosmos continuation.
    /// </summary>
    /// <param name="request">Exact canonical Relations source-read request.</param>
    /// <param name="providerContinuation">Opaque Cosmos continuation, or <see langword="null"/> for the first page.</param>
    /// <param name="maximumProviderItems">Positive SDK page-size bound supplied for this response.</param>
    /// <param name="cancellationToken">Caller cancellation observed before I/O and throughout projection.</param>
    /// <returns>One complete projected Relations page plus provider continuation and operation evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="request"/> is not a bounded enumeration or <paramref name="providerContinuation"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumProviderItems"/> is not positive.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    internal async ValueTask<CosmosRelationQueryMaterializationPage> ReadMaterializationPageAsync(
        RelationQuerySourceReadRequest request,
        string? providerContinuation,
        int maximumProviderItems,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Constraint is not RelationQueryBoundedEnumeration)
        {
            throw new ArgumentException(
                "Provider-continuation materialization paging requires a bounded enumeration.",
                nameof(request));
        }
        if (providerContinuation is not null && string.IsNullOrWhiteSpace(providerContinuation))
            throw new ArgumentException("A provider continuation cannot be empty or white space.", nameof(providerContinuation));
        if (maximumProviderItems <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumProviderItems),
                maximumProviderItems,
                "A materialization provider page must be bounded.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (ValidateRequest(request, cancellationToken) is { } invalid)
        {
            return CosmosRelationQueryMaterializationPage.FromRead(Failed(request, invalid));
        }

        var projection = CreateProjection(request, relationshipSelector: null);
        var statement = BuildStatement(projection, predicate: null);
        var providerItems = Math.Min(maximumProviderItems, Policy.MaximumSdkPageSize);
        QueryRequestOptions options = new()
        {
            MaxItemCount = providerItems,
            MaxBufferedItemCount = providerItems,
            MaxConcurrency = checked((int)Math.Min(int.MaxValue, source.Limits.MaximumConcurrency)),
            ConsistencyLevel = Policy.ReadConsistencyLevel
        };
        if (Policy.FixedPartitionKey is { } partitionKey)
            options.PartitionKey = partitionKey;
        var prepared = feedReader.Prepare(
            statement.ToQueryDefinition(),
            options,
            Policy.RequestSizeLimits);
        var feed = await feedReader.ReadPageAsync(
            prepared,
            feedRange: null,
            providerContinuation,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var materialized = MaterializeRows(
                request,
                projection,
                feed.Rows,
                feed.ProviderEvidenceReference,
                cancellationToken);
            if (materialized.FailureReason is { } failure)
            {
                return new(
                    Failed(request, failure, feed.ProviderEvidenceReference),
                    feed.NextContinuationToken,
                    feed.HasMoreResults,
                    feed.RequestCharge,
                    feed.StatusCode,
                    feed.ProviderEvidenceReference);
            }
            if (HasDuplicateIdentity(materialized.Rows, allowRepeatedPhysicalRows: false, out _))
            {
                return new(
                    Failed(request, "duplicate-observation-identity", feed.ProviderEvidenceReference),
                    feed.NextContinuationToken,
                    feed.HasMoreResults,
                    feed.RequestCharge,
                    feed.StatusCode,
                    feed.ProviderEvidenceReference);
            }

            var observations = ProjectObservations(materialized.Rows.AsSpan());
            var hasMore = feed.NextContinuationToken is not null;
            if (feed.HasMoreResults && !hasMore)
                throw new InvalidOperationException("The Cosmos query iterator reported more results without a resumable continuation.");
            var state = hasMore
                ? RelationQuerySourceReadState.Partial
                : observations.IsDefaultOrEmpty
                    ? RelationQuerySourceReadState.NotFound
                    : RelationQuerySourceReadState.Complete;
            return new(
                new(
                    state,
                    observations,
                    Evidence(
                        request,
                        hasMore ? "materialization-page-partial" : "materialization-page-complete",
                        feed.ProviderEvidenceReference)),
                feed.NextContinuationToken,
                hasMore,
                feed.RequestCharge,
                feed.StatusCode,
                feed.ProviderEvidenceReference);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new CosmosRelationQueryMaterializationCanceledException(
                exception,
                feed.RequestCharge,
                feed.StatusCode,
                feed.ProviderEvidenceReference,
                cancellationToken);
        }
    }

    async ValueTask<RelationQuerySourceReadResult> ReadCoreAsync(
        RelationQuerySourceReadRequest request,
        CancellationToken cancellationToken,
        CosmosRelationQueryMaterializationAccumulator? accumulator = null,
        bool preserveProviderFailure = false)
    {
        try
        {
            if (ValidateRequest(request, cancellationToken) is { } invalid)
                return Failed(request, invalid);
            if (BatchBoundaryExceeded(request.Constraint))
                return Inconclusive(request, "batch-boundary-exceeded");
            return request.Constraint switch
            {
                RelationQueryBoundedEnumeration enumeration =>
                    await ReadEnumerationAsync(request, enumeration, accumulator, cancellationToken).ConfigureAwait(false),
                RelationQueryIdentityBatchLookup identity =>
                    await ReadIdentityBatchAsync(request, identity, accumulator, cancellationToken).ConfigureAwait(false),
                RelationQueryRelationshipKeyBatchLookup relationship =>
                    await ReadRelationshipBatchAsync(request, relationship, accumulator, cancellationToken).ConfigureAwait(false),
                _ => Failed(request, "unsupported-read-constraint")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CosmosQueryRequestSizeLimitException exception)
        {
            return Inconclusive(request, exception.Reason);
        }
        catch (CosmosException exception) when (!preserveProviderFailure)
        {
            return Failed(
                request,
                $"provider-read-failed/status/{(int)exception.StatusCode}/substatus/{exception.SubStatusCode}");
        }
        catch (Exception exception) when (
            (!preserveProviderFailure
             || exception is not CosmosException and not CosmosProviderProtocolException)
            && exception is not OutOfMemoryException
            && exception is not StackOverflowException)
        {
            return Failed(
                request,
                $"provider-read-failed/{Uri.EscapeDataString(exception.GetType().FullName ?? exception.GetType().Name)}");
        }
    }

    async ValueTask<RelationQuerySourceReadResult> ReadObservedAsync(
        RelationQuerySourceReadRequest request,
        CancellationToken cancellationToken)
    {
        Activity? activity = CosmosRelationQueryTelemetry.Emitter.StartActivity(
            CosmosRelationQueryTelemetry.SourceAcquisitionActivityName,
            ActivityKind.Client);
        var started = CosmosRelationQueryTelemetry.Emitter.StartTimer();
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(
                RelationQueryTelemetry.ReadKindTagName,
                RelationQueryTelemetry.GetTagValue(GetReadKind(request.Constraint)));
        }

        try
        {
            var result = await ReadCoreAsync(request, cancellationToken).ConfigureAwait(false);
            if (activity?.IsAllDataRequested == true)
            {
                activity.SetTag(RelationQueryTelemetry.RowCountTagName, result.Observations.Length);
            }
            CosmosRelationQueryTelemetry.Emitter.CompleteOperation(
                activity,
                started,
                CosmosRelationQueryTelemetry.SourceAcquisitionActivityName,
                RelationQueryTelemetry.GetStatusTagValue(result.State));
            return result;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            CosmosRelationQueryTelemetry.Emitter.CompleteOperation(
                activity,
                started,
                CosmosRelationQueryTelemetry.SourceAcquisitionActivityName,
                RelationQueryTelemetry.CanceledStatus,
                exception: exception);
            throw;
        }
        catch (Exception exception)
        {
            CosmosRelationQueryTelemetry.Emitter.CompleteOperation(
                activity,
                started,
                CosmosRelationQueryTelemetry.SourceAcquisitionActivityName,
                RelationQueryTelemetry.ExceptionStatus,
                exception: exception);
            throw;
        }
    }

    static RelationQuerySourceReadKind GetReadKind(RelationQuerySourceReadConstraint constraint) => constraint switch
    {
        RelationQueryBoundedEnumeration => RelationQuerySourceReadKind.BoundedEnumeration,
        RelationQueryIdentityBatchLookup => RelationQuerySourceReadKind.IdentityBatch,
        RelationQueryRelationshipKeyBatchLookup => RelationQuerySourceReadKind.RelationshipKeyBatch,
        _ => throw new ArgumentOutOfRangeException(nameof(constraint), constraint, "Unsupported source-read constraint.")
    };

    async ValueTask<RelationQuerySourceReadResult> ReadEnumerationAsync(
        RelationQuerySourceReadRequest request,
        RelationQueryBoundedEnumeration enumeration,
        CosmosRelationQueryMaterializationAccumulator? accumulator,
        CancellationToken cancellationToken)
    {
        var maximumRows = Math.Min(
            enumeration.MaximumRows,
            Math.Min(
                request.MaximumBufferedRows,
                Math.Min(source.Limits.MaximumBufferedRows, Policy.MaximumEnumerationRows)
                ));
        var projection = CreateProjection(request, relationshipSelector: null);
        var statement = BuildStatement(projection, predicate: null);
        var feed = await ReadFeedAsync(statement, ProbeLimit(maximumRows), accumulator, cancellationToken).ConfigureAwait(false);
        var materialized = MaterializeRows(
            request,
            projection,
            feed.Rows,
            feed.ProviderEvidenceReference,
            cancellationToken
            );
        if (materialized.FailureReason is { } failure)
            return Failed(request, failure, feed.ProviderEvidenceReference);

        var rows = materialized.Rows;
        if (HasDuplicateIdentity(rows, allowRepeatedPhysicalRows: false, out _))
            return Failed(request, "duplicate-observation-identity", feed.ProviderEvidenceReference);

        var partial = feed.BoundaryStopped || rows.Length > maximumRows;
        var selectedCount = checked((int)Math.Min(maximumRows, rows.Length));
        var observations = ProjectObservations(rows.AsSpan(0, selectedCount));
        if (!partial)
        {
            return new(
                RelationQuerySourceReadState.Complete,
                observations,
                Evidence(request, "enumeration-complete", feed.ProviderEvidenceReference));
        }

        return observations.IsDefaultOrEmpty
            ? Inconclusive(request, "enumeration-boundary-reached", feed.ProviderEvidenceReference)
            : new(
                RelationQuerySourceReadState.Partial,
                observations,
                Evidence(request, "enumeration-partial", feed.ProviderEvidenceReference));
    }

    async ValueTask<RelationQuerySourceReadResult> ReadIdentityBatchAsync(
        RelationQuerySourceReadRequest request,
        RelationQueryIdentityBatchLookup lookup,
        CosmosRelationQueryMaterializationAccumulator? accumulator,
        CancellationToken cancellationToken)
    {
        var projection = CreateProjection(request, relationshipSelector: null);
        var maximumRows = MaximumBufferedRows(request);
        List<MaterializedRow> rows = [];
        Dictionary<string, PhysicalOccurrence> occurrences = new(StringComparer.Ordinal);
        string? providerEvidence = null;
        var partial = false;
        var chunkIndex = 0;
        foreach (var keys in Chunks(lookup.Identities))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var predicate = CosmosSqlExpression.Function(
                CosmosSqlFunction.ArrayContains,
                CosmosSqlExpression.Parameter(keys),
                CosmosSqlExpression.Property(RootAlias, identityPath));
            var feed = await ReadFeedAsync(
                BuildStatement(projection, predicate),
                ProbeLimit(maximumRows - rows.Count),
                accumulator,
                cancellationToken).ConfigureAwait(false);
            providerEvidence = CombineProviderEvidence(providerEvidence, feed.ProviderEvidenceReference);
            var materialized = MaterializeRows(
                request,
                projection,
                feed.Rows,
                feed.ProviderEvidenceReference,
                cancellationToken);
            if (materialized.FailureReason is { } failure)
                return Failed(request, failure, providerEvidence);

            HashSet<string> chunkKeys = new(keys, StringComparer.Ordinal);
            foreach (var row in materialized.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!chunkKeys.Contains(row.Observation.Identity))
                    return Failed(request, "identity-query-returned-unrequested-row", providerEvidence);
                if (!TryAddOccurrence(
                        row,
                        chunkIndex,
                        allowRepeatedPhysicalRows: false,
                        occurrences,
                        out var duplicateReason))
                {
                    return Failed(request, duplicateReason!, providerEvidence);
                }

                if ((long)rows.Count >= maximumRows)
                {
                    partial = true;
                    break;
                }
                rows.Add(row);
            }
            if (partial || feed.BoundaryStopped)
            {
                partial = true;
                break;
            }
            chunkIndex++;
        }

        if (partial)
        {
            var selectedCount = checked((int)Math.Min(maximumRows, rows.Count));
            var observations = ProjectObservations(CollectionsMarshal.AsSpan(rows)[..selectedCount]);
            return observations.IsDefaultOrEmpty
                ? Inconclusive(request, "identity-buffer-boundary-reached", providerEvidence)
                : new(
                    RelationQuerySourceReadState.Partial,
                    observations,
                    Evidence(request, "identity-partial", providerEvidence));
        }
        if (rows.Count == 0)
            return NotFound(request, "identity-not-found", providerEvidence);
        return new(
            RelationQuerySourceReadState.Complete,
            ProjectObservations(CollectionsMarshal.AsSpan(rows)),
            Evidence(request, "identity-complete", providerEvidence));
    }

    async ValueTask<RelationQuerySourceReadResult> ReadRelationshipBatchAsync(
        RelationQuerySourceReadRequest request,
        RelationQueryRelationshipKeyBatchLookup lookup,
        CosmosRelationQueryMaterializationAccumulator? accumulator,
        CancellationToken cancellationToken)
    {
        var projection = CreateProjection(request, lookup.SourceSelector);
        var maximumRows = MaximumBufferedRows(request);
        List<MaterializedRow> rows = [];
        Dictionary<string, PhysicalOccurrence> occurrences = new(StringComparer.Ordinal);
        Dictionary<string, long> fanOutByKey = new(StringComparer.Ordinal);
        string? providerEvidence = null;
        var partial = false;
        var chunkIndex = 0;
        foreach (var keys in Chunks(lookup.Keys))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var predicate = RelationshipPredicate(lookup.SourceSelector, keys);
            var feed = await ReadFeedAsync(
                BuildStatement(projection, predicate),
                // One physical document may be returned again for a key in a later chunk. Probe against the
                // complete unique-output boundary so already-retained rows cannot consume the evidence needed
                // to prove that a later chunk is exhausted.
                ProbeLimit(maximumRows),
                accumulator,
                cancellationToken).ConfigureAwait(false);
            providerEvidence = CombineProviderEvidence(providerEvidence, feed.ProviderEvidenceReference);
            var materialized = MaterializeRows(
                request,
                projection,
                feed.Rows,
                feed.ProviderEvidenceReference,
                cancellationToken);
            if (materialized.FailureReason is { } failure)
                return Failed(request, failure, providerEvidence);

            HashSet<string> chunkKeys = new(keys, StringComparer.Ordinal);
            foreach (var row in materialized.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = row.RelationshipReference ?? ObservationValue.Undefined;
                if (reference.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
                    continue;
                var extraction = RelationQueryReferenceKeyExtractor.Extract(
                    reference,
                    source.Limits.MaximumBatchSize,
                    cancellationToken,
                    out var referenceKeys);
                if (extraction == RelationQueryReferenceKeyExtractionState.BoundaryExceeded)
                    return Inconclusive(request, "relationship-reference-key-boundary-exceeded", providerEvidence);
                if (extraction == RelationQueryReferenceKeyExtractionState.Invalid)
                    return Failed(request, "relationship-reference-invalid", providerEvidence);

                var matched = false;
                foreach (var key in referenceKeys)
                {
                    if (!chunkKeys.Contains(key))
                        continue;

                    matched = true;
                    var fanOut = fanOutByKey.GetValueOrDefault(key) + 1;
                    if (fanOut > source.Limits.MaximumFanOut)
                        return Inconclusive(request, "relationship-fan-out-boundary-exceeded", providerEvidence);
                    fanOutByKey[key] = fanOut;
                }
                if (!matched)
                    return Failed(request, "relationship-query-returned-unrequested-row", providerEvidence);

                if (!TryAddOccurrence(
                        row,
                        chunkIndex,
                        allowRepeatedPhysicalRows: true,
                        occurrences,
                        out var duplicateReason))
                {
                    return Failed(request, duplicateReason!, providerEvidence);
                }
                if (occurrences[row.Observation.Identity].ChunkIndex != chunkIndex)
                    continue;

                if ((long)rows.Count >= maximumRows)
                {
                    partial = true;
                    break;
                }
                rows.Add(row);
            }
            if (partial || feed.BoundaryStopped)
            {
                partial = true;
                break;
            }
            chunkIndex++;
        }

        if (partial)
        {
            var selectedCount = checked((int)Math.Min(maximumRows, rows.Count));
            var observations = ProjectObservations(CollectionsMarshal.AsSpan(rows)[..selectedCount]);
            return observations.IsDefaultOrEmpty
                ? Inconclusive(request, "relationship-buffer-boundary-reached", providerEvidence)
                : new(
                    RelationQuerySourceReadState.Partial,
                    observations,
                    Evidence(request, "relationship-partial", providerEvidence));
        }
        if (rows.Count == 0)
            return NotFound(request, "relationship-not-found", providerEvidence);
        return new(
            RelationQuerySourceReadState.Complete,
            ProjectObservations(CollectionsMarshal.AsSpan(rows)),
            Evidence(request, "relationship-complete", providerEvidence));
    }

    static ImmutableArray<RelationQuerySourceReadObservation> ProjectObservations(
        ReadOnlySpan<MaterializedRow> rows)
    {
        var observations = ImmutableArray.CreateBuilder<RelationQuerySourceReadObservation>(rows.Length);
        foreach (ref readonly var row in rows)
            observations.Add(row.Observation);
        observations.Sort(
            static (left, right) => StringComparer.Ordinal.Compare(left.Identity, right.Identity));
        return observations.MoveToImmutable();
    }

    CosmosSqlStatement BuildStatement(ProjectionPlan projection, CosmosSqlExpression? predicate)
    {
        var builder = new CosmosSqlBuilder(RootAlias)
            .SelectValue(projection.Expression)
            .Where(CosmosSqlExpression.Binary(
                CosmosSqlBinaryOperator.Equal,
                CosmosSqlExpression.Property(RootAlias, FieldPath.FromField(DocumentKindSourceSelector)),
                CosmosSqlExpression.Parameter(EntityDocumentKind)))
            .Where(CosmosSqlExpression.Binary(
                CosmosSqlBinaryOperator.Equal,
                CosmosSqlExpression.Property(RootAlias, FieldPath.FromField(ObservationTypeSourceSelector)),
                CosmosSqlExpression.Parameter(PersistedObservationType.ShapeId.Value)))
            .Where(CosmosSqlExpression.Function(
                CosmosSqlFunction.IsDefined,
                CosmosSqlExpression.Property(RootAlias, FieldPath.FromField(ObservationEnvelopeSourceSelector))))
            .Where(CosmosSqlExpression.Function(
                CosmosSqlFunction.IsObject,
                CosmosSqlExpression.Property(RootAlias, FieldPath.FromField(ObservationEnvelopeSourceSelector))));
        if (PersistedStreamName is not null)
        {
            builder.Where(CosmosSqlExpression.Binary(
                CosmosSqlBinaryOperator.Equal,
                CosmosSqlExpression.Property(RootAlias, FieldPath.FromField(StreamNameSourceSelector)),
                CosmosSqlExpression.Parameter(PersistedStreamName)));
        }
        if (predicate is not null)
            builder.Where(predicate);
        builder.OrderBy(CosmosSqlExpression.Property(RootAlias, identityPath));
        return builder.Build();
    }

    CosmosSqlExpression RelationshipPredicate(string sourceSelector, string[] keys)
    {
        var reference = CosmosSqlExpression.Property(
            RootAlias,
            CosmosRelationQuerySourceSelectors.RequirePropertyPath(sourceSelector, nameof(sourceSelector)));
        List<CosmosSqlExpression> terms = new(keys.Length + 1)
        {
            CosmosSqlExpression.Function(
                CosmosSqlFunction.ArrayContains,
                CosmosSqlExpression.Parameter(keys),
                reference)
        };
        foreach (var key in keys)
        {
            terms.Add(
                CosmosSqlExpression.Function(
                    CosmosSqlFunction.ArrayContains,
                    reference,
                    CosmosSqlExpression.Parameter(key)));
        }
        return CombineOr(terms, offset: 0, terms.Count);

        static CosmosSqlExpression CombineOr(
            IReadOnlyList<CosmosSqlExpression> expressions,
            int offset,
            int count)
        {
            if (count == 1)
                return expressions[offset];
            var leftCount = count / 2;
            return CosmosSqlExpression.Binary(
                CosmosSqlBinaryOperator.Or,
                CombineOr(expressions, offset, leftCount),
                CombineOr(expressions, offset + leftCount, count - leftCount));
        }
    }

    ProjectionPlan CreateProjection(RelationQuerySourceReadRequest request, string? relationshipSelector)
    {
        Dictionary<string, (string Alias, FieldPath Path)> selectorBindings = new(StringComparer.Ordinal);
        var identityAlias = RegisterSelector(IdentitySourceSelector, IdentityAlias);
        var fieldAliases = ImmutableArray.CreateBuilder<string>(request.Fields.Length);
        for (var index = 0; index < request.Fields.Length; index++)
            fieldAliases.Add(RegisterSelector(request.Fields[index].SourceSelector, $"_field{index}"));
        var relationshipAlias = relationshipSelector is null
            ? null
            : RegisterSelector(relationshipSelector, RelationshipAlias);
        var partitionAlias = Policy.FixedPartitionKey is null
            ? RegisterSelector(Policy.PartitionSourceSelector, PartitionAlias)
            : null;

        var properties = ImmutableArray.CreateBuilder<CosmosSqlObjectProperty>(selectorBindings.Count);
        var aliases = ImmutableArray.CreateBuilder<string>(selectorBindings.Count);
        AddProperty(IdentitySourceSelector, IdentityAlias);
        for (var index = 0; index < request.Fields.Length; index++)
            AddProperty(request.Fields[index].SourceSelector, $"_field{index}");
        if (relationshipSelector is not null)
            AddProperty(relationshipSelector, RelationshipAlias);
        if (Policy.FixedPartitionKey is null)
            AddProperty(Policy.PartitionSourceSelector, PartitionAlias);
        var immutableProperties = properties.MoveToImmutable();
        return new(
            CosmosSqlExpression.ObjectFromImmutable(immutableProperties),
            identityAlias,
            fieldAliases.MoveToImmutable(),
            relationshipAlias,
            partitionAlias,
            aliases.MoveToImmutable());

        string RegisterSelector(string selector, string preferredAlias)
        {
            if (selectorBindings.TryGetValue(selector, out var existing))
                return existing.Alias;
            var path = CosmosRelationQuerySourceSelectors.RequirePropertyPath(selector, nameof(selector));
            selectorBindings.Add(selector, (preferredAlias, path));
            return preferredAlias;
        }

        void AddProperty(string selector, string preferredAlias)
        {
            var binding = selectorBindings[selector];
            if (!string.Equals(binding.Alias, preferredAlias, StringComparison.Ordinal))
                return;
            properties.Add(new(binding.Alias, CosmosSqlExpression.Property(RootAlias, binding.Path)));
            aliases.Add(binding.Alias);
        }
    }

    MaterializationResult MaterializeRows(
        RelationQuerySourceReadRequest request,
        ProjectionPlan projection,
        ImmutableArray<JsonElement> documents,
        string? providerEvidenceReference,
        CancellationToken cancellationToken)
    {
        var rows = ImmutableArray.CreateBuilder<MaterializedRow>(documents.Length);
        HashSet<string> allowedAliases = new(projection.Aliases, StringComparer.Ordinal);
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.ValueKind != JsonValueKind.Object)
                return new([], "projected-row-not-object");

            Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
            foreach (var property in document.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!allowedAliases.Contains(property.Name))
                    return new([], "projected-row-unexpected-alias");
                if (!values.TryAdd(property.Name, property.Value))
                    return new([], "projected-row-duplicate-alias");
            }
            if (!values.TryGetValue(projection.IdentityAlias, out var identity)
                || identity.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(identity.GetString()))
            {
                return new([], "observation-identity-invalid");
            }

            var fields = ImmutableArray.CreateBuilder<RelationQuerySourceReadFieldResult>(request.Fields.Length);
            for (var index = 0; index < request.Fields.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fields.Add(ProjectField(
                    request,
                    request.Fields[index],
                    projection.FieldAliases[index],
                    providerEvidenceReference,
                    values));
            }

            ObservationValue? relationship = null;
            if (projection.RelationshipAlias is { } relationshipAlias)
            {
                relationship = values.TryGetValue(relationshipAlias, out var reference)
                    ? ObservationValue.FromJsonElement(reference)
                    : ObservationValue.Undefined;
            }

            string? partitionToken = null;
            if (projection.PartitionAlias is { } partitionAlias)
            {
                if (!values.TryGetValue(partitionAlias, out var partition)
                    || partition.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Undefined)
                {
                    return new([], "partition-coordinate-invalid");
                }
                partitionToken = partition.GetRawText();
            }

            var observation = new RelationQuerySourceReadObservation(
                identity.GetString()!,
                request.Shape,
                fields.MoveToImmutable());
            rows.Add(new(
                observation,
                relationship,
                partitionToken,
                projection.RelationshipAlias is null
                    ? null
                    : Signature(projection.Aliases, values)));
        }
        return new(rows.MoveToImmutable(), FailureReason: null);
    }

    RelationQuerySourceReadFieldResult ProjectField(
        RelationQuerySourceReadRequest request,
        RelationQuerySourceReadField field,
        string alias,
        string? providerEvidenceReference,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        var evidence = Evidence(
            request,
            $"field/{Uri.EscapeDataString(field.SemanticPath.ToString())}",
            providerEvidenceReference);
        if (!values.TryGetValue(alias, out var element) || element.ValueKind == JsonValueKind.Undefined)
        {
            return new(
                field,
                RelationQuerySourceReadFieldState.Missing,
                evidenceReference: evidence);
        }
        if (element.ValueKind == JsonValueKind.Null)
            return new(field, RelationQuerySourceReadFieldState.Null, evidenceReference: evidence);
        try
        {
            var value = ObservationValue.FromJsonElement(element);
            return value.Kind switch
            {
                ObservationValueKind.Undefined => new(
                    field,
                    RelationQuerySourceReadFieldState.Missing,
                    evidenceReference: evidence),
                ObservationValueKind.Null => new(
                    field,
                    RelationQuerySourceReadFieldState.Null,
                    evidenceReference: evidence),
                _ => new(field, RelationQuerySourceReadFieldState.Value, value, evidence)
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not OutOfMemoryException
                                          and not StackOverflowException)
        {
            return new(
                field,
                RelationQuerySourceReadFieldState.Failed,
                evidenceReference: evidence);
        }
    }

    async ValueTask<CosmosJsonQueryFeedReadResult> ReadFeedAsync(
        CosmosSqlStatement statement,
        long maximumRows,
        CosmosRelationQueryMaterializationAccumulator? accumulator,
        CancellationToken cancellationToken)
    {
        var maximumSdkRows = checked((int)Math.Min(int.MaxValue, maximumRows));
        QueryRequestOptions options = new()
        {
            MaxItemCount = Math.Min(Policy.MaximumSdkPageSize, maximumSdkRows),
            MaxBufferedItemCount = maximumSdkRows,
            MaxConcurrency = checked((int)Math.Min(int.MaxValue, source.Limits.MaximumConcurrency)),
            ConsistencyLevel = Policy.ReadConsistencyLevel
        };
        if (Policy.FixedPartitionKey is { } partitionKey)
            options.PartitionKey = partitionKey;
        var request = feedReader.Prepare(
            statement.ToQueryDefinition(),
            options,
            Policy.RequestSizeLimits);
        Action<double, HttpStatusCode>? completedPageObserver = accumulator is null
            ? null
            : accumulator.Observe;
        var result = await feedReader.ReadAllAsync(
            request,
            maximumRows,
            cancellationToken,
            completedPageObserver).ConfigureAwait(false);
        accumulator?.ObserveEvidence(result.ProviderEvidenceReference);
        return result;
    }

    string? ValidateRequest(
        RelationQuerySourceReadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Source != source.Id)
            return "source-mismatch";
        if (request.Shape != Shape)
            return "shape-mismatch";
        if (!string.Equals(request.IdentitySelector, IdentitySourceSelector, StringComparison.Ordinal))
            return "identity-selector-mismatch";

        try
        {
            CosmosRelationQuerySourceSelectors.RequirePropertyPath(
                request.IdentitySelector,
                nameof(request.IdentitySelector));
            foreach (var field in request.Fields)
            {
                var valid = field.Purpose switch
                {
                    RelationQuerySourceReadFieldPurpose.SemanticInput =>
                        string.Equals(
                            field.SourceSelector,
                            FieldSourceSelector(field.SemanticPath),
                            StringComparison.Ordinal),
                    RelationQuerySourceReadFieldPurpose.Correlation =>
                        string.Equals(
                            field.SourceSelector,
                            RelationshipKeySourceSelector(field.SemanticPath),
                            StringComparison.Ordinal),
                    RelationQuerySourceReadFieldPurpose.SemanticInputAndCorrelation =>
                        string.Equals(
                            field.SourceSelector,
                            FieldSourceSelector(field.SemanticPath),
                            StringComparison.Ordinal)
                        && string.Equals(
                            field.SourceSelector,
                            RelationshipKeySourceSelector(field.SemanticPath),
                            StringComparison.Ordinal),
                    _ => false
                };
                if (!valid)
                    return "field-selector-mismatch";
                CosmosRelationQuerySourceSelectors.RequirePropertyPath(
                    field.SourceSelector,
                    nameof(field.SourceSelector));
            }

            if (request.Constraint is RelationQueryRelationshipKeyBatchLookup relationship)
            {
                if (!string.Equals(
                        relationship.SourceSelector,
                        RelationshipKeySourceSelector(relationship.RelationshipReference),
                        StringComparison.Ordinal))
                {
                    return "relationship-selector-mismatch";
                }
                CosmosRelationQuerySourceSelectors.RequirePropertyPath(
                    relationship.SourceSelector,
                    nameof(relationship.SourceSelector));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return "selector-policy-failed";
        }
        return null;
    }

    bool BatchBoundaryExceeded(RelationQuerySourceReadConstraint constraint) => constraint switch
    {
        RelationQueryIdentityBatchLookup identity =>
            (long)identity.Identities.Length > source.Limits.MaximumBatchSize,
        RelationQueryRelationshipKeyBatchLookup relationship =>
            (long)relationship.Keys.Length > source.Limits.MaximumBatchSize,
        _ => false
    };

    IEnumerable<string[]> Chunks(ImmutableArray<string> keys)
    {
        for (var offset = 0; offset < keys.Length;)
        {
            var count = Math.Min(Policy.MaximumKeysPerQuery, keys.Length - offset);
            var chunk = new string[count];
            keys.AsSpan(offset, count).CopyTo(chunk);
            yield return chunk;
            offset += count;
        }
    }

    long MaximumBufferedRows(RelationQuerySourceReadRequest request) =>
        Math.Min(request.MaximumBufferedRows, source.Limits.MaximumBufferedRows);

    static long ProbeLimit(long maximumRows) => checked(maximumRows + 1);

    bool HasDuplicateIdentity(
        ImmutableArray<MaterializedRow> rows,
        bool allowRepeatedPhysicalRows,
        out string? reason)
    {
        Dictionary<string, PhysicalOccurrence> occurrences = new(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!TryAddOccurrence(
                    row,
                    chunkIndex: 0,
                    allowRepeatedPhysicalRows,
                    occurrences,
                    out reason))
            {
                return true;
            }
        }
        reason = null;
        return false;
    }

    static bool TryAddOccurrence(
        MaterializedRow row,
        int chunkIndex,
        bool allowRepeatedPhysicalRows,
        Dictionary<string, PhysicalOccurrence> occurrences,
        out string? failureReason)
    {
        var identity = row.Observation.Identity;
        var occurrence = new PhysicalOccurrence(row.PartitionToken, chunkIndex, row.Signature);
        if (occurrences.TryAdd(identity, occurrence))
        {
            failureReason = null;
            return true;
        }

        var existing = occurrences[identity];
        if (!string.Equals(existing.PartitionToken, occurrence.PartitionToken, StringComparison.Ordinal))
        {
            failureReason = "duplicate-observation-identity-across-partitions";
            return false;
        }
        if (!allowRepeatedPhysicalRows || existing.ChunkIndex == chunkIndex)
        {
            failureReason = "duplicate-observation-identity";
            return false;
        }
        if (!string.Equals(existing.Signature, occurrence.Signature, StringComparison.Ordinal))
        {
            failureReason = "observation-changed-between-query-chunks";
            return false;
        }

        failureReason = null;
        return true;
    }

    static string Signature(
        ImmutableArray<string> aliases,
        IReadOnlyDictionary<string, JsonElement> values) =>
        string.Join(
            "\u001f",
            aliases.Select(alias => values.TryGetValue(alias, out var value)
                ? string.Concat(alias, "=", value.GetRawText())
                : string.Concat(alias, "=<missing>")));

    RelationQuerySourceReadResult Failed(
        RelationQuerySourceReadRequest request,
        string reason,
        string? providerEvidence = null) => new(
        RelationQuerySourceReadState.Failed,
        evidenceReference: Evidence(request, reason, providerEvidence));

    RelationQuerySourceReadResult Inconclusive(
        RelationQuerySourceReadRequest request,
        string reason,
        string? providerEvidence = null) => new(
        RelationQuerySourceReadState.Inconclusive,
        evidenceReference: Evidence(request, reason, providerEvidence));

    RelationQuerySourceReadResult NotFound(
        RelationQuerySourceReadRequest request,
        string reason,
        string? providerEvidence = null) => new(
        RelationQuerySourceReadState.NotFound,
        evidenceReference: Evidence(request, reason, providerEvidence));

    string Evidence(
        RelationQuerySourceReadRequest request,
        string reason,
        string? providerEvidence) => string.Concat(
        EvidencePrefix,
        "/account/sha256/",
        accountFingerprint,
        "/database/",
        Uri.EscapeDataString(DatabaseId),
        "/container/",
        Uri.EscapeDataString(ContainerId),
        "/document-kind/",
        Uri.EscapeDataString(EntityDocumentKind),
        "/persisted-observation-type/",
        Uri.EscapeDataString(PersistedObservationType.GraphId.Value),
        "/",
        Uri.EscapeDataString(PersistedObservationType.ShapeId.Value),
        "/persisted-stream/",
        Uri.EscapeDataString(PersistedStreamName ?? "none"),
        "/source/",
        Uri.EscapeDataString(source.Id.Value),
        "/physical-plan/",
        Uri.EscapeDataString(request.PhysicalPlan.Algorithm),
        "/",
        Uri.EscapeDataString(request.PhysicalPlan.Canonicalization),
        "/",
        Uri.EscapeDataString(request.PhysicalPlan.Value),
        "/stage/",
        Uri.EscapeDataString(request.Stage.Value),
        "/placement-binding/",
        Uri.EscapeDataString(request.PlacementBinding.Value),
        "/",
        reason,
        providerEvidence is null
            ? string.Empty
            : $"/provider/{Uri.EscapeDataString(providerEvidence)}");

    static string? CombineProviderEvidence(string? accumulated, string? next)
    {
        if (next is null)
            return accumulated;
        if (accumulated is null)
            return next;

        var canonical = string.Concat(
            accumulated.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ":",
            accumulated,
            next.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ":",
            next);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"cosmos-source-feed-chain/v1/sha256/{Convert.ToHexStringLower(digest)}";
    }

    static double AddUnaccountedResponseCharge(
        double accumulated,
        double? responseCharge,
        bool responseChargeAccounted)
    {
        if (responseChargeAccounted || responseCharge is null)
        {
            return accumulated;
        }

        var total = accumulated + responseCharge.Value;
        if (!double.IsFinite(total) || total < 0)
        {
            throw new InvalidOperationException(
                "Cosmos materialization request charge overflowed its finite range.");
        }

        return total;
    }

    static Container ValidateContainer(Container container, string databaseId, string containerId)
    {
        ArgumentNullException.ThrowIfNull(container);
        var normalizedDatabase = Guard.RequireNotNullOrWhiteSpace(databaseId);
        var normalizedContainer = Guard.RequireNotNullOrWhiteSpace(containerId);
        if (!string.Equals(container.Id, normalizedContainer, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Cosmos SDK container '{container.Id}' does not match declared container '{normalizedContainer}'.",
                nameof(containerId));
        }
        if (!string.Equals(container.Database.Id, normalizedDatabase, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Cosmos SDK database '{container.Database.Id}' does not match declared database '{normalizedDatabase}'.",
                nameof(databaseId));
        }
        return container;
    }

    static RelationQueryTargetCapabilityProfile CreateTargetProfile()
    {
        return new(
            new("cohesive.adapters.cosmos.entity-source"),
            new("cohesive.adapters.cosmos.entity-source/v1"),
            [RelationQueryDocument.CurrentSchemaVersion],
            [RelationQueryCompilationProvenance.CurrentCompilerProfile],
            [
                Capability(RelationQueryPrimitiveCapabilityKind.BatchedKeyLookup),
                Capability(RelationQueryPrimitiveCapabilityKind.BatchedPredicateLookup),
                Capability(RelationQueryPrimitiveCapabilityKind.CompleteSetEnumeration),
                Capability(RelationQueryPrimitiveCapabilityKind.FieldProjection),
                Capability(RelationQueryPrimitiveCapabilityKind.ObservationIdentityRead),
                Capability(RelationQueryPrimitiveCapabilityKind.RelationshipReferenceRead)
            ],
            description: "Bounded canonical acquisition over one Cosmos entity-document container.");

        static RelationQueryTargetCapabilityEvidence Capability(
            RelationQueryPrimitiveCapabilityKind capability) => new(
            new($"cohesive.adapters.cosmos.entity-source/capability/{(int)capability}"),
            new PrimitiveRelationQueryCapability(capability));
    }

    sealed record ProjectionPlan(
        CosmosSqlExpression Expression,
        string IdentityAlias,
        ImmutableArray<string> FieldAliases,
        string? RelationshipAlias,
        string? PartitionAlias,
        ImmutableArray<string> Aliases);

    sealed record MaterializedRow(
        RelationQuerySourceReadObservation Observation,
        ObservationValue? RelationshipReference,
        string? PartitionToken,
        string? Signature);

    readonly record struct MaterializationResult(
        ImmutableArray<MaterializedRow> Rows,
        string? FailureReason);

    readonly record struct PhysicalOccurrence(
        string? PartitionToken,
        int ChunkIndex,
        string? Signature);

    sealed class CosmosRelationQueryMaterializationAccumulator
    {
        internal double RequestCharge { get; private set; }

        internal HttpStatusCode? StatusCode { get; private set; }

        internal string? ProviderEvidenceReference { get; private set; }

        internal void Observe(double requestCharge, HttpStatusCode statusCode)
        {
            RequestCharge += requestCharge;
            if (!double.IsFinite(RequestCharge) || RequestCharge < 0)
                throw new InvalidOperationException("Cosmos materialization request charge overflowed its finite range.");
            StatusCode = statusCode;
        }

        internal void ObserveEvidence(string? providerEvidenceReference) =>
            ProviderEvidenceReference = CombineProviderEvidence(
                ProviderEvidenceReference,
                providerEvidenceReference);
    }
}

/// <summary>One buffered canonical Relations acquisition with aggregate Cosmos provider evidence.</summary>
internal sealed record CosmosRelationQueryMaterializationRead
{
    /// <summary>Creates one buffered materialization read.</summary>
    /// <param name="read">Canonical Relations source result.</param>
    /// <param name="requestCharge">Request units aggregated across completed provider pages and query chunks.</param>
    /// <param name="statusCode">HTTP status from the final completed provider page, when I/O occurred.</param>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="requestCharge"/> is negative or non-finite.</exception>
    internal CosmosRelationQueryMaterializationRead(
        RelationQuerySourceReadResult read,
        double requestCharge,
        HttpStatusCode? statusCode)
    {
        Read = Guard.RequireNotNull(read);
        if (!double.IsFinite(requestCharge) || requestCharge < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestCharge),
                requestCharge,
                "A buffered Cosmos materialization request charge must be finite and non-negative.");
        }
        RequestCharge = requestCharge;
        StatusCode = statusCode;
    }

    /// <summary>Canonical Relations source result.</summary>
    internal RelationQuerySourceReadResult Read { get; }

    /// <summary>Request units aggregated across completed provider pages and query chunks.</summary>
    internal double RequestCharge { get; }

    /// <summary>HTTP status from the final completed provider page, when I/O occurred.</summary>
    internal HttpStatusCode? StatusCode { get; }
}

/// <summary>Provider failure retaining request charge from buffered query chunks completed before the failure.</summary>
internal sealed class CosmosRelationQueryMaterializationProviderException : Exception
{
    internal CosmosRelationQueryMaterializationProviderException(
        CosmosException providerException,
        double completedRequestCharge)
        : base("A buffered Cosmos materialization acquisition failed.", Guard.RequireNotNull(providerException))
    {
        if (!double.IsFinite(completedRequestCharge) || completedRequestCharge < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedRequestCharge),
                completedRequestCharge,
                "Completed Cosmos request charge must be finite and non-negative.");
        }
        ProviderException = providerException;
        CompletedRequestCharge = completedRequestCharge;
    }

    internal CosmosException ProviderException { get; }

    internal double CompletedRequestCharge { get; }
}

/// <summary>Provider-protocol failure retaining evidence from all completed buffered responses.</summary>
internal sealed class CosmosRelationQueryMaterializationProtocolException : Exception
{
    internal CosmosRelationQueryMaterializationProtocolException(
        CosmosProviderProtocolException providerException,
        double completedRequestCharge,
        HttpStatusCode? completedStatusCode,
        string? providerEvidenceReference)
        : base(
            "A buffered Cosmos materialization acquisition received an invalid provider response.",
            Guard.RequireNotNull(providerException))
    {
        if (!double.IsFinite(completedRequestCharge) || completedRequestCharge < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedRequestCharge),
                completedRequestCharge,
                "Completed Cosmos request charge must be finite and non-negative.");
        }
        if (providerEvidenceReference is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(providerEvidenceReference);
        ProviderException = providerException;
        CompletedRequestCharge = completedRequestCharge;
        CompletedStatusCode = completedStatusCode;
        ProviderEvidenceReference = providerEvidenceReference;
    }

    internal CosmosProviderProtocolException ProviderException { get; }

    internal double CompletedRequestCharge { get; }

    internal HttpStatusCode? CompletedStatusCode { get; }

    internal string? ProviderEvidenceReference { get; }
}

/// <summary>Caller cancellation retaining evidence from buffered provider responses completed before cancellation.</summary>
internal sealed class CosmosRelationQueryMaterializationCanceledException : OperationCanceledException
{
    internal CosmosRelationQueryMaterializationCanceledException(
        OperationCanceledException cancellation,
        double completedRequestCharge,
        HttpStatusCode? completedStatusCode,
        string? providerEvidenceReference,
        CancellationToken cancellationToken)
        : base(
            "A buffered Cosmos materialization acquisition was canceled after one or more provider responses may have completed.",
            Guard.RequireNotNull(cancellation),
            cancellationToken)
    {
        if (!double.IsFinite(completedRequestCharge) || completedRequestCharge < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedRequestCharge),
                completedRequestCharge,
                "Completed Cosmos request charge must be finite and non-negative.");
        }
        if (providerEvidenceReference is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(providerEvidenceReference);
        CompletedRequestCharge = completedRequestCharge;
        CompletedStatusCode = completedStatusCode;
        ProviderEvidenceReference = providerEvidenceReference;
    }

    internal double CompletedRequestCharge { get; }

    internal HttpStatusCode? CompletedStatusCode { get; }

    internal string? ProviderEvidenceReference { get; }
}

/// <summary>One complete provider page projected through the canonical Cosmos Relations source semantics.</summary>
internal sealed record CosmosRelationQueryMaterializationPage
{
    /// <summary>Creates a projected materialization page.</summary>
    /// <param name="read">Canonical Relations result for the complete provider page.</param>
    /// <param name="nextContinuationToken">Opaque provider continuation, or <see langword="null"/> at exhaustion.</param>
    /// <param name="hasMoreResults">Whether another provider page is resumable.</param>
    /// <param name="requestCharge">Non-negative Cosmos request charge.</param>
    /// <param name="statusCode">Successful provider status.</param>
    /// <param name="providerEvidenceReference">Optional non-sensitive provider evidence digest.</param>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Continuation, progress, charge, or evidence invariants conflict.</exception>
    internal CosmosRelationQueryMaterializationPage(
        RelationQuerySourceReadResult read,
        string? nextContinuationToken,
        bool hasMoreResults,
        double requestCharge,
        HttpStatusCode statusCode,
        string? providerEvidenceReference)
    {
        Read = Guard.RequireNotNull(read);
        if (nextContinuationToken is not null && string.IsNullOrWhiteSpace(nextContinuationToken))
            throw new ArgumentException("A provider continuation cannot be empty.", nameof(nextContinuationToken));
        if (hasMoreResults != (nextContinuationToken is not null))
            throw new ArgumentException("Provider progress requires exactly one non-empty continuation.", nameof(hasMoreResults));
        if (!double.IsFinite(requestCharge) || requestCharge < 0)
            throw new ArgumentOutOfRangeException(nameof(requestCharge), requestCharge, "A request charge must be finite and non-negative.");
        if (providerEvidenceReference is not null && string.IsNullOrWhiteSpace(providerEvidenceReference))
            throw new ArgumentException("Provider evidence cannot be empty.", nameof(providerEvidenceReference));
        NextContinuationToken = nextContinuationToken;
        HasMoreResults = hasMoreResults;
        RequestCharge = requestCharge;
        StatusCode = statusCode;
        ProviderEvidenceReference = providerEvidenceReference;
    }

    /// <summary>Canonical Relations result for the complete provider page.</summary>
    internal RelationQuerySourceReadResult Read { get; }

    /// <summary>Opaque provider continuation, or <see langword="null"/> at exhaustion.</summary>
    internal string? NextContinuationToken { get; }

    /// <summary>Whether another provider page is resumable.</summary>
    internal bool HasMoreResults { get; }

    /// <summary>Cosmos request charge.</summary>
    internal double RequestCharge { get; }

    /// <summary>Successful provider status.</summary>
    internal HttpStatusCode StatusCode { get; }

    /// <summary>Optional non-sensitive provider evidence digest.</summary>
    internal string? ProviderEvidenceReference { get; }

    /// <summary>Creates a no-I/O page from canonical validation evidence.</summary>
    /// <param name="read">Failed canonical Relations result.</param>
    /// <returns>A terminal zero-charge page.</returns>
    internal static CosmosRelationQueryMaterializationPage FromRead(RelationQuerySourceReadResult read) =>
        new(read, null, hasMoreResults: false, requestCharge: 0, HttpStatusCode.OK, providerEvidenceReference: null);
}
