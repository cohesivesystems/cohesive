using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable diagnostics emitted while validating canonical interaction envelopes.</summary>
public static class InteractionEnvelopeDiagnosticCodes
{
    /// <summary>The envelope uses an exact schema version not implemented by this reader.</summary>
    public const string SchemaVersionUnsupported = "interactions.envelope.schemaVersion.unsupported";

    /// <summary>The envelope schema is implemented locally but excluded by the admitting interpreter.</summary>
    public const string SchemaVersionInterpreterUnsupported =
        "interactions.envelope.schemaVersion.interpreterUnsupported";

    /// <summary>A payload, ordering key, or terminal result violates portable value semantics.</summary>
    public const string ValueInvalid = "interactions.envelope.value.invalid";

    /// <summary>An envelope payload does not match its resolved contract payload schema.</summary>
    public const string PayloadContractMismatch = "interactions.envelope.payload.contractMismatch";

    /// <summary>A Reply terminal outcome is not declared by its exact Request contract.</summary>
    public const string OutcomeUnknown = "interactions.envelope.reply.outcome.unknown";

    /// <summary>A Reply terminal-outcome discriminator differs from its contract declaration.</summary>
    public const string OutcomeKindMismatch = "interactions.envelope.reply.outcome.kindMismatch";

    /// <summary>A Reply result value does not match its declared terminal-outcome schema.</summary>
    public const string OutcomeContractMismatch = "interactions.envelope.reply.outcome.contractMismatch";
}

/// <summary>Static portability, schema-compatibility, and exact interaction-contract link validation.</summary>
/// <remarks>
/// Process-token liveness and execution definition/node links for origins and Transition targets are validated by
/// the consuming Process or Transition compiler/runtime, which owns the referenced definition and continuation
/// state. This validator does not weaken or infer those links from envelope data alone.
/// </remarks>
public static class InteractionEnvelopeValidator
{
    static readonly ExecutionIrSchemaCompatibilityDeclaration CurrentSchemaCompatibility =
        new([InteractionEnvelope.CurrentSchemaVersion]);

    /// <summary>Validates a canonical interaction envelope.</summary>
    /// <param name="envelope">Envelope to validate.</param>
    /// <param name="contracts">Exact interaction catalog used to validate family, payload, and Reply outcome links.</param>
    /// <param name="graph">Optional shape graph used to resolve named portable values.</param>
    /// <param name="schemaCompatibility">
    /// Optional exact envelope schemas admitted by the consuming interpreter, in addition to local implementation
    /// support.
    /// </param>
    /// <returns>Deterministically ordered compatibility, linking, and portability diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="envelope"/> or <paramref name="contracts"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult Validate(
        InteractionEnvelope envelope,
        InteractionContractCatalog contracts,
        ShapeGraph? graph = null,
        ExecutionIrSchemaCompatibilityDeclaration? schemaCompatibility = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(contracts);

        List<DocumentValidationDiagnostic> diagnostics = [];
        if (!CurrentSchemaCompatibility.Supports(envelope.SchemaVersion))
        {
            diagnostics.Add(new(
                InteractionEnvelopeDiagnosticCodes.SchemaVersionUnsupported,
                DiagnosticSeverity.Error,
                $"Interaction envelope schema '{envelope.SchemaVersion.Value}' is not implemented by this reader.",
                "/schemaVersion"));
        }
        else if (schemaCompatibility is not null && !schemaCompatibility.Supports(envelope.SchemaVersion))
        {
            diagnostics.Add(new(
                InteractionEnvelopeDiagnosticCodes.SchemaVersionInterpreterUnsupported,
                DiagnosticSeverity.Error,
                $"Interaction envelope schema '{envelope.SchemaVersion.Value}' is not admitted by the consuming interpreter.",
                "/schemaVersion"));
        }

        if (envelope.Context.Ordering is { } ordering)
            AddPortableDiagnostics(ordering.Key, "/context/ordering/key", graph, diagnostics);

        switch (envelope)
        {
            case DomainEventEnvelope domainEvent:
                ValidatePayload(
                    domainEvent.Payload,
                    domainEvent.Contract,
                    contracts,
                    graph,
                    diagnostics);
                break;
            case RequestEnvelope request:
                ValidatePayload(
                    request.Payload,
                    request.Contract,
                    contracts,
                    graph,
                    diagnostics);
                break;
            case SignalEnvelope signal:
                ValidatePayload(
                    signal.Payload,
                    signal.Contract,
                    contracts,
                    graph,
                    diagnostics);
                break;
            case ReplyEnvelope reply:
                ValidateReply(reply, contracts, graph, diagnostics);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(envelope),
                    envelope.GetType(),
                    "Unsupported interaction envelope type.");
        }

        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static void ValidatePayload(
        PortableValue payload,
        InteractionContractReference reference,
        InteractionContractCatalog contracts,
        ShapeGraph? graph,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        AddPortableDiagnostics(payload, "/payload", graph, diagnostics);
        var referenceValidation = contracts.ValidateReference(reference, "/contract", out var definition);
        AddDiagnostics(referenceValidation, diagnostics);
        if (definition is null)
            return;

        var expected = definition switch
        {
            DomainEventContractDefinition domainEvent => domainEvent.Payload.Contract,
            RequestContractDefinition request => request.Payload.Contract,
            SignalContractDefinition signal => signal.Payload.Contract,
            _ => null
        };
        if (expected is null || payload.Contract != expected)
        {
            diagnostics.Add(new(
                InteractionEnvelopeDiagnosticCodes.PayloadContractMismatch,
                DiagnosticSeverity.Error,
                "The interaction payload contract differs from its exact resolved contract definition.",
                "/payload/contract",
                Evidence: new(
                    stage: "interactionEnvelopeLinking",
                    subject: reference.Definition.DefinitionId.Value,
                    expected: expected?.ToString() ?? "payload-bearing interaction contract",
                    observed: payload.Contract.ToString())));
        }
    }

    static void ValidateReply(
        ReplyEnvelope reply,
        InteractionContractCatalog contracts,
        ShapeGraph? graph,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        AddPortableDiagnostics(reply.Outcome.Value, "/outcome/value", graph, diagnostics);
        var replyReferenceValidation = contracts.ValidateReference(
            reply.Contract,
            "/contract",
            out var replyDefinition);
        AddDiagnostics(replyReferenceValidation, diagnostics);
        if (replyDefinition is not ReplyContractDefinition replyContract)
            return;

        var requestReferenceValidation = contracts.ValidateReference(
            replyContract.Request,
            "/contract/request",
            out var requestDefinition);
        AddDiagnostics(requestReferenceValidation, diagnostics);
        if (requestDefinition is not RequestContractDefinition request)
            return;

        var expected = request.Response.Find(reply.Outcome.Id);
        if (expected is null || replyContract.Outcome != reply.Outcome.Id)
        {
            diagnostics.Add(new(
                InteractionEnvelopeDiagnosticCodes.OutcomeUnknown,
                DiagnosticSeverity.Error,
                $"Reply outcome '{reply.Outcome.Id.Value}' is not selected by its exact Reply and Request contracts.",
                "/outcome/id"));
            return;
        }

        if (!OutcomeKindsMatch(expected, reply.Outcome))
        {
            diagnostics.Add(new(
                InteractionEnvelopeDiagnosticCodes.OutcomeKindMismatch,
                DiagnosticSeverity.Error,
                "The Reply terminal-outcome discriminator differs from its Request declaration.",
                "/outcome"));
        }
        if (reply.Outcome.Value.Contract != expected.Schema.Contract)
        {
            diagnostics.Add(new(
                InteractionEnvelopeDiagnosticCodes.OutcomeContractMismatch,
                DiagnosticSeverity.Error,
                "The Reply result contract differs from the declared Request terminal-outcome schema.",
                "/outcome/value/contract"));
        }
    }

    static bool OutcomeKindsMatch(
        RequestTerminalOutcomeDefinition definition,
        RequestTerminalOutcome outcome) =>
        (definition, outcome) switch
        {
            (RequestResultDefinition, RequestResultOutcome) => true,
            (RequestFailureDefinition, RequestFailureOutcome) => true,
            (RequestTimeoutDefinition, RequestTimeoutOutcome) => true,
            (RequestCancellationDefinition, RequestCancellationOutcome) => true,
            _ => false
        };

    static void AddPortableDiagnostics(
        PortableValue value,
        string location,
        ShapeGraph? graph,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var validation = PortableExecutionValidator.Validate(value, graph);
        foreach (var diagnostic in validation.Diagnostics)
        {
            diagnostics.Add(diagnostic with
            {
                Code = InteractionEnvelopeDiagnosticCodes.ValueInvalid,
                Location = Prefix(location, diagnostic.Location),
                Evidence = new(
                    stage: "interactionEnvelopeValidation",
                    subject: diagnostic.Evidence?.Subject,
                    relatedLocations: diagnostic.Evidence?.RelatedLocations ?? [],
                    sourceReferences: diagnostic.Evidence?.SourceReferences ?? [],
                    resolutionOptions: diagnostic.Evidence?.ResolutionOptions ?? [],
                    expected: diagnostic.Evidence?.Expected ?? "portable Cohesive value",
                    observed: diagnostic.Evidence?.Observed ?? diagnostic.Code)
            });
        }
    }

    static string Prefix(string prefix, string? location) =>
        string.IsNullOrEmpty(location) || location == "$"
            ? prefix
            : location[0] == '/'
                ? prefix + location
                : prefix;

    static void AddDiagnostics(
        DocumentValidationResult validation,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        foreach (var diagnostic in validation.Diagnostics)
            diagnostics.Add(diagnostic);
    }
}
