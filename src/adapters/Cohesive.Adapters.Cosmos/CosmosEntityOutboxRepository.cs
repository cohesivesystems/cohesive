using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using System.Text.Json.Serialization;
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
    readonly EntityDefinition entityDefinition;
    readonly string observationType;
    readonly Container container;
    readonly Container leaseContainer;
    readonly EntityPartitionKeyPolicy partitionKeyPolicy;
    readonly Func<Observation, string> itemIdSelector;
    readonly CosmosObservationOutboxRepositoryOptions options;

    /// <summary>
    /// Creates a repository for one entity definition persisted in observation format.
    /// </summary>
    /// <param name="entityDefinition">Entity shape and semantic identity persisted by this repository.</param>
    /// <param name="container">Cosmos container containing entity and optional outbox documents.</param>
    /// <param name="leaseContainer">Cosmos container used by entity and outbox change-feed processors.</param>
    /// <param name="partitionKeySelector">
    /// Legacy observation-to-partition selector, or <see langword="null"/> to use <paramref name="partitionKeyPolicy"/>
    /// or the entity-identity convention.
    /// </param>
    /// <param name="pointReadPartitionKeySelector">
    /// Legacy entity-identity-to-partition selector, or <see langword="null"/> when unavailable.
    /// </param>
    /// <param name="itemIdSelector">Observation-to-item-id selector, or <see langword="null"/> for the default.</param>
    /// <param name="options">Repository and change-feed options, or <see langword="null"/> for conventions.</param>
    /// <param name="mappingContext">Object/observation mapping context, or <see langword="null"/> for the default.</param>
    /// <param name="partitionKeyPolicy">
    /// Explicit read/write partition policy. It is mutually exclusive with either legacy partition selector.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entityDefinition"/>, <paramref name="container"/>, or <paramref name="leaseContainer"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Explicit and legacy partition policies are combined, the legacy selectors are incomplete, or the entity and
    /// outbox document discriminators are empty or equal.
    /// </exception>
    public CosmosEntityOutboxRepository(
        EntityDefinition entityDefinition,
        Container container,
        Container leaseContainer,
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
        this.leaseContainer = Guard.RequireNotNull(leaseContainer);
        this.partitionKeyPolicy = ResolvePartitionKeyPolicy(
            partitionKeyPolicy,
            partitionKeySelector,
            pointReadPartitionKeySelector
            );
        this.itemIdSelector = itemIdSelector ?? DefaultItemIdSelector;
        this.options = CosmosObservationOutboxRepositoryOptions.RequireValid(options ?? new());
        MappingContext = mappingContext ?? ShapeMappingContext.Default;
    }

    /// <summary>
    /// Gets the underlying Cosmos container.
    /// </summary>
    public Container Container => container;
    
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

        if (commit.Messages.Count == 0)
        {
            var committedSnapshot = await Upsert(context, commit.Write).ConfigureAwait(false);
            return new(committedSnapshot, commit.Messages);
        }

        EnsureEntityType(commit.Write.Entity);
        var partitionKey = GetPartitionKey(context, commit.Write.Entity);

        foreach (var message in commit.Messages)
        {
            if (!string.Equals(message.PartitionKey, partitionKey, StringComparison.Ordinal))
                throw new SemanticRuleViolationException($"Outbox message '{message.MessageId}' uses partition '{message.PartitionKey}', but commit partition was '{partitionKey}'.");
        }

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

        foreach (var message in commit.Messages)
            batch.CreateItem(CreateOutboxDocument(context, message));

        using var response = await batch.ExecuteAsync(context.CancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                throw new ObservationConcurrencyConflictException($"Observation '{observationType}:{commit.Write.Entity.Id}' failed optimistic concurrency validation inside transactional batch.");

            throw new InvalidOperationException($"Transactional Cosmos observation commit for '{observationType}:{commit.Write.Entity.Id}' failed with status '{response.StatusCode}'.");
        }

        var snapshot = await TryGet(context, id: commit.Write.Entity.Id, readOptions: EntityReadOptions.Full).ConfigureAwait(false);
        if (snapshot is null)
            throw new InvalidOperationException($"Transactional Cosmos observation commit for '{observationType}:{commit.Write.Entity.Id}' succeeded, but the entity could not be reloaded.");

        return new(snapshot, commit.Messages);
    }

    /// <inheritdoc />
    public IObservationStream GetChangeStream(string processorName, DateTimeOffset? startTime = null) => new CosmosObservationStream(
        processorName: ComposeProcessorName(processorName, "changes"),
        streamName: $"{observationType}:changes",
        container: container,
        leaseContainer: leaseContainer,
        options: options,
        startTime: startTime,
        filter: IsEntityDocument,
        projection: document => CreateRecord(ObservationStreamRecordKind.EntityChange, document)
        );

    /// <inheritdoc />
    public IObservationStream GetOutboxStream(string processorName, string? streamName = null, DateTimeOffset? startTime = null) => new CosmosObservationStream(
        processorName: ComposeProcessorName(processorName, string.IsNullOrWhiteSpace(streamName) ? "outbox" : $"outbox:{streamName}"),
        streamName: string.IsNullOrWhiteSpace(streamName) ? $"{observationType}:outbox" : streamName!,
        container: container,
        leaseContainer: leaseContainer,
        options: options,
        startTime: startTime,
        filter: document => IsOutboxDocument(document) && (string.IsNullOrWhiteSpace(streamName) || string.Equals(document.StreamName, streamName, StringComparison.Ordinal)),
        projection: document => CreateRecord(ObservationStreamRecordKind.OutboxEvent, document)
        );

    /// <summary>
    /// Counts outbox documents in this repository's container associated with the supplied subject id.
    /// </summary>
    /// <param name="context">Operation context carrying cancellation and attribution.</param>
    /// <param name="subjectId">Non-empty outbox subject identity.</param>
    /// <returns>The number of matching outbox documents returned by Cosmos.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="subjectId"/> is empty or white space.</exception>
    /// <exception cref="OperationCanceledException">The operation context is canceled.</exception>
    /// <exception cref="CosmosException">Cosmos rejects or fails the count query.</exception>
    public async Task<int> CountOutboxMessages(OperationContext context, string subjectId)
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

    string ComposeProcessorName(string processorName, string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorName);
        return $"{processorName}:{observationType}:{suffix}";
    }

    bool IsEntityDocument(CosmosObservationContainerDocument document) =>
        string.Equals(document.DocumentKind, options.EntityDocumentKind, StringComparison.Ordinal)
        && string.Equals(document.ObservationType, observationType, StringComparison.Ordinal);

    bool IsOutboxDocument(CosmosObservationContainerDocument document) =>
        string.Equals(document.DocumentKind, options.OutboxDocumentKind, StringComparison.Ordinal);

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
            """
            );
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

    CosmosObservationContainerDocument CreateOutboxDocument(OperationContext context, EntityOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(message);

        return new(
            Id: message.MessageId,
            PartitionKey: message.PartitionKey,
            DocumentKind: options.OutboxDocumentKind,
            ObservationType: message.Entity.ShapeId.Value,
            ObservationId: message.Entity.Id,
            ObservationVersion: message.Entity.Version,
            Observation: message.Entity.Fields as Dictionary<string, ObservationValue> ?? new Dictionary<string, ObservationValue>(message.Entity.Fields, StringComparer.Ordinal),
            StreamName: message.StreamName,
            SubjectType: message.SubjectType,
            SubjectId: message.SubjectId,
            SubjectVersion: message.SubjectVersion,
            CorrelationId: message.CorrelationId,
            OccurredAtUtc: message.OccurredAtUtc ?? context.UtcNow,
            TraceId: GetTraceId(context),
            SpanId: GetSpanId(context)
            );
    }

    static ObservationRecord CreateRecord(ObservationStreamRecordKind kind, CosmosObservationContainerDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new(
            Kind: kind,
            Observation: BuildObservation(document),
            PartitionKey: document.PartitionKey,
            DocumentId: document.Id,
            StreamName: document.StreamName,
            SubjectType: document.SubjectType,
            SubjectId: document.SubjectId,
            SubjectVersion: document.SubjectVersion,
            CorrelationId: document.CorrelationId,
            OccurredAtUtc: document.OccurredAtUtc,
            ConcurrencyToken: string.IsNullOrWhiteSpace(document.ETag) ? null : new(document.ETag)
            );
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

    sealed class CosmosObservationStream(
        string processorName,
        string streamName,
        Container container,
        Container leaseContainer,
        CosmosObservationOutboxRepositoryOptions options,
        DateTimeOffset? startTime,
        Func<CosmosObservationContainerDocument, bool> filter,
        Func<CosmosObservationContainerDocument, ObservationRecord> projection
        ) : IObservationStream
    {
        readonly string processorName = Guard.RequireNotNullOrWhiteSpace(processorName);
        readonly string streamName = Guard.RequireNotNullOrWhiteSpace(streamName);
        readonly Container container = Guard.RequireNotNull(container);
        readonly Container leaseContainer = Guard.RequireNotNull(leaseContainer);
        readonly CosmosObservationOutboxRepositoryOptions options = Guard.RequireNotNull(options);
        readonly Func<CosmosObservationContainerDocument, bool> filter = Guard.RequireNotNull(filter);
        readonly Func<CosmosObservationContainerDocument, ObservationRecord> projection = Guard.RequireNotNull(projection);

        public string StreamName => streamName;

        public async Task Process(Func<ObservationBatchContext, IReadOnlyCollection<ObservationRecord>, CancellationToken, Task> handle, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(handle);

            var builder = container.GetChangeFeedProcessorBuilder<CosmosObservationContainerDocument>(
                processorName: processorName,
                onChangesDelegate: async (context, changes, cancellationToken) =>
                {
                    var records = changes
                        .Where(filter)
                        .Select(projection)
                        .ToArray();

                    if (records.Length == 0)
                        return;

                    await handle(
                        new(
                            StreamName: streamName,
                            ProcessorName: processorName,
                            LeaseToken: context.LeaseToken,
                            NativeContext: context),
                        records,
                        cancellationToken).ConfigureAwait(false);
                })
                .WithInstanceName(options.InstanceName)
                .WithLeaseContainer(leaseContainer);

            if (startTime.HasValue)
                builder = builder.WithStartTime(startTime.Value.UtcDateTime);

            var processor = builder.Build();
            await processor.StartAsync().ConfigureAwait(false);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            finally
            {
                await processor.StopAsync().ConfigureAwait(false);
            }
        }

        public async IAsyncEnumerable<IReadOnlyList<ObservationStreamLagSnapshot>> LagStream([EnumeratorCancellation] CancellationToken ct = default)
        {
            var channel = Channel.CreateUnbounded<IReadOnlyList<ObservationStreamLagSnapshot>>();
            var estimator = container.GetChangeFeedEstimatorBuilder(
                    processorName: processorName,
                    estimationDelegate: (estimation, cancellationToken) =>
                        channel.Writer.WriteAsync(
                            [
                                new(
                                    StreamName: streamName,
                                    EstimatedLag: estimation,
                                    SampledAtUtc: DateTimeOffset.UtcNow)
                            ],
                            cancellationToken).AsTask(),
                    estimationPeriod: options.LagPollingInterval)
                .WithLeaseContainer(leaseContainer)
                .Build();

            await estimator.StartAsync().ConfigureAwait(false);
            try
            {
                await foreach (var sample in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    yield return sample;
            }
            finally
            {
                channel.Writer.TryComplete();
                await estimator.StopAsync().ConfigureAwait(false);
            }
        }
    }
}

[JsonSerializable(typeof(CosmosObservationContainerDocument))]
sealed record CosmosObservationContainerDocument(
    
    [property: JsonPropertyName("id")] string Id,
    
    [property: JsonPropertyName("partitionKey")] string PartitionKey,
    
    [property: JsonPropertyName("documentKind")] string DocumentKind,
    
    [property: JsonPropertyName("observationType")] string ObservationType,
    
    [property: JsonPropertyName("observationId")] string ObservationId,
    
    [property: JsonPropertyName("observationVersion")] long ObservationVersion,
    
    [property: JsonPropertyName("observation")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Dictionary<string, ObservationValue>? Observation = null,
    
    [property: JsonPropertyName("streamName")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? StreamName = null,
    
    [property: JsonPropertyName("subjectType")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SubjectType = null,
    
    [property: JsonPropertyName("subjectId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SubjectId = null,
    
    [property: JsonPropertyName("subjectVersion")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? SubjectVersion = null,
    
    [property: JsonPropertyName("correlationId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CorrelationId = null,
    
    [property: JsonPropertyName("occurredAtUtc")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? OccurredAtUtc = null,
    
    [property: JsonPropertyName("traceId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TraceId = null,
    
    [property: JsonPropertyName("spanId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SpanId = null,
    
    [property: JsonPropertyName("_etag")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ETag = null
    );
