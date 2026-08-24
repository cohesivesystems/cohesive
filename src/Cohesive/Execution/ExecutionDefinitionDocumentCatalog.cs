using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Immutable exact-reference catalog of canonical execution-definition documents.</summary>
/// <remarks>
/// <para>
/// The catalog is an integrity-checked index over authoritative <see cref="ExecutionDefinitionDocument"/>
/// instances. It does not copy definition semantics or become another definition authority.
/// </para>
/// <para>
/// Resolution always uses the complete definition identity, revision, and fingerprint tuple. The catalog
/// intentionally exposes no latest-revision or partial-reference lookup.
/// </para>
/// </remarks>
public sealed class ExecutionDefinitionDocumentCatalog
{
    readonly ImmutableArray<Entry> entries;
    readonly ImmutableArray<ExecutionDefinitionDocument> documents;

    ExecutionDefinitionDocumentCatalog(ImmutableArray<Entry> entries)
    {
        this.entries = entries;
        var builder = ImmutableArray.CreateBuilder<ExecutionDefinitionDocument>(entries.Length);
        foreach (var entry in entries)
            builder.Add(entry.Document);
        documents = builder.MoveToImmutable();
    }

    /// <summary>Number of exact canonical definition documents in the catalog.</summary>
    public int Count => entries.Length;

    /// <summary>Canonical documents in deterministic exact-reference order.</summary>
    public ImmutableArray<ExecutionDefinitionDocument> Documents => documents;

    /// <summary>Attempts to assemble an integrity-checked exact definition-document catalog.</summary>
    /// <param name="documents">Canonical execution-definition documents to validate and index.</param>
    /// <param name="catalog">Receives the catalog only when every document and revision is valid.</param>
    /// <returns>Deterministically ordered integrity and revision-uniqueness diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="documents"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A document has no canonical semantic JSON representation.</exception>
    /// <exception cref="NotSupportedException">A document contains an unsupported runtime type.</exception>
    /// <exception cref="System.Text.Json.JsonException">A document violates the strict JSON contract.</exception>
    public static DocumentValidationResult TryCreate(
        IEnumerable<ExecutionDefinitionDocument> documents,
        out ExecutionDefinitionDocumentCatalog? catalog) =>
        TryCreateCore(documents, graph: null, out catalog);

    /// <summary>Attempts to assemble a catalog using a graph that resolves named portable types.</summary>
    /// <param name="documents">Canonical execution-definition documents to validate and index.</param>
    /// <param name="graph">Shape graph used to resolve named and qualified extension value contracts.</param>
    /// <param name="catalog">Receives the catalog only when every document and revision is valid.</param>
    /// <returns>Deterministically ordered integrity and revision-uniqueness diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="documents"/> or <paramref name="graph"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">A document has no canonical semantic JSON representation.</exception>
    /// <exception cref="NotSupportedException">A document contains an unsupported runtime type.</exception>
    /// <exception cref="System.Text.Json.JsonException">A document violates the strict JSON contract.</exception>
    public static DocumentValidationResult TryCreate(
        IEnumerable<ExecutionDefinitionDocument> documents,
        ShapeGraph graph,
        out ExecutionDefinitionDocumentCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return TryCreateCore(documents, graph, out catalog);
    }

    /// <summary>Attempts to resolve one complete exact definition reference.</summary>
    /// <param name="reference">Exact definition identity, revision, and fingerprint.</param>
    /// <param name="document">Receives the authoritative canonical document on an exact match.</param>
    /// <returns><see langword="true"/> only when the complete exact reference resolves.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    public bool TryResolve(
        ExecutionDefinitionReference reference,
        [NotNullWhen(true)] out ExecutionDefinitionDocument? document)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var status = Resolve(reference, out var entry);
        document = status == ExecutionDefinitionDocumentResolutionStatus.Resolved
            ? entry!.Document
            : null;
        return document is not null;
    }

    /// <summary>Validates and resolves one complete exact definition reference.</summary>
    /// <param name="reference">Exact definition identity, revision, and fingerprint.</param>
    /// <param name="location">Diagnostic location assigned to the reference.</param>
    /// <param name="document">Receives the authoritative canonical document on an exact match.</param>
    /// <returns>
    /// A valid result on an exact match, or structured unknown-identity, unknown-revision, or
    /// fingerprint-incompatibility evidence.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="location"/> is empty or white-space.</exception>
    public DocumentValidationResult ValidateReference(
        ExecutionDefinitionReference reference,
        string location,
        [NotNullWhen(true)] out ExecutionDefinitionDocument? document)
    {
        ArgumentNullException.ThrowIfNull(reference);
        Guard.RequireNotNullOrWhiteSpace(location);
        var status = Resolve(reference, out var entry);
        if (status == ExecutionDefinitionDocumentResolutionStatus.Resolved)
        {
            document = entry!.Document;
            return DocumentValidationResult.Valid;
        }

        document = null;
        return status switch
        {
            ExecutionDefinitionDocumentResolutionStatus.DefinitionUnknown => Error(
                ExecutionDefinitionDiagnosticCodes.DefinitionIdentityUnknown,
                $"Execution definition '{reference.DefinitionId.Value}' is unknown.",
                location + "/definitionId"),
            ExecutionDefinitionDocumentResolutionStatus.RevisionUnknown => Error(
                ExecutionDefinitionDiagnosticCodes.RevisionUnsupported,
                $"Execution revision '{reference.RevisionId.Value}' is unknown for definition "
                + $"'{reference.DefinitionId.Value}'.",
                location + "/revisionId"),
            ExecutionDefinitionDocumentResolutionStatus.FingerprintMismatch => Error(
                ExecutionDefinitionDiagnosticCodes.FingerprintIncompatible,
                "The execution-definition fingerprint is incompatible with the exact catalog revision.",
                location + "/fingerprint"),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported resolution status.")
        };
    }

    internal ExecutionDefinitionDocumentResolutionStatus Resolve(
        ExecutionDefinitionReference reference,
        out Entry? entry)
    {
        var identityStart = LowerBound(reference.DefinitionId);
        if (identityStart == entries.Length
            || entries[identityStart].Reference.DefinitionId != reference.DefinitionId)
        {
            entry = null;
            return ExecutionDefinitionDocumentResolutionStatus.DefinitionUnknown;
        }

        for (var index = identityStart;
             index < entries.Length && entries[index].Reference.DefinitionId == reference.DefinitionId;
             index++)
        {
            var candidate = entries[index];
            if (candidate.Reference.RevisionId != reference.RevisionId)
                continue;
            if (candidate.Reference.Fingerprint != reference.Fingerprint)
            {
                entry = candidate;
                return ExecutionDefinitionDocumentResolutionStatus.FingerprintMismatch;
            }

            entry = candidate;
            return ExecutionDefinitionDocumentResolutionStatus.Resolved;
        }

        entry = null;
        return ExecutionDefinitionDocumentResolutionStatus.RevisionUnknown;
    }

    int LowerBound(ExecutionDefinitionId definitionId)
    {
        var low = 0;
        var high = entries.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (StringComparer.Ordinal.Compare(
                    entries[middle].Reference.DefinitionId.Value,
                    definitionId.Value) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    static DocumentValidationResult TryCreateCore(
        IEnumerable<ExecutionDefinitionDocument> documents,
        ShapeGraph? graph,
        out ExecutionDefinitionDocumentCatalog? catalog)
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
                    ExecutionDefinitionDiagnosticCodes.CatalogDocumentInvalid,
                    DiagnosticSeverity.Error,
                    "An execution-definition document catalog cannot contain a null document.",
                    $"/definitions/{index}"));
                index++;
                continue;
            }

            var validation = ExecutionDefinitionDocumentValidator.Validate(document, graph);
            foreach (var diagnostic in validation.Diagnostics)
            {
                diagnostics.Add(diagnostic with
                {
                    Location = Prefix($"/definitions/{index}", diagnostic.Location)
                });
            }

            if (validation.IsValid)
            {
                candidates.Add(new(
                    Reference(document),
                    document));
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
                    ExecutionDefinitionDiagnosticCodes.CatalogRevisionDuplicate,
                    DiagnosticSeverity.Error,
                    $"Execution definition '{current.Reference.DefinitionId.Value}' revision "
                    + $"'{current.Reference.RevisionId.Value}' is duplicated.",
                    "/definitions"));
            }
        }

        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        var result = DocumentValidationResult.FromDiagnostics(diagnostics);
        catalog = result.IsValid
            ? new([.. candidates])
            : null;
        return result;
    }

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) =>
        new(
            document.Metadata.DefinitionId,
            document.Metadata.RevisionId,
            document.Metadata.Fingerprint);

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

    internal sealed record Entry(
        ExecutionDefinitionReference Reference,
        ExecutionDefinitionDocument Document);
}

internal enum ExecutionDefinitionDocumentResolutionStatus
{
    Resolved,
    DefinitionUnknown,
    RevisionUnknown,
    FingerprintMismatch
}
