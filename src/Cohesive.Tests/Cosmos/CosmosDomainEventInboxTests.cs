using Cohesive.Adapters.Cosmos;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Tests.Cosmos;

public sealed class CosmosDomainEventInboxTests
{
    const string EmulatorMasterKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly DateTimeOffset AcceptedAtUtc =
        new(2026, 8, 15, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Document_RoundTripsCompleteScopedKeyCanonicalEnvelopeAndStableReceipt()
    {
        var contractDocument = EventContractDocument();
        var contract = new DomainEventContractReference(Reference(contractDocument));
        var contracts = Catalog(contractDocument);
        using var client = Client();
        var inbox = new CosmosDomainEventInbox(
            client.GetContainer("tests", "domain-events"),
            contracts,
            [contract]);
        var invocation = DomainEventPublicationInvocation.From(Envelope(contract, "payload"));
        var document = inbox.CreateDocument(invocation, AcceptedAtUtc);
        var serializer = new CosmosSystemTextJsonSerializer();

        var roundTrip = serializer.FromStream<CosmosDomainEventInboxDocument>(
            serializer.ToStream(document));
        var entry = inbox.Restore(roundTrip, invocation.DeduplicationKey);

        Assert.Equal(invocation.DeduplicationKey, entry.DeduplicationKey);
        Assert.Equal(
            InteractionEnvelopeJsonSerializer.Serialize(invocation.DomainEvent),
            InteractionEnvelopeJsonSerializer.Serialize(entry.DomainEvent));
        Assert.Equal(
            InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(invocation.DomainEvent),
            entry.ContentFingerprint);
        Assert.Equal(AcceptedAtUtc, entry.AcceptedAtUtc);
        Assert.StartsWith("authority-", document.PartitionKey, StringComparison.Ordinal);
        Assert.StartsWith("domain-event-", document.Id, StringComparison.Ordinal);
        Assert.Equal(
            $"cosmos-domain-event-inbox:{document.Id}",
            entry.Acknowledgement.Evidence?.Value?.GetRequiredString());
    }

    [Fact]
    public void Replay_AdmitsExactContentButRejectsSameScopedKeyWithDifferentCanonicalEnvelope()
    {
        var contractDocument = EventContractDocument();
        var contract = new DomainEventContractReference(Reference(contractDocument));
        using var client = Client();
        var inbox = new CosmosDomainEventInbox(
            client.GetContainer("tests", "domain-events"),
            Catalog(contractDocument),
            [contract]);
        var first = DomainEventPublicationInvocation.From(Envelope(contract, "payload"));
        var conflict = DomainEventPublicationInvocation.From(Envelope(contract, "changed"));
        var retained = inbox.CreateDocument(first, AcceptedAtUtc);
        var replay = inbox.CreateDocument(first, AcceptedAtUtc.AddMinutes(1));
        var conflicting = inbox.CreateDocument(conflict, AcceptedAtUtc.AddMinutes(1));

        CosmosDomainEventInbox.RequireExactReplay(replay, retained);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CosmosDomainEventInbox.RequireExactReplay(conflicting, retained));

        Assert.Contains(CosmosDomainEventInbox.IdentityConflictCode, exception.Message, StringComparison.Ordinal);
        Assert.Contains("different canonical content", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsAnExactReferenceThatDoesNotResolveAsADomainEvent()
    {
        var retainedDocument = EventContractDocument();
        var foreignDocument = InteractionContractDocuments.Create(
            new("tests/cosmos/domain-event-inbox/foreign"),
            new("revision/1"),
            new DomainEventContractDefinition(new(
                StringContract,
                new("event/v1"))),
            Provenance());
        using var client = Client();

        var exception = Assert.Throws<ArgumentException>(() => new CosmosDomainEventInbox(
            client.GetContainer("tests", "domain-events"),
            Catalog(retainedDocument),
            [new DomainEventContractReference(Reference(foreignDocument))]));

        Assert.Contains("resolve as an exact domain-event contract", exception.Message, StringComparison.Ordinal);
    }

    [CosmosDomainEventInboxFact]
    public async Task Cosmos_TargetPersistsExactReplayAcrossPublisherRestartAndRejectsIdentityConflict()
    {
        var connectionString = Environment.GetEnvironmentVariable("COSMOS_DOMAIN_EVENT_INBOX_CONNECTION_STRING")
            ?? throw new InvalidOperationException("The Cosmos connection string disappeared after discovery.");
        var contractDocument = EventContractDocument();
        var contract = new DomainEventContractReference(Reference(contractDocument));
        var contracts = Catalog(contractDocument);
        var databaseId = $"cohesive-domain-event-inbox-tests-{Guid.NewGuid():N}";
        using var client = new CosmosClient(connectionString, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            Serializer = new CosmosSystemTextJsonSerializer(),
            HttpClientFactory = static () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            })
        });
        var database = (await client.CreateDatabaseAsync(databaseId)).Database;
        try
        {
            var container = (await database.CreateContainerAsync(
                new ContainerProperties("events", "/partitionKey"))).Container;
            var firstPublisher = new CosmosDomainEventInbox(container, contracts, [contract]);
            await firstPublisher.ValidateAsync(OperationContext.Create());
            var invocation = DomainEventPublicationInvocation.From(Envelope(contract, "payload"));
            var first = await firstPublisher.PublishAsync(OperationContext.Create(), invocation);
            var replay = await firstPublisher.PublishAsync(OperationContext.Create(), invocation);

            var restartedPublisher = new CosmosDomainEventInbox(container, contracts, [contract]);
            var restartedReplay = await restartedPublisher.PublishAsync(OperationContext.Create(), invocation);
            var retained = await restartedPublisher.TryReadAsync(
                OperationContext.Create(),
                invocation.DeduplicationKey);

            Assert.Equal(first, replay);
            Assert.Equal(first, restartedReplay);
            Assert.NotNull(retained);
            Assert.Equal(
                InteractionEnvelopeJsonSerializer.Serialize(invocation.DomainEvent),
                InteractionEnvelopeJsonSerializer.Serialize(retained.DomainEvent));

            var conflict = DomainEventPublicationInvocation.From(Envelope(contract, "changed"));
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await restartedPublisher.PublishAsync(OperationContext.Create(), conflict));
            Assert.Contains(CosmosDomainEventInbox.IdentityConflictCode, exception.Message, StringComparison.Ordinal);

            var expiring = (await database.CreateContainerAsync(new ContainerProperties(
                "expiring-events",
                "/partitionKey")
            {
                DefaultTimeToLive = 60
            })).Container;
            var incompatible = new CosmosDomainEventInbox(expiring, contracts, [contract]);
            var validation = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await incompatible.ValidateAsync(OperationContext.Create()));
            Assert.Contains(CosmosDomainEventInbox.ContainerIncompatibleCode, validation.Message, StringComparison.Ordinal);
        }
        finally
        {
            await database.DeleteAsync();
        }
    }

    static CosmosClient Client() => new(
        "https://localhost:8081/",
        EmulatorMasterKey,
        new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            Serializer = new CosmosSystemTextJsonSerializer()
        });

    static DomainEventEnvelope Envelope(DomainEventContractReference contract, string payload) => new(
        InteractionEnvelope.CurrentSchemaVersion,
        new(
            new("emission/domain-event-inbox/1"),
            new TransitionInteractionOrigin(
                DefinitionReference("transition/generate", 'd'),
                new("emit/generated"),
                new(new("TrainingExample"), new("example/1")),
                new("outcome/applied")),
            new("correlation/domain-event-inbox/1"),
            causationId: null,
            new("authority/tests", "tenant/acme"),
            new("idempotency/domain-event-inbox/1"),
            ordering: null,
            new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit),
            Provenance()),
        contract,
        PortableValue.Concrete(StringContract, ObservationValue.FromString(payload)));

    static ExecutionDefinitionDocument EventContractDocument() => InteractionContractDocuments.Create(
        new("tests/cosmos/domain-event-inbox/generated"),
        new("revision/1"),
        new DomainEventContractDefinition(new(
            StringContract,
            new("event/v1"))),
        Provenance());

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

    static ExecutionDefinitionReference DefinitionReference(string id, char fingerprintDigit) => new(
        new(id),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string(fingerprintDigit, 64)));

    static ExecutionProvenance Provenance() => new(
        new("cosmos-domain-event-inbox-tests", "1"),
        new("tests/cosmos/domain-event-inbox"),
        DocumentOrigin.Generated);

    sealed class CosmosDomainEventInboxFactAttribute : FactAttribute
    {
        public CosmosDomainEventInboxFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("COSMOS_DOMAIN_EVENT_INBOX_CONNECTION_STRING")))
            {
                Skip = "Set COSMOS_DOMAIN_EVENT_INBOX_CONNECTION_STRING to run the Cosmos inbox integration test.";
            }
        }
    }

}
