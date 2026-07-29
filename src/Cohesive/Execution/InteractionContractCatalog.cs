using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable diagnostics emitted while assembling and resolving an interaction-contract catalog.</summary>
public static class InteractionContractCatalogDiagnosticCodes
{
    /// <summary>A catalog input is not a valid canonical interaction-contract document.</summary>
    public const string DocumentInvalid = "interactions.catalog.document.invalid";

    /// <summary>More than one contract occupies the same definition identity and semantic revision.</summary>
    public const string DuplicateRevision = "interactions.catalog.revision.duplicate";

    /// <summary>A referenced interaction definition identity is absent from the catalog.</summary>
    public const string DefinitionUnknown = "interactions.catalog.definition.unknown";

    /// <summary>A referenced semantic revision is absent for a known interaction definition.</summary>
    public const string RevisionUnknown = "interactions.catalog.revision.unknown";

    /// <summary>A referenced fingerprint differs from the catalog contract at the same identity and revision.</summary>
    public const string FingerprintMismatch = "interactions.catalog.fingerprint.mismatch";

    /// <summary>The typed reference family differs from the resolved contract family.</summary>
    public const string ContractKindMismatch = "interactions.catalog.contractKind.mismatch";

    /// <summary>A Reply selects a terminal outcome absent from its Request contract.</summary>
    public const string ReplyOutcomeUnknown = "interactions.catalog.reply.outcome.unknown";
}

/// <summary>Immutable exact-reference catalog for canonical interaction contracts.</summary>
/// <remarks>
/// The catalog is a linking interpretation over persisted <see cref="ExecutionDefinitionDocument"/> instances.
/// It does not become another source of contract semantics.
/// </remarks>
public sealed class InteractionContractCatalog
{
    readonly ImmutableArray<Entry> entries;

    InteractionContractCatalog(ImmutableArray<Entry> entries, ShapeGraph? shapeGraph)
    {
        this.entries = entries;
        ShapeGraph = shapeGraph;
    }

    /// <summary>Number of exact canonical interaction contracts in the catalog.</summary>
    public int Count => entries.Length;

    /// <summary>Contextual named-type and qualified-shape authority retained during catalog linking.</summary>
    public ShapeGraph? ShapeGraph { get; }

    /// <summary>Attempts to assemble a validated exact interaction-contract catalog.</summary>
    /// <param name="documents">Canonical interaction-contract documents to link.</param>
    /// <param name="catalog">Receives the catalog only when every document and cross-reference is valid.</param>
    /// <returns>Deterministically ordered document, uniqueness, and Reply-link diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="documents"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A document has no canonical semantic JSON representation.</exception>
    /// <exception cref="NotSupportedException">A document contains an unsupported runtime type.</exception>
    /// <exception cref="System.Text.Json.JsonException">A document violates the strict JSON contract.</exception>
    public static DocumentValidationResult TryCreate(
        IEnumerable<ExecutionDefinitionDocument> documents,
        out InteractionContractCatalog? catalog) =>
        TryCreateCore(documents, graph: null, out catalog);

    /// <summary>Attempts to assemble a catalog using a graph that resolves named portable types.</summary>
    /// <param name="documents">Canonical interaction-contract documents to link.</param>
    /// <param name="graph">Shape graph used to resolve named and qualified value contracts.</param>
    /// <param name="catalog">Receives the catalog only when every document and cross-reference is valid.</param>
    /// <returns>Deterministically ordered document, uniqueness, and Reply-link diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="documents"/> or <paramref name="graph"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">A document has no canonical semantic JSON representation.</exception>
    /// <exception cref="NotSupportedException">A document contains an unsupported runtime type.</exception>
    /// <exception cref="System.Text.Json.JsonException">A document violates the strict JSON contract.</exception>
    public static DocumentValidationResult TryCreate(
        IEnumerable<ExecutionDefinitionDocument> documents,
        ShapeGraph graph,
        out InteractionContractCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return TryCreateCore(documents, graph, out catalog);
    }

    /// <summary>Attempts to resolve an exact typed contract reference.</summary>
    /// <param name="reference">Exact typed interaction-contract reference.</param>
    /// <param name="definition">Receives the resolved canonical definition when found and type-compatible.</param>
    /// <returns><see langword="true"/> when the exact reference resolves; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    public bool TryResolve(
        InteractionContractReference reference,
        [NotNullWhen(true)] out InteractionContractDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var status = Resolve(reference, out var entry);
        definition = status == ResolutionStatus.Resolved ? entry!.Definition : null;
        return definition is not null;
    }

    /// <summary>Attempts to resolve an exact unclassified definition reference.</summary>
    /// <remarks>
    /// This overload is the linking boundary used by canonical Transition and Process nodes, whose persisted
    /// references remain independent of a duplicated local interaction-kind declaration.
    /// </remarks>
    /// <param name="reference">Exact definition identity, revision, and fingerprint.</param>
    /// <param name="definition">Receives the resolved canonical interaction contract.</param>
    /// <returns><see langword="true"/> when the exact reference resolves; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    public bool TryResolve(
        ExecutionDefinitionReference reference,
        [NotNullWhen(true)] out InteractionContractDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var status = ResolveExact(reference, out var entry);
        definition = status == ResolutionStatus.Resolved ? entry!.Definition : null;
        return definition is not null;
    }

    internal DocumentValidationResult ValidateReference(
        InteractionContractReference reference,
        string location,
        [NotNullWhen(true)] out InteractionContractDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var status = Resolve(reference, out var entry);
        if (status == ResolutionStatus.Resolved)
        {
            definition = entry!.Definition;
            return DocumentValidationResult.Valid;
        }

        definition = null;
        var exact = reference.Definition;
        return status switch
        {
            ResolutionStatus.DefinitionUnknown => Error(
                InteractionContractCatalogDiagnosticCodes.DefinitionUnknown,
                $"Interaction contract '{exact.DefinitionId.Value}' is unknown.",
                location + "/definition/definitionId"),
            ResolutionStatus.RevisionUnknown => Error(
                InteractionContractCatalogDiagnosticCodes.RevisionUnknown,
                $"Interaction revision '{exact.RevisionId.Value}' is unknown for contract '{exact.DefinitionId.Value}'.",
                location + "/definition/revisionId"),
            ResolutionStatus.FingerprintMismatch => Error(
                InteractionContractCatalogDiagnosticCodes.FingerprintMismatch,
                "The interaction contract fingerprint is incompatible with the exact catalog revision.",
                location + "/definition/fingerprint"),
            ResolutionStatus.KindMismatch => Error(
                InteractionContractCatalogDiagnosticCodes.ContractKindMismatch,
                "The typed interaction reference family differs from the resolved canonical contract family.",
                location),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported contract resolution status.")
        };
    }

    ResolutionStatus Resolve(InteractionContractReference reference, out Entry? entry)
    {
        var exact = reference.Definition;
        var status = ResolveExact(exact, out entry);
        if (status != ResolutionStatus.Resolved)
            return status;
        return ReferenceMatches(reference, entry!.Definition)
            ? ResolutionStatus.Resolved
            : ResolutionStatus.KindMismatch;
    }

    ResolutionStatus ResolveExact(ExecutionDefinitionReference exact, out Entry? entry)
    {
        var identityStart = LowerBound(exact.DefinitionId);
        if (identityStart == entries.Length || entries[identityStart].Reference.DefinitionId != exact.DefinitionId)
        {
            entry = null;
            return ResolutionStatus.DefinitionUnknown;
        }

        for (var index = identityStart;
             index < entries.Length && entries[index].Reference.DefinitionId == exact.DefinitionId;
             index++)
        {
            var candidate = entries[index];
            if (candidate.Reference.RevisionId != exact.RevisionId)
                continue;
            if (candidate.Reference.Fingerprint != exact.Fingerprint)
            {
                entry = candidate;
                return ResolutionStatus.FingerprintMismatch;
            }

            entry = candidate;
            return ResolutionStatus.Resolved;
        }

        entry = null;
        return ResolutionStatus.RevisionUnknown;
    }

    int LowerBound(ExecutionDefinitionId definitionId)
    {
        var low = 0;
        var high = entries.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (StringComparer.Ordinal.Compare(entries[middle].Reference.DefinitionId.Value, definitionId.Value) < 0)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    static DocumentValidationResult TryCreateCore(
        IEnumerable<ExecutionDefinitionDocument> documents,
        ShapeGraph? graph,
        out InteractionContractCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(documents);
        List<DocumentValidationDiagnostic> diagnostics = [];
        List<Entry> candidates = [];
        var index = 0;
        foreach (var document in documents)
        {
            if (document is null)
            {
                diagnostics.Add(new(
                    InteractionContractCatalogDiagnosticCodes.DocumentInvalid,
                    DiagnosticSeverity.Error,
                    "An interaction-contract catalog cannot contain a null document.",
                    $"/contracts/{index}"));
                index++;
                continue;
            }

            var validation = graph is null
                ? InteractionContractDocuments.Validate(document)
                : InteractionContractDocuments.Validate(document, graph);
            foreach (var diagnostic in validation.Diagnostics)
            {
                diagnostics.Add(diagnostic with
                {
                    Code = InteractionContractCatalogDiagnosticCodes.DocumentInvalid,
                    Location = Prefix($"/contracts/{index}", diagnostic.Location),
                    Evidence = new(
                        stage: "interactionContractLinking",
                        subject: document.Metadata.DefinitionId.Value,
                        relatedLocations: diagnostic.Evidence?.RelatedLocations ?? [],
                        sourceReferences: diagnostic.Evidence?.SourceReferences ?? [],
                        resolutionOptions: diagnostic.Evidence?.ResolutionOptions ?? [],
                        expected: diagnostic.Evidence?.Expected,
                        observed: diagnostic.Evidence?.Observed ?? diagnostic.Code)
                });
            }

            if (validation.IsValid)
            {
                var definition = document.GetDefinition<InteractionContractDefinition>();
                candidates.Add(new(
                    new(
                        document.Metadata.DefinitionId,
                        document.Metadata.RevisionId,
                        document.Metadata.Fingerprint),
                    definition,
                    index));
            }
            index++;
        }

        candidates.Sort(static (left, right) =>
            ExecutionDefinitionReference.CompareCanonical(left.Reference, right.Reference));
        for (var candidateIndex = 1; candidateIndex < candidates.Count; candidateIndex++)
        {
            var previous = candidates[candidateIndex - 1];
            var current = candidates[candidateIndex];
            if (previous.Reference.DefinitionId == current.Reference.DefinitionId
                && previous.Reference.RevisionId == current.Reference.RevisionId)
            {
                diagnostics.Add(new(
                    InteractionContractCatalogDiagnosticCodes.DuplicateRevision,
                    DiagnosticSeverity.Error,
                    $"Interaction contract '{current.Reference.DefinitionId.Value}' revision '{current.Reference.RevisionId.Value}' is duplicated.",
                    "/contracts"));
            }
        }

        var provisional = new InteractionContractCatalog([.. candidates], graph);
        provisional.ValidateReplyLinks(diagnostics);
        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        var result = DocumentValidationResult.FromDiagnostics(diagnostics);
        catalog = result.IsValid ? provisional : null;
        return result;
    }

    void ValidateReplyLinks(ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Definition is not ReplyContractDefinition reply)
                continue;

            var location = $"/contracts/{entry.SourceIndex}/definition/request";
            var validation = ValidateReference(reply.Request, location, out var requestDefinition);
            foreach (var diagnostic in validation.Diagnostics)
                diagnostics.Add(diagnostic);
            if (!validation.IsValid || requestDefinition is not RequestContractDefinition request)
            {
                continue;
            }

            if (request.Response.Find(reply.Outcome) is null)
            {
                diagnostics.Add(new(
                    InteractionContractCatalogDiagnosticCodes.ReplyOutcomeUnknown,
                    DiagnosticSeverity.Error,
                    $"Reply outcome '{reply.Outcome.Value}' is not declared by Request '{reply.Request.Definition.DefinitionId.Value}'.",
                    $"/contracts/{entry.SourceIndex}/definition/outcome"));
            }
        }
    }

    static bool ReferenceMatches(
        InteractionContractReference reference,
        InteractionContractDefinition definition) =>
        (reference, definition) switch
        {
            (DomainEventContractReference, DomainEventContractDefinition) => true,
            (RequestContractReference, RequestContractDefinition) => true,
            (SignalContractReference, SignalContractDefinition) => true,
            (ReplyContractReference, ReplyContractDefinition) => true,
            _ => false
        };

    static string Prefix(string prefix, string? location) =>
        string.IsNullOrEmpty(location) || location == "$"
            ? prefix
            : location[0] == '/'
                ? prefix + location
                : prefix;

    static DocumentValidationResult Error(string code, string message, string location) =>
        DocumentValidationResult.FromDiagnostics([
            new(code, DiagnosticSeverity.Error, message, location)
        ]);

    sealed record Entry(
        ExecutionDefinitionReference Reference,
        InteractionContractDefinition Definition,
        int SourceIndex);

    enum ResolutionStatus
    {
        Resolved,
        DefinitionUnknown,
        RevisionUnknown,
        FingerprintMismatch,
        KindMismatch
    }
}
