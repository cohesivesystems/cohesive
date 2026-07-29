using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable diagnostics emitted while validating canonical interaction contracts.</summary>
public static class InteractionContractDiagnosticCodes
{
    /// <summary>An interaction value schema is not portable or internally coherent.</summary>
    public const string ValueSchemaInvalid = "interactions.contract.valueSchema.invalid";
}

/// <summary>Static semantic and portability validation for canonical interaction contracts.</summary>
public static class InteractionContractValidator
{
    /// <summary>Validates an interaction contract without a graph for resolving named portable types.</summary>
    /// <param name="definition">Canonical interaction contract to validate.</param>
    /// <returns>Deterministically ordered interaction diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult Validate(InteractionContractDefinition definition) =>
        ValidateCore(definition, graph: null);

    /// <summary>Validates an interaction contract using a graph that resolves named portable types.</summary>
    /// <param name="definition">Canonical interaction contract to validate.</param>
    /// <param name="graph">Shape graph used to resolve named and qualified value contracts.</param>
    /// <returns>Deterministically ordered interaction diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="graph"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult Validate(
        InteractionContractDefinition definition,
        ShapeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return ValidateCore(definition, graph);
    }

    static DocumentValidationResult ValidateCore(
        InteractionContractDefinition definition,
        ShapeGraph? graph)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<DocumentValidationDiagnostic> diagnostics = [];
        switch (definition)
        {
            case DomainEventContractDefinition domainEvent:
                ValidateSchema(domainEvent.Payload, "/payload", graph, diagnostics);
                break;
            case SignalContractDefinition signal:
                ValidateSchema(signal.Payload, "/payload", graph, diagnostics);
                break;
            case RequestContractDefinition request:
                ValidateSchema(request.Payload, "/payload", graph, diagnostics);
                for (var index = 0; index < request.Response.TerminalOutcomes.Length; index++)
                {
                    ValidateSchema(
                        request.Response.TerminalOutcomes[index].Schema,
                        $"/response/terminalOutcomes/{index}/schema",
                        graph,
                        diagnostics);
                }
                break;
            case ReplyContractDefinition:
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(definition),
                    definition.GetType(),
                    "Unsupported interaction contract definition type.");
        }

        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static void ValidateSchema(
        InteractionValueSchema schema,
        string location,
        ShapeGraph? graph,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var validation = graph is null
            ? PortableExecutionValidator.Validate(schema.Contract)
            : PortableExecutionValidator.Validate(schema.Contract, graph);
        foreach (var diagnostic in validation.Diagnostics)
        {
            diagnostics.Add(diagnostic with
            {
                Code = InteractionContractDiagnosticCodes.ValueSchemaInvalid,
                Location = Prefix(location + "/contract", diagnostic.Location),
                Evidence = MergeEvidence(diagnostic.Evidence, diagnostic.Code)
            });
        }
    }

    static DocumentDiagnosticEvidence MergeEvidence(DocumentDiagnosticEvidence? evidence, string portableCode) =>
        new(
            stage: "interactionContractValidation",
            subject: evidence?.Subject,
            relatedLocations: evidence?.RelatedLocations ?? [],
            sourceReferences: evidence?.SourceReferences ?? [],
            resolutionOptions: evidence?.ResolutionOptions ?? [],
            expected: evidence?.Expected ?? "portable Cohesive value schema",
            observed: evidence?.Observed ?? portableCode);

    static string Prefix(string prefix, string? location)
    {
        if (string.IsNullOrEmpty(location) || location == "$")
            return prefix;
        return location[0] == '/' ? prefix + location : prefix;
    }
}
