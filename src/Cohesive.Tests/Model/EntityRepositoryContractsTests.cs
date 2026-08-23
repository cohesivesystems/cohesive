using Cohesive.Adapters.Cosmos;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Tests.Model;

public sealed class EntityRepositoryContractsTests
{
    const string EmulatorMasterKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    [Fact]
    public void EntityReadOptions_WithExpectedConcurrencyToken_StoresValue()
    {
        var options = new EntityReadOptions(expectedConcurrencyToken: new("etag-7"));

        Assert.Equal(new EntityConcurrencyToken("etag-7"), options.ExpectedConcurrencyToken);
    }

    [Fact]
    public void EntityReadOptions_WithoutExpectedConcurrencyToken_KeepsNull()
    {
        var options = new EntityReadOptions();

        Assert.Null(options.ExpectedConcurrencyToken);
    }

    [Fact]
    public void EntityReadOptions_WithFieldSelection_StoresSelection()
    {
        var selection = FieldSelection.ForFields("Name", "Name");
        var options = new EntityReadOptions(fieldSelection: selection);

        Assert.Same(selection, options.FieldSelection);
        Assert.Equal(["Name"], options.Fields);
    }

    [Fact]
    public void FieldSelection_WithWhitespaceField_Throws()
    {
        var error = Assert.Throws<ArgumentException>(() => FieldSelection.ForFields("Name", " "));

        Assert.Contains("must not be null, empty, or whitespace", error.Message);
    }

    [Fact]
    public void CosmosEntityOutboxRepository_ValidateReadPreconditions_WithExpectedConcurrencyTokenMismatch_ThrowsConcurrencyConflict()
    {
        var document = CreateDocument(
            observationId: "obs-1",
            version: 7,
            etag: "etag-actual");

        var error = Assert.Throws<ObservationConcurrencyConflictException>(() =>
            CosmosEntityOutboxRepository.ValidateReadPreconditions(
                entityType: "Sample",
                id: "obs-1",
                document: document,
                read: new EntityReadOptions(expectedConcurrencyToken: new("etag-expected"))));

        Assert.Contains("expected ETag 'etag-expected' but found 'etag-actual'", error.Message);
    }

    [Fact]
    public void CosmosEntityOutboxRepository_ValidateReadPreconditions_WithExpectedConcurrencyTokenMatch_DoesNotThrow()
    {
        var document = CreateDocument(
            observationId: "obs-1",
            version: 7,
            etag: "etag-7");

        CosmosEntityOutboxRepository.ValidateReadPreconditions(
            entityType: "Sample",
            id: "obs-1",
            document: document,
            read: new EntityReadOptions(expectedConcurrencyToken: new("etag-7")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(CosmosEntityOutboxRepository.MaximumExactObservationVersion)]
    public void CosmosEntityOutboxRepository_ObservationVersionWithinExactDomain_IsAccepted(long version)
    {
        CosmosEntityOutboxRepository.ValidateObservationVersion(version);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(CosmosEntityOutboxRepository.MaximumExactObservationVersion + 1)]
    public void CosmosEntityOutboxRepository_ObservationVersionOutsideExactDomain_IsRejected(long version)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CosmosEntityOutboxRepository.ValidateObservationVersion(version));

        Assert.Contains("Cosmos SQL retains it exactly", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CosmosEntityOutboxRepository_OutboxMaterializationRejectsUnsafeVersionBeforeProviderIo()
    {
        var state = RepositoryEntity.Instance.CreateState(
            "obs-unsafe",
            new RepositoryState("obs-unsafe", "tenant-a", "payload"));
        using CosmosClient client = new(
            "https://localhost:8081/",
            EmulatorMasterKey,
            new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
        var repository = new CosmosEntityOutboxRepository(
            RepositoryEntity.Instance.Definition,
            client.GetContainer("tests", "entities"),
            partitionKeyPolicy: EntityPartitionKeyPolicy.FromField(nameof(RepositoryState.Tenant)));

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => repository.CreateOutboxDocuments(
            OperationContext.Create(),
            new EntityOutboxCommit(
                new(state.Observation with
                {
                    Version = CosmosEntityOutboxRepository.MaximumExactObservationVersion + 1
                }),
                []),
            partitionKey: "tenant-a"));

        Assert.Equal("version", error.ParamName);
    }

    [Fact]
    public void CosmosObservationOutboxRepositoryOptions_RequireDistinctNonemptyDocumentKinds()
    {
        Assert.Throws<ArgumentException>(() =>
            CosmosObservationOutboxRepositoryOptions.RequireValid(new() { EntityDocumentKind = " " }));
        Assert.Throws<ArgumentException>(() =>
            CosmosObservationOutboxRepositoryOptions.RequireValid(new() { OutboxDocumentKind = string.Empty }));
        Assert.Throws<ArgumentException>(() =>
            CosmosObservationOutboxRepositoryOptions.RequireValid(new()
            {
                EntityDocumentKind = "document",
                OutboxDocumentKind = "document"
            }));

        var options = new CosmosObservationOutboxRepositoryOptions
        {
            EntityDocumentKind = "entity-v2",
            OutboxDocumentKind = "outbox-v2"
        };
        Assert.Same(options, CosmosObservationOutboxRepositoryOptions.RequireValid(options));
    }

    [Fact]
    public void CosmosOutboxDocument_RoundTripsExactCanonicalEnvelopeAndFingerprint()
    {
        var eventDocument = InteractionContractDocuments.Create(
            new("tests/cosmos/outbox/event"),
            new("revision/1"),
            new DomainEventContractDefinition(new(
                new(new ScalarTypeRef(ScalarTypeKind.String)),
                new("event/v1"))),
            Provenance());
        var contracts = Catalog(eventDocument);
        var state = RepositoryEntity.Instance.CreateState(
            "obs-1",
            new RepositoryState("obs-1", "tenant-a", "payload"));
        var envelope = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            new(
                new("emission/cosmos/1"),
                new TransitionInteractionOrigin(
                    Reference(eventDocument),
                    new("emit/event"),
                    new(new(state.Observation.ShapeId.Value), new(state.Observation.Id)),
                    new("outcome/applied")),
                new("correlation/cosmos/1"),
                causationId: null,
                new("authority/tests", "tenant-a"),
                new("idempotency/cosmos/1"),
                ordering: null,
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit),
                Provenance()),
            new(Reference(eventDocument)),
            PortableValue.Concrete(
                new(new ScalarTypeRef(ScalarTypeKind.String)),
                ObservationValue.FromString("payload")));
        using CosmosClient client = new(
            "https://localhost:8081/",
            EmulatorMasterKey,
            new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
        var repository = new CosmosEntityOutboxRepository(
            RepositoryEntity.Instance.Definition,
            client.GetContainer("tests", "entities"),
            partitionKeyPolicy: EntityPartitionKeyPolicy.FromField(nameof(RepositoryState.Tenant)));
        Assert.Equal(CosmosObservationOutboxRepositoryOptions.DefaultEntityDocumentKind, repository.EntityDocumentKind);
        var document = Assert.Single(repository.CreateOutboxDocuments(
            OperationContext.Create(),
            new EntityOutboxCommit(new(state.Observation), [envelope]),
            partitionKey: "tenant-a"));
        var serializer = new CosmosSystemTextJsonSerializer();

        var roundTrip = serializer.FromStream<CosmosObservationContainerDocument>(serializer.ToStream(document));
        var restored = CosmosEntityOutboxRepository.DeserializeOutboxEnvelope(roundTrip, contracts);

        Assert.Equal(
            InteractionEnvelopeJsonSerializer.Serialize(envelope),
            InteractionEnvelopeJsonSerializer.Serialize(restored));
        Assert.Equal(document.EnvelopeFingerprint, roundTrip.EnvelopeFingerprint);

        var error = Assert.Throws<InvalidOperationException>(() =>
            CosmosEntityOutboxRepository.DeserializeOutboxEnvelope(
                roundTrip with { EnvelopeFingerprint = "sha256-v1:corrupt" },
                contracts));
        Assert.Contains("does not match", error.Message, StringComparison.Ordinal);
        Assert.Equal(eventDocument.Metadata.DefinitionId.Value, document.StreamName);
        Assert.Equal(state.Observation.ShapeId.Value, document.SubjectType);
        Assert.Equal("payload", document.Observation?["value"].GetString());
    }

    static CosmosObservationContainerDocument CreateDocument(
        string observationId,
        long version,
        string? etag) =>
        new(
            Id: observationId,
            PartitionKey: observationId,
            DocumentKind: "entity",
            ObservationType: "Sample",
            ObservationId: observationId,
            ObservationVersion: version,
            Observation: new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["Name"] = ObservationValue.FromString("alpha")
            },
            ETag: etag);

    static InteractionContractCatalog Catalog(params ExecutionDefinitionDocument[] documents)
    {
        var validation = InteractionContractCatalog.TryCreate(documents, out var catalog);
        Assert.True(validation.IsValid, string.Join(
            Environment.NewLine,
            validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        return Assert.IsType<InteractionContractCatalog>(catalog);
    }

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static ExecutionProvenance Provenance() => new(
        new("entity-repository-contract-tests", "1"),
        new("tests/model/entity-repository-contracts"),
        DocumentOrigin.Generated);

    sealed class RepositoryEntity : Entity<RepositoryEntity>
    {
        public RepositoryEntity()
        {
            Id = WriteOnceField<string>(nameof(Id));
            Tenant = WriteOnceField<string>(nameof(Tenant));
            Payload = MutableField<string>(nameof(Payload));
        }

        public Field<string> Id { get; }

        public Field<string> Tenant { get; }

        public Field<string> Payload { get; }
    }

    sealed record RepositoryState(string Id, string Tenant, string Payload);
}
