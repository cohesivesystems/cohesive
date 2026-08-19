using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Typed C# handle for one canonical domain-event contract.</summary>
/// <remarks>
/// The CLR payload type is authoring input from which the portable payload contract is derived. The persisted
/// <see cref="Document"/> remains the semantic authority; durable event identity, revisions, and provenance are
/// always supplied explicitly rather than inferred from CLR names.
/// </remarks>
/// <typeparam name="TPayload">CLR payload type projected into the portable event-value contract.</typeparam>
public sealed class AuthoredDomainEventContract<TPayload>
{
    internal AuthoredDomainEventContract(
        ExecutionDefinitionDocument document,
        DocumentValidationResult validation)
    {
        Document = Guard.RequireNotNull(document);
        Validation = Guard.RequireNotNull(validation);
    }

    /// <summary>Canonical persisted execution-definition document and sole durable semantic authority.</summary>
    public ExecutionDefinitionDocument Document { get; }

    /// <summary>Canonical document and portable payload-contract validation diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Whether canonical document and interaction-contract validation found no errors.</summary>
    public bool IsValid => Validation.IsValid;

    /// <summary>Typed projection of the canonical domain-event definition.</summary>
    /// <exception cref="System.Text.Json.JsonException">
    /// The canonical payload cannot be projected as a domain-event contract.
    /// </exception>
    /// <exception cref="NotSupportedException">The strict execution serializer does not support a payload value.</exception>
    /// <exception cref="InvalidOperationException">The canonical document does not contain a domain-event contract.</exception>
    public DomainEventContractDefinition Definition =>
        Document.GetDefinition<InteractionContractDefinition>() as DomainEventContractDefinition
        ?? throw new InvalidOperationException("The authored document does not contain a domain-event contract.");

    /// <summary>Exact typed reference to this canonical domain-event contract.</summary>
    public DomainEventContractReference Reference => new(new(
        Document.Metadata.DefinitionId,
        Document.Metadata.RevisionId,
        Document.Metadata.Fingerprint));
}

/// <summary>Produces canonical interaction contracts from typed C# payload declarations.</summary>
public static partial class InteractionContractAuthoring
{
    static readonly IClrTypeRefMapper TypeMapper = new DefaultClrTypeRefMapper();

    /// <summary>Authors a canonical domain-event contract whose portable payload schema is derived from C#.</summary>
    /// <typeparam name="TPayload">CLR payload type projected into a portable value contract.</typeparam>
    /// <param name="definitionId">Stable identity shared by every revision of the domain event.</param>
    /// <param name="revisionId">Stable identity of the accepted domain-event contract revision.</param>
    /// <param name="payloadRevision">Exact semantic revision of the derived payload schema.</param>
    /// <param name="provenance">Producer and root-source attribution for the authored definition.</param>
    /// <param name="extensions">Optional exact-versioned semantic extensions.</param>
    /// <param name="displayName">Optional human-facing name excluded from semantic fingerprinting.</param>
    /// <param name="description">Optional human-facing description excluded from semantic fingerprinting.</param>
    /// <returns>
    /// A typed handle containing the canonical document, exact reference, and retained validation diagnostics.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// An identity, revision, extension, or descriptive metadata value is invalid.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="provenance"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// The authored canonical content cannot be encoded by the strict execution serializer.
    /// </exception>
    /// <exception cref="InvalidOperationException">The canonical content has no stable JSON representation.</exception>
    /// <exception cref="NotSupportedException">The canonical content contains an unsupported runtime type.</exception>
    public static AuthoredDomainEventContract<TPayload> CreateDomainEvent<TPayload>(
        ExecutionDefinitionId definitionId,
        ExecutionRevisionId revisionId,
        InteractionValueSchemaRevision payloadRevision,
        ExecutionProvenance provenance,
        ImmutableArray<ExecutionDefinitionExtension> extensions = default,
        string? displayName = null,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        var definition = new DomainEventContractDefinition(new(
            new ValueContract(TypeMapper.Map(typeof(TPayload), null)),
            payloadRevision));
        var initial = InteractionContractDocuments.Create(
            definitionId,
            revisionId,
            definition,
            provenance,
            extensions,
            displayName,
            description);
        var validation = InteractionContractDocuments.Validate(initial);
        var document = InteractionContractDocuments.Create(
            definitionId,
            revisionId,
            definition,
            provenance,
            extensions,
            displayName,
            description,
            diagnostics: validation.Diagnostics);
        return new(document, validation);
    }
}
