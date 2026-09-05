using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Model;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Cosmos DB-backed observation repository with optional atomic outbox support.
/// </summary>
public sealed class CosmosEntityOutboxRepository : IEntityOutboxRepository, IEntityTransitionOperationRepository
{
    internal const string TransitionCommitBrotliEncoding = "br+base64/canonical-json;v=1";
    internal const int MaximumTransitionCommitCanonicalBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Largest observation version this repository can persist while retaining exact Cosmos SQL numeric semantics.
    /// </summary>
    public const long MaximumExactObservationVersion = CosmosRelationQueryTargetProfile.MaximumExactInteger;

    readonly EntityDefinition entityDefinition;
    readonly string observationType;
    readonly Container container;
    readonly EntityPartitionKeyPolicy partitionKeyPolicy;
    readonly Func<EntityObservationSnapshot, string> itemIdSelector;
    readonly CosmosObservationOutboxRepositoryOptions options;
    static readonly JsonSerializerOptions TransitionReceiptJsonOptions = StrictDocumentJson.CreateOptions();

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
        Func<EntityObservationSnapshot, string>? partitionKeySelector = null,
        Func<string, string?>? pointReadPartitionKeySelector = null,
        Func<EntityObservationSnapshot, string>? itemIdSelector = null,
        CosmosObservationOutboxRepositoryOptions? options = null,
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
    public EntityTransitionOperationCapabilities TransitionOperationCapabilities { get; } =
        EntityTransitionOperationCapabilities.AtomicStateAndReceipt;

    /// <inheritdoc />
    public EntityDefinition EntityDefinition => entityDefinition;

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
            Entity: BuildObservation(document),
            PartitionKey: document.PartitionKey,
            ConcurrencyToken: GetConcurrencyToken(document),
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
                var current = await QueryEntityDocumentAsync(
                        context,
                        write.Entity.EntityId.Value,
                        partitionKey)
                    .ConfigureAwait(false);
                RequireExpectedConcurrencyToken(current, expectedConcurrencyToken, write.Entity.EntityId.Value);
                var replace = await container.ReplaceItemAsync(
                    item: document,
                    id: document.Id,
                    partitionKey: new(partitionKey),
                    requestOptions: new()
                    {
                        IfMatchEtag = current!.ETag
                    },
                    cancellationToken: context.CancellationToken
                    ).ConfigureAwait(false);
                return CreateSnapshot(document with { ETag = replace.ETag }, partitionKey);
            }

            var upsert = await container.UpsertItemAsync(item: document, partitionKey: new(partitionKey), cancellationToken: context.CancellationToken).ConfigureAwait(false);
            return CreateSnapshot(document with { ETag = upsert.ETag }, partitionKey);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new ObservationConcurrencyConflictException(
                $"Observation '{observationType}:{write.Entity.EntityId.Value}' failed optimistic concurrency validation.",
                ex
                );
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Observation '{observationType}:{write.Entity.EntityId.Value}' was not found in partition '{partitionKey}' with token='{write.ExpectedConcurrencyToken}'.",
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
            var current = await QueryEntityDocumentAsync(
                    context,
                    commit.Write.Entity.EntityId.Value,
                    partitionKey)
                .ConfigureAwait(false);
            RequireExpectedConcurrencyToken(
                current,
                expectedConcurrencyToken,
                commit.Write.Entity.EntityId.Value);
            batch.ReplaceItem(
                id: entityDocument.Id,
                item: entityDocument,
                requestOptions: new() { IfMatchEtag = current!.ETag });
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
                throw new ObservationConcurrencyConflictException($"Observation '{observationType}:{commit.Write.Entity.EntityId.Value}' failed optimistic concurrency validation inside transactional batch.");

            throw new InvalidOperationException($"Transactional Cosmos observation commit for '{observationType}:{commit.Write.Entity.EntityId.Value}' failed with status '{response.StatusCode}'.");
        }

        var snapshot = await TryGet(
                context,
                id: commit.Write.Entity.EntityId.Value,
                readOptions: EntityReadOptions.Full.WithPartitionKey(partitionKey))
            .ConfigureAwait(false);
        if (snapshot is null)
            throw new InvalidOperationException($"Transactional Cosmos observation commit for '{observationType}:{commit.Write.Entity.EntityId.Value}' succeeded, but the entity could not be reloaded.");

        return new(snapshot, commit.Envelopes);
    }

    /// <inheritdoc />
    public async Task<EntityTransitionOperationResult> TryGetTransitionOperation(
        OperationContext context,
        EntityTransitionOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();

        if (!TryResolveTransitionOperationPartitionKey(context, request, out var partitionKey, out var failure))
            return failure!;
        var retained = await TryReadTransitionReceipt(
                context,
                CreateTransitionOperationReceiptId(request),
                partitionKey!)
            .ConfigureAwait(false);
        if (retained is null)
            return EntityTransitionOperationResult.NotFound();
        return retained.Replay(request);
    }

    /// <inheritdoc />
    public async Task<EntityTransitionOperationResult> TryGetCreationTransitionOperation(
        OperationContext context,
        EntityTransitionOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();

        if (!TryResolveTransitionOperationPartitionKey(context, request, out var partitionKey, out var failure))
            return failure!;
        var index = await TryReadDocument(
                context,
                CreateTransitionCreationReceiptIndexId(request.Subject),
                partitionKey!)
            .ConfigureAwait(false);
        if (index is null)
            return EntityTransitionOperationResult.NotFound();
        ValidateTransitionReceiptDocument(index, requireCommit: false);
        var receiptId = Guard.RequireNotNullOrWhiteSpace(index.TransitionOperationReceiptId);
        var retained = await TryReadTransitionReceipt(context, receiptId, partitionKey!).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Cosmos Transition creation index '{index.Id}' references absent receipt '{receiptId}'.");
        return retained.ReplayCreation(request);
    }

    /// <inheritdoc />
    public async Task<EntityTransitionOperationResult> CommitTransitionOperation(
        OperationContext context,
        EntityTransitionOperationCommit commit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);
        context.ThrowIfCancellationRequested();
        EnsureEntityType(commit.Write.Entity);

        var partitionKey = GetPartitionKey(context, commit.Write.Entity);
        var entityDocument = CreateEntityDocument(context, commit.Write.Entity, partitionKey);
        var receiptDocument = CreateTransitionReceiptDocument(
            context,
            commit,
            entityDocument,
            partitionKey);
        var batch = container.CreateTransactionalBatch(new(partitionKey));
        if (commit.SubjectCondition == EntityTransitionSubjectCondition.MustExist)
        {
            var current = await QueryEntityDocumentAsync(
                    context,
                    commit.Write.Entity.EntityId.Value,
                    partitionKey)
                .ConfigureAwait(false);
            if (!TryMatchExpectedConcurrencyToken(
                    current,
                    commit.Write.ExpectedConcurrencyToken!.Value,
                    out var currentEtag))
            {
                return await ResolveTransitionCommitConflict(
                        context,
                        commit,
                        partitionKey,
                        current is null
                            ? "preflight subject missing"
                            : $"preflight token {GetConcurrencyToken(current).Value}")
                    .ConfigureAwait(false);
            }
            batch.ReplaceItem(
                id: entityDocument.Id,
                item: entityDocument,
                requestOptions: new() { IfMatchEtag = currentEtag });
        }
        else
        {
            batch.CreateItem(entityDocument);
        }
        batch.CreateItem(receiptDocument);
        if (commit.SubjectCondition == EntityTransitionSubjectCondition.MustBeAbsent)
            batch.CreateItem(CreateTransitionCreationReceiptIndexDocument(context, commit, receiptDocument, partitionKey));

        using var response = await batch.ExecuteAsync(context.CancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed
                or HttpStatusCode.NotFound)
            {
                var providerStatus = string.Join(
                    ",",
                    Enumerable.Range(0, response.Count).Select(index => response[index].StatusCode));
                return await ResolveTransitionCommitConflict(
                        context,
                        commit,
                        partitionKey,
                        providerStatus)
                    .ConfigureAwait(false);
            }
            throw new InvalidOperationException(
                $"Transactional Cosmos Transition operation commit for "
                + $"'{observationType}:{commit.Write.Entity.EntityId.Value}' failed with status '{response.StatusCode}'.");
        }

        var receipt = await TryReadTransitionReceipt(context, receiptDocument.Id, partitionKey).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Transactional Cosmos Transition operation commit '{receiptDocument.Id}' succeeded, but its receipt could not be reloaded.");
        if (receipt.Commit.Fingerprint != commit.Fingerprint)
        {
            throw new InvalidOperationException(
                $"Transactional Cosmos Transition operation commit '{receiptDocument.Id}' reloaded different canonical content.");
        }
        return EntityTransitionOperationResult.Committed(receipt);
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

    static string DefaultItemIdSelector(EntityObservationSnapshot snapshot) => snapshot.EntityId.Value;

    static EntityPartitionKeyPolicy ResolvePartitionKeyPolicy(
        EntityPartitionKeyPolicy? partitionKeyPolicy,
        Func<EntityObservationSnapshot, string>? partitionKeySelector,
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
                    writePartitionKeyResolver: static (_, snapshot) => snapshot.EntityId.Value,
                    pointReadPartitionKeyResolver: (_, id) => pointReadPartitionKeySelector(id)
                    );
        }

        return EntityPartitionKeyPolicy.FromObservation(
            partitionKeySelector,
            pointReadPartitionKeySelector: pointReadPartitionKeySelector
            );
    }

    void EnsureEntityType(EntityObservationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Observation.ShapeId != entityDefinition.StateShape.QualifiedId)
            throw new SemanticRuleViolationException($"Repository for '{observationType}' cannot persist snapshot '{snapshot.EntityId.Value}' with shape '{snapshot.Observation.ShapeId}'.");
    }

    string GetPartitionKey(OperationContext context, EntityObservationSnapshot observation)
    {
        try
        {
            return partitionKeyPolicy.ResolveWritePartitionKey(context, observation);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Observation '{observationType}:{observation.EntityId.Value}' did not resolve a partition key from {partitionKeyPolicy.Description}.",
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

    EntitySnapshot CreateSnapshot(CosmosObservationContainerDocument document, string partitionKey) => new(
        Entity: BuildObservation(document),
        PartitionKey: partitionKey,
        ConcurrencyToken: GetConcurrencyToken(document)
        );

    EntityObservationSnapshot BuildObservation(CosmosObservationContainerDocument document)
    {
        if (document.Observation is null)
            throw new InvalidOperationException($"Cosmos document '{document.Id}' does not contain a serialized observation body.");

        var observation = Observation.Create(entityDefinition.StateShape, document.Observation);
        return new(new(document.ObservationId), document.ObservationVersion, observation);
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

        var actualConcurrencyToken = GetConcurrencyToken(document);
        if (read?.ExpectedConcurrencyToken is { } expectedConcurrencyToken
            && expectedConcurrencyToken != actualConcurrencyToken)
        {
            throw new ObservationConcurrencyConflictException(
                $"Observation '{entityType}:{id}' expected concurrency token '{expectedConcurrencyToken.Value}' "
                + $"but found '{actualConcurrencyToken.Value}'.");
        }
    }

    CosmosObservationContainerDocument CreateEntityDocument(OperationContext context, EntityObservationSnapshot observation, string partitionKey)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ValidateObservationVersion(observation.Version);
        return new(
            Id: itemIdSelector(observation),
            PartitionKey: partitionKey,
            DocumentKind: options.EntityDocumentKind,
            ObservationType: observation.Observation.ShapeId.ShapeId.Value,
            ObservationId: observation.EntityId.Value,
            ObservationVersion: observation.Version,
            EntityConcurrencyToken: Guid.NewGuid().ToString("N"),
            Observation: observation.Observation.Fields as Dictionary<string, ObservationValue> ?? new Dictionary<string, ObservationValue>(observation.Observation.Fields, StringComparer.Ordinal),
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
                id: commit.Write.Entity.EntityId.Value,
                readOptions: EntityReadOptions.Full.WithPartitionKey(partitionKey))
            .ConfigureAwait(false);
        if (snapshot is null || snapshot.Entity != commit.Write.Entity)
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

    CosmosObservationContainerDocument CreateTransitionReceiptDocument(
        OperationContext context,
        EntityTransitionOperationCommit commit,
        CosmosObservationContainerDocument entityDocument,
        string partitionKey)
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(commit, TransitionReceiptJsonOptions);
        return new(
            Id: CreateTransitionOperationReceiptId(commit.Request),
            PartitionKey: partitionKey,
            DocumentKind: options.TransitionOperationReceiptDocumentKind,
            ObservationType: observationType,
            ObservationId: commit.Write.Entity.EntityId.Value,
            ObservationVersion: commit.Write.Entity.Version,
            EntityConcurrencyToken: entityDocument.EntityConcurrencyToken,
            SubjectType: commit.Request.Subject.EntityType.Value,
            SubjectId: commit.Request.Subject.EntityId.Value,
            SubjectVersion: commit.Write.Entity.Version,
            OccurredAtUtc: context.UtcNow,
            TraceId: GetTraceId(context),
            SpanId: GetSpanId(context),
            TransitionRequestFingerprint: commit.Request.Fingerprint.Value,
            TransitionIntentFingerprint: commit.Request.IntentFingerprint.Value,
            TransitionCommitFingerprint: commit.Fingerprint.Value,
            TransitionCommitEncoding: TransitionCommitBrotliEncoding,
            TransitionCommitPayload: CompressTransitionCommit(canonical));
    }

    CosmosObservationContainerDocument CreateTransitionCreationReceiptIndexDocument(
        OperationContext context,
        EntityTransitionOperationCommit commit,
        CosmosObservationContainerDocument receipt,
        string partitionKey) =>
        new(
            Id: CreateTransitionCreationReceiptIndexId(commit.Request.Subject),
            PartitionKey: partitionKey,
            DocumentKind: options.TransitionOperationReceiptDocumentKind,
            ObservationType: observationType,
            ObservationId: commit.Write.Entity.EntityId.Value,
            ObservationVersion: commit.Write.Entity.Version,
            EntityConcurrencyToken: receipt.EntityConcurrencyToken,
            SubjectType: commit.Request.Subject.EntityType.Value,
            SubjectId: commit.Request.Subject.EntityId.Value,
            SubjectVersion: commit.Write.Entity.Version,
            OccurredAtUtc: context.UtcNow,
            TraceId: GetTraceId(context),
            SpanId: GetSpanId(context),
            TransitionRequestFingerprint: commit.Request.Fingerprint.Value,
            TransitionIntentFingerprint: commit.Request.IntentFingerprint.Value,
            TransitionCommitFingerprint: commit.Fingerprint.Value,
            TransitionOperationReceiptId: receipt.Id);

    async Task<EntityTransitionOperationResult> ResolveTransitionCommitConflict(
        OperationContext context,
        EntityTransitionOperationCommit commit,
        string partitionKey,
        string? providerStatus = null)
    {
        var retained = await TryReadTransitionReceipt(
                context,
                CreateTransitionOperationReceiptId(commit.Request),
                partitionKey)
            .ConfigureAwait(false);
        if (retained is not null)
        {
            return retained.Replay(commit);
        }

        if (commit.SubjectCondition == EntityTransitionSubjectCondition.MustBeAbsent)
        {
            var creation = await TryGetCreationTransitionOperation(context, commit.Request).ConfigureAwait(false);
            if (creation.Disposition != EntityTransitionOperationDisposition.NotFound)
            {
                if (creation.Receipt is { } receipt
                    && receipt.Request.IntentFingerprint == commit.Request.IntentFingerprint
                    && receipt.Commit.Fingerprint != commit.Fingerprint)
                {
                    return IdentityConflict(
                        "The entity creation intent is retained with different canonical commit content.",
                        "/commit");
                }
                return creation;
            }
            return SubjectStateConflict(
                $"Entity '{EntityType}:{commit.Write.Entity.EntityId.Value}' must be absent for this Transition operation.");
        }

        return ConcurrencyConflict(
            $"Entity '{EntityType}:{commit.Write.Entity.EntityId.Value}' no longer matches concurrency fence "
            + $"'{commit.Write.ExpectedConcurrencyToken!.Value.Value}'."
            + (string.IsNullOrWhiteSpace(providerStatus) ? "" : $" Cosmos batch status: {providerStatus}."));
    }

    async Task<EntityTransitionOperationReceipt?> TryReadTransitionReceipt(
        OperationContext context,
        string id,
        string partitionKey)
    {
        var document = await TryReadDocument(context, id, partitionKey).ConfigureAwait(false);
        if (document is null)
            return null;
        ValidateTransitionReceiptDocument(document, requireCommit: true);
        var commit = DeserializeTransitionCommit(document);
        if (!string.Equals(commit.Request.Fingerprint.Value, document.TransitionRequestFingerprint, StringComparison.Ordinal)
            || !string.Equals(commit.Request.IntentFingerprint.Value, document.TransitionIntentFingerprint, StringComparison.Ordinal)
            || !string.Equals(commit.Fingerprint.Value, document.TransitionCommitFingerprint, StringComparison.Ordinal)
            || !string.Equals(commit.Request.Subject.EntityType.Value, document.SubjectType, StringComparison.Ordinal)
            || !string.Equals(commit.Request.Subject.EntityId.Value, document.SubjectId, StringComparison.Ordinal)
            || !string.Equals(commit.Write.Entity.EntityId.Value, document.ObservationId, StringComparison.Ordinal)
            || commit.Write.Entity.Version != document.ObservationVersion)
        {
            throw new InvalidOperationException(
                $"Cosmos Transition operation receipt '{document.Id}' does not match its canonical fingerprints or subject evidence.");
        }

        return new(
            commit,
            new(
                Entity: commit.Write.Entity,
                PartitionKey: document.PartitionKey,
                ConcurrencyToken: new(Guard.RequireNotNullOrWhiteSpace(document.EntityConcurrencyToken))),
            document.OccurredAtUtc!.Value);
    }

    async Task<CosmosObservationContainerDocument?> TryReadDocument(
        OperationContext context,
        string id,
        string partitionKey)
    {
        try
        {
            var response = await container.ReadItemAsync<CosmosObservationContainerDocument>(
                    id,
                    new(partitionKey),
                    cancellationToken: context.CancellationToken)
                .ConfigureAwait(false);
            return response.Resource with { ETag = response.ETag };
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    static EntityConcurrencyToken GetConcurrencyToken(CosmosObservationContainerDocument document) =>
        new(Guard.RequireNotNullOrWhiteSpace(document.EntityConcurrencyToken ?? document.ETag));

    void RequireExpectedConcurrencyToken(
        CosmosObservationContainerDocument? current,
        EntityConcurrencyToken expected,
        string entityId)
    {
        if (TryMatchExpectedConcurrencyToken(current, expected, out _))
            return;
        var found = current is null ? "<missing>" : GetConcurrencyToken(current).Value;
        throw new ObservationConcurrencyConflictException(
            $"Observation '{observationType}:{entityId}' expected concurrency token '{expected.Value}' but found '{found}'.");
    }

    static bool TryMatchExpectedConcurrencyToken(
        CosmosObservationContainerDocument? current,
        EntityConcurrencyToken expected,
        out string? etag)
    {
        etag = current?.ETag;
        return current is not null
               && !string.IsNullOrWhiteSpace(etag)
               && GetConcurrencyToken(current) == expected;
    }

    void ValidateTransitionReceiptDocument(
        CosmosObservationContainerDocument document,
        bool requireCommit)
    {
        if (!string.Equals(
                document.DocumentKind,
                options.TransitionOperationReceiptDocumentKind,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(document.TransitionRequestFingerprint)
            || string.IsNullOrWhiteSpace(document.TransitionIntentFingerprint)
            || string.IsNullOrWhiteSpace(document.TransitionCommitFingerprint)
            || string.IsNullOrWhiteSpace(document.EntityConcurrencyToken)
            || string.IsNullOrWhiteSpace(document.SubjectType)
            || string.IsNullOrWhiteSpace(document.SubjectId)
            || document.OccurredAtUtc is not { Offset: var offset }
            || offset != TimeSpan.Zero
            || requireCommit && !HasValidTransitionCommitEvidence(document)
            || !requireCommit && string.IsNullOrWhiteSpace(document.TransitionOperationReceiptId))
        {
            throw new InvalidOperationException(
                $"Cosmos Transition operation receipt document '{document.Id}' is incomplete or uses another discriminator.");
        }
    }

    bool TryResolveTransitionOperationPartitionKey(
        OperationContext context,
        EntityTransitionOperationRequest request,
        out string? partitionKey,
        out EntityTransitionOperationResult? failure)
    {
        partitionKey = partitionKeyPolicy.TryResolvePointReadPartitionKey(
            context,
            request.Subject.EntityId.Value);
        if (!string.IsNullOrWhiteSpace(partitionKey))
        {
            failure = null;
            return true;
        }

        failure = EntityTransitionOperationResult.Rejected(
            EntityTransitionOperationDisposition.CapabilityInsufficient,
            new(
                EntityTransitionOperationDiagnosticCodes.CapabilityInsufficient,
                DiagnosticSeverity.Error,
                $"Cosmos entity repository '{EntityType}' cannot resolve exact Transition receipt placement from "
                + $"{partitionKeyPolicy.Description} for subject '{request.Subject.EntityId.Value}'.",
                "/repository/partitionKeyPolicy"));
        return false;
    }

    static string CreateTransitionOperationReceiptId(EntityTransitionOperationRequest request) =>
        $"entity-transition-operation:v1:{HashIdentity(request.Operation)}";

    static string CreateTransitionCreationReceiptIndexId(InteractionEntityReference subject) =>
        $"entity-transition-creation:v1:{HashIdentity(new { EntityType = subject.EntityType.Value, EntityId = subject.EntityId.Value })}";

    static string HashIdentity<T>(T value) where T : class
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(value, TransitionReceiptJsonOptions);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    static bool HasValidTransitionCommitEvidence(CosmosObservationContainerDocument document)
    {
        var hasLegacyCommit = document.TransitionCommit is not null;
        var hasCompressedCommit = string.Equals(
                document.TransitionCommitEncoding,
                TransitionCommitBrotliEncoding,
                StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(document.TransitionCommitPayload);
        return hasLegacyCommit != hasCompressedCommit
            && (hasLegacyCommit
                ? document.TransitionCommitEncoding is null && document.TransitionCommitPayload is null
                : document.TransitionCommit is null);
    }

    static EntityTransitionOperationCommit DeserializeTransitionCommit(
        CosmosObservationContainerDocument document)
    {
        if (document.TransitionCommit is { } legacy)
        {
            return legacy.Deserialize<EntityTransitionOperationCommit>(TransitionReceiptJsonOptions)
                ?? throw new InvalidOperationException(
                    $"Cosmos Transition operation receipt '{document.Id}' deserialized to null.");
        }

        try
        {
            var canonical = DecompressTransitionCommit(
                Convert.FromBase64String(Guard.RequireNotNullOrWhiteSpace(document.TransitionCommitPayload)));
            var commit = JsonSerializer.Deserialize<EntityTransitionOperationCommit>(
                    canonical,
                    TransitionReceiptJsonOptions)
                ?? throw new InvalidOperationException(
                    $"Cosmos Transition operation receipt '{document.Id}' deserialized to null.");
            if (!canonical.AsSpan().SequenceEqual(
                    StrictDocumentJson.GetCanonicalBytes(commit, TransitionReceiptJsonOptions)))
            {
                throw new InvalidOperationException(
                    $"Cosmos Transition operation receipt '{document.Id}' does not contain canonical commit JSON.");
            }
            return commit;
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or JsonException)
        {
            throw new InvalidOperationException(
                $"Cosmos Transition operation receipt '{document.Id}' has invalid compressed canonical commit evidence.",
                exception);
        }
    }

    static string CompressTransitionCommit(ReadOnlySpan<byte> canonical)
    {
        if (canonical.Length > MaximumTransitionCommitCanonicalBytes)
        {
            throw new InvalidOperationException(
                $"Canonical Transition commit exceeds {MaximumTransitionCommitCanonicalBytes} bytes.");
        }
        using MemoryStream output = new();
        using (BrotliStream compressor = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
            compressor.Write(canonical);
        return Convert.ToBase64String(output.GetBuffer(), 0, checked((int)output.Length));
    }

    static byte[] DecompressTransitionCommit(ReadOnlySpan<byte> compressed)
    {
        using MemoryStream input = new(compressed.ToArray(), writable: false);
        using BrotliStream decompressor = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        var buffer = new byte[81920];
        while (true)
        {
            var read = decompressor.Read(buffer);
            if (read == 0)
                break;
            if (output.Length + read > MaximumTransitionCommitCanonicalBytes)
            {
                throw new InvalidDataException(
                    $"Decompressed Transition commit exceeds {MaximumTransitionCommitCanonicalBytes} bytes.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    static EntityTransitionOperationResult IdentityConflict(string message, string location) =>
        EntityTransitionOperationResult.Rejected(
            EntityTransitionOperationDisposition.IdentityConflict,
            new(
                EntityTransitionOperationDiagnosticCodes.IdentityConflict,
                DiagnosticSeverity.Error,
                message,
                location));

    static EntityTransitionOperationResult ConcurrencyConflict(string message) =>
        EntityTransitionOperationResult.Rejected(
            EntityTransitionOperationDisposition.ConcurrencyConflict,
            new(
                EntityTransitionOperationDiagnosticCodes.ConcurrencyConflict,
                DiagnosticSeverity.Error,
                message,
                "/write/expectedConcurrencyToken"));

    static EntityTransitionOperationResult SubjectStateConflict(string message) =>
        EntityTransitionOperationResult.Rejected(
            EntityTransitionOperationDisposition.SubjectStateConflict,
            new(
                EntityTransitionOperationDiagnosticCodes.SubjectStateConflict,
                DiagnosticSeverity.Error,
                message,
                "/write/subjectCondition"));

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
