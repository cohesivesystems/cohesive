using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class InteractionContractAuthoringTests
{
    [Fact]
    public void CreateDomainEvent_DerivesPortablePayloadAndRetainsExplicitAuthority()
    {
        var provenance = new ExecutionProvenance(
            new("ari.training.events", "1"),
            new("ari/ari-355/training-example-generated"),
            DocumentOrigin.User);

        var authored = InteractionContractAuthoring.CreateDomainEvent<TrainingExampleGenerated>(
            new("ari/event/training-example-generated"),
            new("1"),
            new("ari/training-example-generated/v1"),
            provenance,
            displayName: "Training example generated");

        Assert.True(authored.IsValid, FormatDiagnostics(authored.Validation));
        Assert.Equal(InteractionContractDocuments.Kind, authored.Document.Kind);
        Assert.Equal(new ExecutionDefinitionId("ari/event/training-example-generated"), authored.Document.Metadata.DefinitionId);
        Assert.Equal(new ExecutionRevisionId("1"), authored.Document.Metadata.RevisionId);
        Assert.Equal(provenance, authored.Document.Metadata.Provenance);
        Assert.Equal("Training example generated", authored.Document.Metadata.DisplayName);
        Assert.Equal(authored.Validation.Diagnostics, authored.Document.Metadata.Diagnostics);

        var definition = authored.Definition;
        Assert.Equal(new InteractionValueSchemaRevision("ari/training-example-generated/v1"), definition.Payload.Revision);
        Assert.Equal(
            new DefaultClrTypeRefMapper().Map(typeof(TrainingExampleGenerated), null),
            definition.Payload.Contract.Type);
        var payload = Assert.IsType<ObjectTypeRef>(definition.Payload.Contract.Type);
        Assert.Equal(
            ["DatasetName", "GeneratedAtUtc", "TrainingExampleId"],
            payload.Fields.Select(static field => field.Name));
        Assert.All(payload.Fields, static field =>
        {
            Assert.Equal(FieldPresence.Required, field.Presence);
            Assert.Equal(FieldNullability.NonNullable, field.Nullability);
        });

        Assert.Equal(authored.Document.Metadata.DefinitionId, authored.Reference.Definition.DefinitionId);
        Assert.Equal(authored.Document.Metadata.RevisionId, authored.Reference.Definition.RevisionId);
        Assert.Equal(authored.Document.Metadata.Fingerprint, authored.Reference.Definition.Fingerprint);
    }

    [Fact]
    public void CreateDomainEvent_ProducesDeterministicDocuments()
    {
        var first = Create<TrainingExampleGenerated>();
        var second = Create<TrainingExampleGenerated>();

        Assert.Equal(first.Document, second.Document);
        Assert.Equal(first.Reference, second.Reference);
    }

    [Fact]
    public void CreateDomainEvent_RetainsUnsupportedClrShapeDiagnostics()
    {
        var authored = Create<RecursiveEvent>();

        Assert.False(authored.IsValid);
        var diagnostic = Assert.Single(authored.Validation.Diagnostics);
        Assert.Equal(InteractionContractDiagnosticCodes.ValueSchemaInvalid, diagnostic.Code);
        Assert.Equal(diagnostic, Assert.Single(authored.Document.Metadata.Diagnostics));
        var payload = Assert.IsType<ObjectTypeRef>(authored.Definition.Payload.Contract.Type);
        Assert.IsType<OpaqueRuntimeTypeRef>(Assert.Single(payload.Fields).Type);
    }

    static AuthoredDomainEventContract<TPayload> Create<TPayload>() =>
        InteractionContractAuthoring.CreateDomainEvent<TPayload>(
            new("tests/event/domain-event"),
            new("revision/1"),
            new("tests/event/payload/v1"),
            new(
                new("interaction-authoring-tests", "1"),
                new("tests/execution-kernel/interaction-authoring"),
                DocumentOrigin.Generated));

    static string FormatDiagnostics(DocumentValidationResult validation) =>
        string.Join(Environment.NewLine, validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Severity} {diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}"));

    sealed record TrainingExampleGenerated(
        string TrainingExampleId,
        string DatasetName,
        DateTimeOffset GeneratedAtUtc);

    sealed class RecursiveEvent
    {
        public RecursiveEvent? Parent { get; init; }
    }
}
