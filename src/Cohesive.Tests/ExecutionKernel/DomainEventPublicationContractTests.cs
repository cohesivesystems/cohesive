using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class DomainEventPublicationContractTests
{
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void Invocation_DerivesOneAuthorityAndContractScopedCanonicalDeduplicationKey()
    {
        var envelope = Envelope(
            EventContract("interaction/event/reviewed", 'a'),
            new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit));

        var invocation = DomainEventPublicationInvocation.From(envelope);

        Assert.Same(envelope, invocation.DomainEvent);
        Assert.Equal(envelope.Context.AuthorityScope, invocation.DeduplicationKey.AuthorityScope);
        Assert.Equal(envelope.Contract, invocation.DeduplicationKey.Contract);
        Assert.Equal(envelope.Context.IdempotencyKey, invocation.DeduplicationKey.IdempotencyKey);
    }

    [Fact]
    public void Invocation_RejectsForeignDeduplicationEvidence()
    {
        var envelope = Envelope(
            EventContract("interaction/event/reviewed", 'a'),
            new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit));
        var foreign = new DomainEventPublicationDeduplicationKey(
            envelope.Context.AuthorityScope,
            envelope.Contract,
            new("idempotency/foreign"));

        Assert.Throws<ArgumentException>(() => new DomainEventPublicationInvocation(envelope, foreign));
    }

    [Theory]
    [InlineData(InteractionDurabilityDemand.ActivationLocal, InteractionVisibilityDemand.ActivationLocal)]
    [InlineData(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AtomicWithOrigin)]
    public void Invocation_RejectsDeliveryThatTheAsynchronousPublisherCannotHonor(
        InteractionDurabilityDemand durability,
        InteractionVisibilityDemand visibility)
    {
        var envelope = Envelope(
            EventContract("interaction/event/reviewed", 'a'),
            new(durability, visibility));

        var exception = Assert.Throws<ArgumentException>(() => DomainEventPublicationInvocation.From(envelope));

        Assert.Contains("durable after-origin-commit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PublisherCapabilities_NormalizeExactContractsAndRejectDuplicates()
    {
        var first = EventContract("interaction/event/first", 'a');
        var second = EventContract("interaction/event/second", 'b');

        var capabilities = new DomainEventPublisherCapabilities([second, first]);

        Assert.Equal(first, capabilities.TargetDeduplicatedContracts[0]);
        Assert.Equal(second, capabilities.TargetDeduplicatedContracts[1]);
        Assert.True(capabilities.Supports(first));
        Assert.False(capabilities.Supports(EventContract("interaction/event/other", 'c')));
        Assert.Throws<ArgumentException>(() => new DomainEventPublisherCapabilities([first, first]));
    }

    [Fact]
    public void PublisherCatalog_DerivesExactResolutionFromCapabilitiesAndRejectsOverlap()
    {
        var first = EventContract("interaction/event/first", 'a');
        var second = EventContract("interaction/event/second", 'b');
        var publisher = new StubPublisher(first, second);

        var catalog = new DomainEventPublisherCatalog([publisher]);

        Assert.Equal(2, catalog.Count);
        Assert.True(catalog.TryResolve(first, out var resolved));
        Assert.Same(publisher, resolved);
        Assert.False(catalog.TryResolve(EventContract("interaction/event/other", 'c'), out _));
        Assert.Throws<ArgumentException>(() => new DomainEventPublisherCatalog([
            publisher,
            new StubPublisher(second)
        ]));
    }

    [Fact]
    public void Acknowledgement_RejectsUnmaterializedEvidence()
    {
        Assert.Throws<ArgumentException>(() =>
            new DomainEventPublicationAcknowledgement(PortableValue.Unknown(StringContract)));
    }

    [Fact]
    public void InboxEntry_RetainsExactCanonicalEnvelopeKeyFingerprintAndStableAcknowledgement()
    {
        var envelope = Envelope(
            EventContract("interaction/event/reviewed", 'a'),
            new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit));
        var key = DomainEventPublicationDeduplicationKey.From(envelope);
        var fingerprint = InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(envelope);
        var acknowledgement = new DomainEventPublicationAcknowledgement(
            PortableValue.Concrete(StringContract, ObservationValue.FromString("inbox/entry/1")));

        var entry = new DomainEventInboxEntry(
            key,
            envelope,
            fingerprint,
            new DateTimeOffset(2026, 8, 15, 20, 0, 0, TimeSpan.Zero),
            acknowledgement);

        Assert.Same(envelope, entry.DomainEvent);
        Assert.Equal(key, entry.DeduplicationKey);
        Assert.Equal(fingerprint, entry.ContentFingerprint);
        Assert.Same(acknowledgement, entry.Acknowledgement);
    }

    [Fact]
    public void InboxEntry_RejectsForeignKeyFingerprintAndNonUtcAcceptance()
    {
        var envelope = Envelope(
            EventContract("interaction/event/reviewed", 'a'),
            new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit));
        var key = DomainEventPublicationDeduplicationKey.From(envelope);
        var fingerprint = InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(envelope);
        var acknowledgement = new DomainEventPublicationAcknowledgement();
        var foreignKey = new DomainEventPublicationDeduplicationKey(
            key.AuthorityScope,
            key.Contract,
            new("idempotency/foreign"));

        Assert.Throws<ArgumentException>(() => new DomainEventInboxEntry(
            foreignKey,
            envelope,
            fingerprint,
            new DateTimeOffset(2026, 8, 15, 20, 0, 0, TimeSpan.Zero),
            acknowledgement));
        Assert.Throws<ArgumentException>(() => new DomainEventInboxEntry(
            key,
            envelope,
            new("sha256-v1:foreign"),
            new DateTimeOffset(2026, 8, 15, 20, 0, 0, TimeSpan.Zero),
            acknowledgement));
        Assert.Throws<ArgumentException>(() => new DomainEventInboxEntry(
            key,
            envelope,
            fingerprint,
            new DateTimeOffset(2026, 8, 15, 20, 0, 0, TimeSpan.FromHours(1)),
            acknowledgement));
    }

    static DomainEventEnvelope Envelope(
        DomainEventContractReference contract,
        InteractionDeliveryRequirements delivery) => new(
        InteractionEnvelope.CurrentSchemaVersion,
        new(
            new("emission/event/reviewed"),
            new TransitionInteractionOrigin(
                DefinitionReference("transition/review", 'd'),
                new("emit/reviewed"),
                new(new("DqCase"), new("dq-case/1")),
                new("outcome/approved")),
            new("correlation/review-1"),
            causationId: null,
            new("authority/motion", "tenant/acme"),
            new("idempotency/emission/event/reviewed"),
            ordering: null,
            delivery,
            Provenance()),
        contract,
        PortableValue.Concrete(StringContract, ObservationValue.FromString("reviewed")));

    static DomainEventContractReference EventContract(string id, char fingerprintDigit) => new(
        DefinitionReference(id, fingerprintDigit));

    static ExecutionDefinitionReference DefinitionReference(string id, char fingerprintDigit) => new(
        new(id),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string(fingerprintDigit, 64)));

    static ExecutionProvenance Provenance() => new(
        new("domain-event-publication-tests", "1"),
        new("tests/execution-kernel/domain-event-publication"),
        DocumentOrigin.Generated);

    sealed class StubPublisher(params DomainEventContractReference[] contracts) : IDomainEventPublisher
    {
        public DomainEventPublisherCapabilities Capabilities { get; } = new([.. contracts]);

        public ValueTask<DomainEventPublicationAcknowledgement> PublishAsync(
            OperationContext context,
            DomainEventPublicationInvocation invocation) =>
            throw new NotSupportedException();
    }
}
