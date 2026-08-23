using System.Net;
using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Model;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Cosmos DB-backed observation repository with optional atomic outbox support.
/// </summary>
public sealed class CosmosEntityOutboxRepository : IEntityOutboxRepository
{
    /// <summary>
    /// Largest observation version this repository can persist while retaining exact Cosmos SQL numeric semantics.
    /// </summary>
    public const long MaximumExactObservationVersion = CosmosRelationQueryTargetProfile.MaximumExactInteger;

    readonly EntityDefinition entityDefinition;
    readonly string observationType;
    readonly Container container;
    readonly EntityPartitionKeyPolicy partitionKeyPolicy;
    readonly Func<Observation, string> itemIdSelector;
    readonly CosmosObservationOutboxRepositoryOptions options;

    /// <summary>
    /// Creates a repository for one entity definition persisted in observation format.
    /// </summary>
    /// <param name="entityDefinition">Entity shape and semantic identity persisted by this repository.</param>
    /// <param name="container">Cosmos container containing entity and optional outbox documents.</param>
    /// <param name="partitionKeySelector">
    /// Legacy observation-to-partition selector, or <see langword="null"/> to use <paramref name="partitionKeyPolicy"/>
    /// or the entity-identity convention.
    /// </param>
    /// <param name="pointReadPartitionKeySelector">
    /// Legacy entity-identity-to-partition selector, or <see langword="null"/> when unavailable.
    /// </param>
    /// <param name="itemIdSelector">Observation-to-item-id selector, or <see langword="null"/> for the default.</param>
    /// <param name="options">Repository persistence options, or <see langword="null"/> for conventions.</param>
    /// <param name="mappingContext">Object/observation mapping context, or <see langword="null"/> for the default.</param>
    /// <param name="partitionKeyPolicy">
    /// Explicit read/write partition policy. It is mutually exclusive with either legacy partition selector.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entityDefinition"/> or <paramref name="container"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Explicit and legacy partition policies are combined, the legacy selectors are incomplete, or the entity and
    /// outbox document discriminators are empty or equal.
    /// </exception>
    public CosmosEntityOutboxRepository(
        EntityDefinition entityDefinition,
        Container container,
        Func<Observation, string>? partitionKeySelector = null,
        Func<string, string?>? pointReadPartitionKeySelector = null,
        Func<Observation, string>? itemIdSelector = null,
        CosmosObservationOutboxRepositoryOptions? options = null,
        ShapeMappingContext? mappingContext = null,
        EntityPartitionKeyPolicy? partitionKeyPolicy = null
        )
    {
        this.entityDefinition = Guard.RequireNotNull(entityDefinition);
        this.observationType = entityDefinition.Shape.Id.Value;
        this.container = Guard.RequireNotNull(container);
        this.partitionKeyPolicy = ResolvePartitionKeyPolicy(
            partitionKeyPolicy,
            partitionKeySelector,
            pointReadPartitionKeySelector);
        this.itemIdSelector = itemIdSelector ?? DefaultItemIdSelector;
        this.options = CosmosObservationOutboxRepositoryOptions.RequireValid(options ?? new());
        MappingContext = mappingContext ?? ShapeMappingContext.Default;
    }

    /// <summary>
    /// Gets the underlying Cosmos container.
    /// </summary>
    public Container Container => container;

    /// <summary>
    /// Exact persisted entity-document discriminator. Native queries over the shared container must retain this value
    /// as physical source-scope evidence.
    /// </summary>
    public string EntityDocumentKind => options.EntityDocumentKind;

    /// <inheritdoc />
    public EntityDefinition EntityDefinition => entityDefinition;

    /// <inheritdoc />
    public ShapeMappingContext MappingContext { get; }

    /// <inheritdoc />
    public string EntityType => observationType;

    /// <inheritdoc />
    public async Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? readOptions = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        context.ThrowIfCancellationRequested();

        var partitionKey = readOptions?.PartitionKey ?? partitionKeyPolicy.TryResolvePointReadPartitionKey(context, id);
        var document = await QueryEntityDocumentAsync(context, observationId: id, partitionKey: partitionKey).ConfigureAwait(false);
        if (document is null)
            return null;

        ValidateReadPreconditions(observationType, id, document, readOptions);

        return new(
            Entity: ProjectObservation(document, readOptions?.Fields),
            PartitionKey: document.PartitionKey,
            ConcurrencyToken: new(Guard.RequireNotNullOrWhiteSpace(document.ETag)),
            LoadedFields: readOptions?.Fields
            );
    }

    /// <inheritdoc />
    public async Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(write);
        context.ThrowIfCancellationRequested();
        EnsureEntityType(write.Entity);

        var partitionKey = GetPartitionKey(context, write.Entity);
        var document = CreateEntityDocument(context, write.Entity, partitionKey: partitionKey);
        try
        {
            if (write.ExpectedConcurrencyToken is { } expectedConcurrencyToken)
            {
                var replace = await container.ReplaceItemAsync(
                    item: document,
                    id: document.Id,
                    partitionKey: new(partitionKey),
                    requestOptions: new()
                    {
                        IfMatchEtag = expectedConcurrencyToken.Value
                    },
                    cancellationToken: context.CancellationToken
                    ).ConfigureAwait(false);
                return CreateSnapshot(replace.Resource with { ETag = replace.ETag }, partitionKey);
            }

            var upsert = await container.UpsertItemAsync(item: document, partitionKey: new(partitionKey), cancellationToken: context.CancellationToken).ConfigureAwait(false);
            return CreateSnapshot(upsert.Resource with { ETag = upsert.ETag }, partitionKey);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new ObservationConcurrencyConflictException(
                $"Observation '{observationType}:{write.Entity.Id}' failed optimistic concurrency validation.",
                ex
                );
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Observation '{observationType}:{write.Entity.Id}' was not found in partition '{partitionKey}' with token='{write.ExpectedConcurrencyToken}'.",
                ex
                );
        }
    }

    /// <inheritdoc />
    public async Task<EntityCommitResult> UpsertWithOutbox(OperationContext context, EntityOutboxCommit commit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);
        context.ThrowIfCancellationRequested();

        if (commit.Envelopes.IsEmpty)
        {
            var committedSnapshot = await Upsert(context, commit.Write).ConfigureAwait(false);
            return new(committedSnapshot, commit.Envelopes);
        }

        EnsureEntityType(commit.Write.Entity);
        var partitionKey = GetPartitionKey(context, commit.Write.Entity);
        var outboxDocuments = CreateOutboxDocuments(context, commit, partitionKey);

        var entityDocument = CreateEntityDocument(context, commit.Write.Entity, partitionKey);
        var batch = container.CreateTransactionalBatch(new(partitionKey));
        if (commit.Write.ExpectedConcurrencyToken is { } expectedConcurrencyToken)
        {
            batch.ReplaceItem(id: entityDocument.Id, item: entityDocument, requestOptions: new() { IfMatchEtag = expectedConcurrencyToken.Value });
        }
        else
        {
            batch.UpsertItem(entityDocument);
        }

        foreach (var document in outboxDocuments)
            batch.CreateItem(document);

        using var response = await batch.ExecuteAsync(context.CancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if ((response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                && await TryReplayOutboxCommit(context, commit, partitionKey, outboxDocuments).ConfigureAwait(false)
                    is { } racedReplay)
            {
                return racedReplay;
            }

            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                throw new ObservationConcurrencyConflictException($"Observation '{observationType}:{commit.Write.Entity.Id}' failed optimistic concurrency validation inside transactional batch.");

            throw new InvalidOperationException($"Transactional Cosmos observation commit for '{observationType}:{commit.Write.Entity.Id}' failed with status '{response.StatusCode}'.");
        }

        var snapshot = await TryGet(
                context,
                id: commit.Write.Entity.Id,
                readOptions: EntityReadOptions.Full.WithPartitionKey(partitionKey))
            .ConfigureAwait(false);
        if (snapshot is null)
            throw new InvalidOperationException($"Transactional Cosmos observation commit for '{observationType}:{commit.Write.Entity.Id}' succeeded, but the entity could not be reloaded.");

        return new(snapshot, commit.Envelopes);
    }

    /// <summary>
    /// Counts canonical outbox-envelope documents in this repository's container associated with the supplied subject id.
    /// </summary>
    /// <param name="context">Operation context carrying cancellation and attribution.</param>
    /// <param name="subjectId">Non-empty outbox subject identity.</param>
    /// <returns>The number of matching outbox documents returned by Cosmos.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="subjectId"/> is empty or white space.</exception>
    /// <exception cref="OperationCanceledException">The operation context is canceled.</exception>
    /// <exception cref="CosmosException">Cosmos rejects or fails the count query.</exception>
    public async Task<int> CountOutboxEnvelopes(OperationContext context, string subjectId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        context.ThrowIfCancellationRequested();

        var iterator = container.GetItemQueryIterator<int>(
            new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.documentKind = @documentKind AND c.subjectId = @subjectId")
                .WithParameter("@documentKind", options.OutboxDocumentKind)
                .WithParameter("@subjectId", subjectId),
            requestOptions: new QueryRequestOptions
            {
                MaxItemCount = 1
            });

        var total = 0;
        while (iterator.HasMoreResults)
        {
            context.ThrowIfCancellationRequested();
            var page = await iterator.ReadNextAsync(context.CancellationToken).ConfigureAwait(false);
            total += page.Resource.SingleOrDefault();
        }

        return total;
    }

    static string DefaultItemIdSelector(Observation observation) => observation.Id;

    static EntityPartitionKeyPolicy ResolvePartitionKeyPolicy(
        EntityPartitionKeyPolicy? partitionKeyPolicy,
        Func<Observation, string>? partitionKeySelector,
        Func<string, string?>? pointReadPartitionKeySelector
        )
    {
        if (partitionKeyPolicy is not null)
        {
            if (partitionKeySelector is not null || pointReadPartitionKeySelector is not null)
                throw new ArgumentException("Configure either an entity partition-key policy or legacy partition-key selectors, not both.");

            return partitionKeyPolicy;
        }

        if (partitionKeySelector is null)
        {
            return pointReadPartitionKeySelector is null
                ? EntityPartitionKeyPolicy.ObservationId
                : new(
                    description: "observation id",
                    writePartitionKeyResolver: static (_, observation) => observation.Id,
                    pointReadPartitionKeyResolver: (_, id) => pointReadPartitionKeySelector(id)
                    );
        }

        return EntityPartitionKeyPolicy.FromObservation(
            partitionKeySelector,
            pointReadPartitionKeySelector: pointReadPartitionKeySelector
            );
    }

    void EnsureEntityType(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!string.Equals(observation.ShapeId.Value, observationType, StringComparison.Ordinal))
            throw new SemanticRuleViolationException($"Repository for '{observationType}' cannot persist observation '{observation.ShapeId.Value}:{observation.Id}'.");
    }

    string GetPartitionKey(OperationContext context, Observation observation)
    {
        try
        {
            return partitionKeyPolicy.ResolveWritePartitionKey(context, observation);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Observation '{observationType}:{observation.Id}' did not resolve a partition key from {partitionKeyPolicy.Description}.",
                ex);
        }
    }

    async Task<CosmosObservationContainerDocument?> QueryEntityDocumentAsync(OperationContext context, string observationId, string? partitionKey)
    {
        var queryText = new StringBuilder(
            """
            SELECT TOP 2 * FROM c
            WHERE c.documentKind = @documentKind
              AND c.observationType = @observationType
              AND c.observationId = @observationId
            """);
        var query = new QueryDefinition(queryText.ToString())
            .WithParameter("@documentKind", options.EntityDocumentKind)
            .WithParameter("@observationType", observationType)
            .WithParameter("@observationId", observationId);

        QueryRequestOptions requestOptions = new() { MaxItemCount = 2 };
        if (!string.IsNullOrWhiteSpace(partitionKey))
        {
            queryText.AppendLine("  AND c.partitionKey = @partitionKey");
            query = new QueryDefinition(queryText.ToString())
                .WithParameter("@documentKind", options.EntityDocumentKind)
                .WithParameter("@observationType", observationType)
                .WithParameter("@observationId", observationId)
                .WithParameter("@partitionKey", partitionKey);
            requestOptions.PartitionKey = new(partitionKey);
        }

        var iterator = container.GetItemQueryIterator<CosmosObservationContainerDocument>(query, requestOptions: requestOptions);
        List<CosmosObservationContainerDocument> matches = [];
        while (iterator.HasMoreResults && matches.Count < 2)
        {
            context.ThrowIfCancellationRequested();
            var page = await iterator.ReadNextAsync(context.CancellationToken).ConfigureAwait(false);
            matches.AddRange(page);
        }

        if (matches.Count == 0)
            return null;

        if (matches.Count > 1)
            throw new InvalidOperationException($"Observation '{observationType}:{observationId}' exists in multiple partitions and cannot be loaded by id alone.");

        return matches[0];
    }

    static EntitySnapshot CreateSnapshot(CosmosObservationContainerDocument document, string partitionKey) => new(
        Entity: BuildObservation(document),
        PartitionKey: partitionKey,
        ConcurrencyToken: new(Guard.RequireNotNullOrWhiteSpace(document.ETag))
        );

    static Observation BuildObservation(CosmosObservationContainerDocument document)
    {
        if (document.Observation is null)
            throw new InvalidOperationException($"Cosmos document '{document.Id}' does not contain a serialized observation body.");

        return new(
            shapeId: new(document.ObservationType),
            id: document.ObservationId,
            fields: document.Observation,
            version: document.ObservationVersion
            );
    }

    internal static void ValidateReadPreconditions(
        string entityType,
        string id,
        CosmosObservationContainerDocument document,
        EntityReadOptions? read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(document);

        if (read?.ExpectedVersion is { } expectedVersion && document.ObservationVersion != expectedVersion)
        {
            throw new ObservationConcurrencyConflictException($"Observation '{entityType}:{id}' expected version '{expectedVersion}' but found '{document.ObservationVersion}'.");
        }

        if (read?.ExpectedConcurrencyToken is { } expectedConcurrencyToken && !string.Equals(expectedConcurrencyToken.Value, document.ETag, StringComparison.Ordinal))
        {
            var found = document.ETag ?? "<null>";
            throw new ObservationConcurrencyConflictException(
                $"Observation '{entityType}:{id}' expected ETag '{expectedConcurrencyToken.Value}' but found '{found}'.");
        }
    }

    static Observation ProjectObservation(CosmosObservationContainerDocument document, IReadOnlySet<string>? fields)
    {
        var observation = BuildObservation(document);
        if (fields is null)
            return observation;

        Dictionary<string, ObservationValue> projected = new(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (observation.Fields.TryGetValue(field, out var value))
                projected[field] = value;
        }

        return new(
            shapeId: observation.ShapeId,
            id: observation.Id,
            fields: projected,
            version: observation.Version
            );
    }

    CosmosObservationContainerDocument CreateEntityDocument(OperationContext context, Observation observation, string partitionKey)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ValidateObservationVersion(observation.Version);
        return new(
            Id: itemIdSelector(observation),
            PartitionKey: partitionKey,
            DocumentKind: options.EntityDocumentKind,
            ObservationType: observation.ShapeId.Value,
            ObservationId: observation.Id,
            ObservationVersion: observation.Version,
            Observation: observation.Fields as Dictionary<string, ObservationValue> ?? new Dictionary<string, ObservationValue>(observation.Fields, StringComparer.Ordinal),
            StreamName: null,
            SubjectType: null,
            SubjectId: null,
            SubjectVersion: null,
            CorrelationId: null,
            OccurredAtUtc: context.UtcNow,
            TraceId: GetTraceId(context),
            SpanId: GetSpanId(context)
            );
    }

    internal CosmosObservationContainerDocument[] CreateOutboxDocuments(
        OperationContext context,
        EntityOutboxCommit commit,
        string partitionKey)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ValidateObservationVersion(commit.Write.Entity.Version);

        var documents = new CosmosObservationContainerDocument[commit.Envelopes.Length];
        for (var index = 0; index < commit.Envelopes.Length; index++)
        {
            var envelope = commit.Envelopes[index];
            var origin = (TransitionInteractionOrigin)envelope.Context.Origin;
            var content = GetContent(envelope);
            var canonicalBytes = InteractionEnvelopeJsonSerializer.GetCanonicalBytes(
                envelope,
                out var envelopeFingerprint);
            using var canonicalDocument = JsonDocument.Parse(canonicalBytes);
            documents[index] = new(
                Id: envelope.Context.EmissionId.Value,
                PartitionKey: partitionKey,
                DocumentKind: options.OutboxDocumentKind,
                ObservationType: origin.Entity.EntityType.Value,
                ObservationId: origin.Entity.EntityId.Value,
                ObservationVersion: commit.Write.Entity.Version,
                Observation: ProjectPayload(content.Payload),
                StreamName: content.Contract.Definition.DefinitionId.Value,
                SubjectType: origin.Entity.EntityType.Value,
                SubjectId: origin.Entity.EntityId.Value,
                SubjectVersion: commit.Write.Entity.Version,
                CorrelationId: envelope.Context.CorrelationId.Value,
                OccurredAtUtc: context.UtcNow,
                TraceId: GetTraceId(context),
                SpanId: GetSpanId(context),
                Envelope: canonicalDocument.RootElement.Clone(),
                EnvelopeFingerprint: envelopeFingerprint.Value);
        }

        return documents;
    }

    internal static void ValidateObservationVersion(long version)
    {
        if (version is < 0 or > MaximumExactObservationVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                version,
                $"A Cosmos observation version must be between 0 and {MaximumExactObservationVersion} so Cosmos SQL retains it exactly.");
        }
    }

    async Task<EntityCommitResult?> TryReplayOutboxCommit(
        OperationContext context,
        EntityOutboxCommit commit,
        string partitionKey,
        IReadOnlyList<CosmosObservationContainerDocument> candidates)
    {
        var retainedCount = 0;
        foreach (var candidate in candidates)
        {
            CosmosObservationContainerDocument? retained;
            try
            {
                var response = await container.ReadItemAsync<CosmosObservationContainerDocument>(
                        id: candidate.Id,
                        partitionKey: new(partitionKey),
                        cancellationToken: context.CancellationToken)
                    .ConfigureAwait(false);
                retained = response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            retainedCount++;
            if (!string.Equals(retained.DocumentKind, options.OutboxDocumentKind, StringComparison.Ordinal)
                || !string.Equals(
                    retained.EnvelopeFingerprint,
                    candidate.EnvelopeFingerprint,
                    StringComparison.Ordinal)
                || retained.Envelope is not { } retainedEnvelope
                || candidate.Envelope is not { } candidateEnvelope
                || !string.Equals(
                    retainedEnvelope.GetRawText(),
                    candidateEnvelope.GetRawText(),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Cosmos outbox identity '{candidate.Id}' is retained with different canonical content.");
            }
        }

        if (retainedCount == 0)
            return null;

        if (retainedCount != candidates.Count)
        {
            throw new InvalidOperationException(
                "A Cosmos entity outbox retry cannot mix retained and previously unseen emission identities.");
        }

        var snapshot = await TryGet(
                context,
                id: commit.Write.Entity.Id,
                readOptions: EntityReadOptions.Full.WithPartitionKey(partitionKey))
            .ConfigureAwait(false);
        if (snapshot is null || !snapshot.Entity.HasSameContent(commit.Write.Entity))
        {
            throw new InvalidOperationException(
                "The Cosmos outbox emissions are retained, but the candidate entity differs from their atomic commit.");
        }

        return new(snapshot, commit.Envelopes);
    }

    internal static InteractionEnvelope DeserializeOutboxEnvelope(
        CosmosObservationContainerDocument document,
        InteractionContractCatalog contracts)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(contracts);
        if (document.Envelope is not { } element || string.IsNullOrWhiteSpace(document.EnvelopeFingerprint))
            throw new InvalidOperationException($"Cosmos outbox document '{document.Id}' has no canonical envelope evidence.");

        var envelope = InteractionEnvelopeJsonSerializer.Deserialize(element.GetRawText(), contracts);
        var fingerprint = InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(envelope);
        if (!string.Equals(fingerprint.Value, document.EnvelopeFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cosmos outbox document '{document.Id}' does not match its canonical envelope fingerprint.");
        }

        return envelope;
    }

    static (InteractionContractReference Contract, PortableValue Payload) GetContent(
        InteractionEnvelope envelope) => envelope switch
    {
        DomainEventEnvelope domainEvent => (domainEvent.Contract, domainEvent.Payload),
        RequestEnvelope request => (request.Contract, request.Payload),
        _ => throw new InvalidOperationException(
            $"A direct Transition outbox cannot persist envelope kind '{envelope.GetType().Name}'.")
    };

    static Dictionary<string, ObservationValue> ProjectPayload(PortableValue payload)
    {
        if (payload.Value is { Kind: ObservationValueKind.Object, Fields: { } fields })
            return fields as Dictionary<string, ObservationValue>
                ?? new Dictionary<string, ObservationValue>(fields, StringComparer.Ordinal);

        return new(StringComparer.Ordinal)
        {
            ["valueState"] = ObservationValue.FromString(payload.State.ToString()),
            ["value"] = payload.Value ?? ObservationValue.Null
        };
    }

    string? GetTraceId(OperationContext context)
    {
        if (!options.WriteTraceId || !context.TraceContext.HasValue)
            return null;

        var traceContext = context.TraceContext.Value;
        return traceContext.TraceId == default ? null : traceContext.TraceId.ToString();
    }

    string? GetSpanId(OperationContext context)
    {
        if (!options.WriteSpanId || !context.TraceContext.HasValue)
            return null;

        var traceContext = context.TraceContext.Value;
        return traceContext.SpanId == default ? null : traceContext.SpanId.ToString();
    }

}
