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
    public void Acknowledgement_RejectsUnmaterializedEvidence()
    {
        Assert.Throws<ArgumentException>(() =>
            new DomainEventPublicationAcknowledgement(PortableValue.Unknown(StringContract)));
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
}
