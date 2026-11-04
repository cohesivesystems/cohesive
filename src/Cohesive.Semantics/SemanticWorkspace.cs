using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Semantics;

/// <summary>Read-only inspection session over explicitly exported execution documents.</summary>
/// <remarks>
/// The session retains four immutable snapshots. Every request rereads the bounded bundle and checks declared
/// input hashes. It never runs application code, edits source, or treats a failed refresh as a successful one.
/// Concurrent operations are serialized. Freshness checks read all declared bytes twice per request.
/// </remarks>
public sealed class SemanticWorkspace
{
    const int MaximumSnapshots = 4;
    const int MaximumPageSize = 100;
    const int MaximumInspectionBytes = 16 * 1024;
    const int DiagnosticPageSize = 20;
    readonly string root;
    readonly string bundlePath;
    readonly ImmutableDictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, DocumentValidationResult>> validators;
    readonly Dictionary<string, Snapshot> snapshots = new(StringComparer.Ordinal);
    readonly Queue<string> retention = new();
    readonly object gate = new();
    static readonly ImmutableArray<string> Limitations =
    [
        "Only producer-declared inputs and exported execution documents are covered; input completeness is not proven.",
        "No target realization, cross-document linking, API/client propagation, runtime safety, or invariant proof is claimed.",
        "Comparison reports document fingerprint changes, not compatibility or rename continuity. Edit owning source and re-export."
    ];

    /// <summary>Opens a session without executing or loading application assemblies.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="bundlePath">Relative path to a derived workspace bundle.</param>
    /// <param name="validators">Domain-owned validators keyed by their existing canonical kind; these supplement mandatory shared integrity validation.</param>
    /// <exception cref="ArgumentException">The bundle path escapes the repository, traverses a symbolic link, or a validator has an uninitialized kind or null delegate.</exception>
    public SemanticWorkspace(string root, string bundlePath,
        IReadOnlyDictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, DocumentValidationResult>>? validators = null)
    {
        this.root = Path.GetFullPath(root);
        _ = SemanticWorkspaceExport.ResolvePath(this.root, bundlePath);
        this.bundlePath = bundlePath;
        this.validators = validators?.ToImmutableDictionary() ?? ImmutableDictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, DocumentValidationResult>>.Empty;
        if (this.validators.Any(pair => string.IsNullOrWhiteSpace(pair.Key.Value) || pair.Value is null))
            throw new ArgumentException("Validators require initialized kinds and non-null delegates.", nameof(validators));
    }

    /// <summary>Lists the current exported subjects and obtains the snapshot required by other operations.</summary>
    /// <param name="offset">Zero-based page offset.</param>
    /// <param name="limit">Page size from 1 through 100.</param>
    /// <param name="expectedSnapshot">Optional exact snapshot to require.</param>
    /// <returns>Current revision and a bounded subject page, including source freshness.</returns>
    /// <exception cref="IOException">The bundle cannot be read.</exception>
    /// <exception cref="JsonException">The export is malformed or unsupported.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Pagination is outside the supported range.</exception>
    /// <exception cref="InvalidOperationException">The requested snapshot has changed.</exception>
    /// <exception cref="UnauthorizedAccessException">The bundle cannot be accessed.</exception>
    public WorkspaceResult<WorkspacePage<WorkspaceSubject>> Describe(int offset = 0, int limit = 20, string? expectedSnapshot = null)
        => Read(expectedSnapshot, snapshot => Page(snapshot.Documents.Select(Subject), offset, limit));

    /// <summary>Finds all identity, display-name, or source-reference matches without selecting an ambiguous winner.</summary>
    /// <param name="query">Nonempty case-insensitive substring.</param>
    /// <param name="expectedSnapshot">Snapshot returned by workspace discovery.</param>
    /// <param name="offset">Zero-based page offset.</param>
    /// <param name="limit">Page size from 1 through 100.</param>
    /// <returns>Deterministically ordered candidates.</returns>
    /// <exception cref="ArgumentException">The query or expected snapshot is empty.</exception>
    /// <exception cref="UnauthorizedAccessException">The bundle cannot be accessed.</exception>
    /// <exception cref="InvalidOperationException">The snapshot is stale.</exception>
    /// <exception cref="IOException">The bundle cannot be read.</exception>
    /// <exception cref="JsonException">The bundle is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Pagination is invalid.</exception>
    public WorkspaceResult<WorkspacePage<WorkspaceSubject>> Resolve(string query, string expectedSnapshot, int offset = 0, int limit = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        return Read(RequireSnapshot(expectedSnapshot), snapshot => Page(snapshot.Documents.Select(Subject).Where(s =>
            s.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (s.DisplayName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || s.Source.Contains(query, StringComparison.OrdinalIgnoreCase)), offset, limit));
    }

    /// <summary>Retrieves a bounded canonical subtree and the producer's source mappings.</summary>
    /// <param name="id">Exact definition identity.</param>
    /// <param name="expectedSnapshot">Expected exported revision.</param>
    /// <param name="pointer">RFC 6901 pointer; empty selects the complete document.</param>
    /// <param name="offset">Zero-based child-pointer offset.</param>
    /// <param name="limit">Page size from 1 through 100.</param>
    /// <param name="sourceOffset">Independent zero-based source-reference offset.</param>
    /// <param name="sourceLimit">Source-reference page size from 1 through 100.</param>
    /// <returns>At most 16 KiB of selected JSON, otherwise child pointers for further retrieval.</returns>
    /// <exception cref="KeyNotFoundException">The definition or pointer is absent.</exception>
    /// <exception cref="ArgumentException">The pointer is malformed or the expected snapshot is empty.</exception>
    /// <exception cref="ArgumentNullException">The pointer is null.</exception>
    /// <exception cref="UnauthorizedAccessException">The bundle cannot be accessed.</exception>
    /// <exception cref="InvalidOperationException">The snapshot is stale.</exception>
    /// <exception cref="IOException">The bundle cannot be read.</exception>
    /// <exception cref="JsonException">The bundle is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Pagination is invalid.</exception>
    public WorkspaceResult<WorkspaceInspection> Inspect(string id, string expectedSnapshot, string pointer = "", int offset = 0, int limit = 20, int sourceOffset = 0, int sourceLimit = 20)
        => Read(RequireSnapshot(expectedSnapshot), snapshot =>
        {
            var document = snapshot.Documents.FirstOrDefault(d => d.Metadata.DefinitionId.Value == id)
                ?? throw new KeyNotFoundException($"Unknown definition '{id}'. Use resolve first.");
            var json = snapshot.Json[document.Metadata.DefinitionId];
            var selected = Select(json, pointer);
            var fits = Encoding.UTF8.GetByteCount(selected.GetRawText()) <= MaximumInspectionBytes;
            if (!fits && selected.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                throw new InvalidOperationException("The selected scalar exceeds the inspection byte limit. Inspect it in the local exported artifact.");
            var children = selected.ValueKind switch
            {
                JsonValueKind.Object => selected.EnumerateObject().Select(p => pointer + "/" + Escape(p.Name)),
                JsonValueKind.Array => Enumerable.Range(0, selected.GetArrayLength()).Select(i => pointer + "/" + i),
                _ => []
            };
            return new WorkspaceInspection(Subject(document), pointer,
                fits ? selected : null,
                Page(children, offset, limit),
                Page(document.Metadata.SourceMap.ResolveReferences(pointer, document.Metadata.Provenance.Source.Reference), sourceOffset, sourceLimit));
        });

    /// <summary>Runs registered domain validators and reports their precise coverage.</summary>
    /// <param name="expectedSnapshot">Expected exported revision.</param>
    /// <param name="offset">Zero-based definition offset.</param>
    /// <param name="limit">Number of definitions from 1 through 100.</param>
    /// <param name="diagnosticOffset">Offset into each definition's diagnostics; each page contains at most 20 diagnostics.</param>
    /// <returns>Validation evidence; unchanged declared sources are required before treating it as current.</returns>
    /// <exception cref="ArgumentException">The expected snapshot is empty.</exception>
    /// <exception cref="UnauthorizedAccessException">The bundle cannot be accessed.</exception>
    /// <remarks>Exceptions from registered domain validators propagate unchanged.</remarks>
    /// <exception cref="InvalidOperationException">The snapshot is stale.</exception>
    /// <exception cref="IOException">The bundle cannot be read.</exception>
    /// <exception cref="JsonException">The bundle is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Pagination is invalid.</exception>
    public WorkspaceResult<WorkspacePage<WorkspaceCheck>> Check(string expectedSnapshot, int offset = 0, int limit = 20, int diagnosticOffset = 0)
        => Read(RequireSnapshot(expectedSnapshot), snapshot =>
        {
            ValidatePage(diagnosticOffset, DiagnosticPageSize);
            var documents = Page(snapshot.Documents, offset, limit);
            var checks = ImmutableArray.CreateBuilder<WorkspaceCheck>(documents.Items.Length);
            foreach (var document in documents.Items)
            {
                var available = validators.TryGetValue(document.Kind, out var validate);
                // Shared wire/integrity findings cannot be replaced by an extension's validation policy.
                var integrity = snapshot.Integrity[document.Metadata.DefinitionId];
                var diagnostics = available
                    ? integrity.Diagnostics.Concat(validate!(document).Diagnostics).Distinct()
                    : integrity.Diagnostics.AsEnumerable();
                var validation = new DocumentValidationResult(DocumentValidationDiagnostics.Normalize([.. diagnostics]));
                checks.Add(new(document.Metadata.DefinitionId.Value, available, validation.IsValid,
                    Page(validation.Diagnostics, diagnosticOffset, DiagnosticPageSize)));
            }
            return new WorkspacePage<WorkspaceCheck>(checks.MoveToImmutable(), documents.Total, documents.NextOffset);
        });

    /// <summary>Compares verified semantic content against a retained session snapshot.</summary>
    /// <param name="baselineSnapshot">Prior snapshot; at most four revisions are retained.</param>
    /// <param name="expectedSnapshot">Expected current exported revision.</param>
    /// <param name="offset">Zero-based changed-definition offset.</param>
    /// <param name="limit">Page size from 1 through 100.</param>
    /// <returns>Added, removed and content-changed definitions, excluding metadata-only changes.</returns>
    /// <exception cref="ArgumentException">The expected snapshot is empty.</exception>
    /// <exception cref="UnauthorizedAccessException">The bundle cannot be accessed.</exception>
    /// <exception cref="KeyNotFoundException">The baseline was never loaded or was evicted.</exception>
    /// <exception cref="InvalidOperationException">The snapshot is stale or either document set fails integrity validation.</exception>
    /// <exception cref="IOException">The bundle cannot be read.</exception>
    /// <exception cref="JsonException">The bundle is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Pagination is invalid.</exception>
    public WorkspaceResult<WorkspaceComparison> Compare(string baselineSnapshot, string expectedSnapshot, int offset = 0, int limit = 20)
        => Read(RequireSnapshot(expectedSnapshot), snapshot =>
        {
            if (!snapshots.TryGetValue(baselineSnapshot, out var baseline))
                throw new KeyNotFoundException("Baseline is unavailable. The session retains four exported snapshots.");
            if (snapshot.Fingerprint is null || baseline.Fingerprint is null)
                throw new InvalidOperationException("Cannot compare documents with invalid integrity evidence.");
            var before = baseline.Documents.ToDictionary(d => d.Metadata.DefinitionId.Value, d => d.Metadata.Fingerprint.Value, StringComparer.Ordinal);
            var after = snapshot.Documents.ToDictionary(d => d.Metadata.DefinitionId.Value, d => d.Metadata.Fingerprint.Value, StringComparer.Ordinal);
            var changes = before.Keys.Union(after.Keys).Order(StringComparer.Ordinal)
                .Select(id => new WorkspaceDifference(id, before.GetValueOrDefault(id), after.GetValueOrDefault(id)))
                .Where(d => d.Before != d.After);
            return new WorkspaceComparison(baselineSnapshot, Page(changes, offset, limit));
        });

    WorkspaceResult<T> Read<T>(string? expectedSnapshot, Func<Snapshot, T> operation)
    {
        lock (gate)
        {
            var bytes = SemanticWorkspaceExport.ReadBounded(SemanticWorkspaceExport.ResolvePath(root, bundlePath));
            var id = SemanticWorkspaceExport.Digest(bytes);
            if (expectedSnapshot is not null && id != expectedSnapshot)
                throw new InvalidOperationException("Workspace snapshot changed. Call workspace and resolve again.");
            if (!snapshots.TryGetValue(id, out var snapshot))
            {
                snapshot = Load(bytes);
                snapshots.Add(id, snapshot);
                retention.Enqueue(id);
                if (retention.Count > MaximumSnapshots)
                    snapshots.Remove(retention.Dequeue());
            }
            var changedBefore = ChangedInputs(snapshot);
            var result = operation(snapshot);
            // Report changes observed on either side of a potentially expensive domain validator.
            var changed = changedBefore.Union(ChangedInputs(snapshot), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            if (SemanticWorkspaceExport.DigestFile(SemanticWorkspaceExport.ResolvePath(root, bundlePath)) != id)
                throw new InvalidOperationException("Workspace snapshot changed during inspection. Call workspace and retry.");
            return new(id, snapshot.InputRevision, snapshot.Fingerprint, changed, Limitations, result);
        }
    }

    ImmutableArray<string> ChangedInputs(Snapshot snapshot) =>
        [.. snapshot.Bundle.Inputs.Where(pair => Changed(pair.Key, pair.Value)).Select(pair => pair.Key)];

    bool Changed(string path, string expected)
    {
        try { return SemanticWorkspaceExport.DigestFile(SemanticWorkspaceExport.ResolvePath(root, path)) != expected; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }

    Snapshot Load(byte[] bytes)
    {
        using var parsed = JsonDocument.Parse(bytes);
        if (StrictDocumentJson.TryFindDuplicateProperty(parsed.RootElement, "", out var duplicate))
            throw new JsonException($"Duplicate workspace property at '{duplicate}'.");
        var bundle = JsonSerializer.Deserialize<WorkspaceBundle>(bytes, SemanticWorkspaceExport.JsonOptions)
            ?? throw new JsonException("Missing workspace bundle.");
        if (bundle.SchemaVersion != SemanticWorkspaceExport.Schema || string.IsNullOrWhiteSpace(bundle.ProducerVersion)
            || bundle.Inputs is null || bundle.Inputs.Count is < 1 or > SemanticWorkspaceExport.MaximumInputs
            || bundle.Documents.IsDefault || bundle.Documents.Length > SemanticWorkspaceExport.MaximumDefinitions)
            throw new JsonException("Unsupported or incomplete workspace export.");
        bundle = bundle with { Inputs = bundle.Inputs.WithComparers(StringComparer.Ordinal) };
        foreach (var input in bundle.Inputs)
        {
            var fullPath = SemanticWorkspaceExport.ResolvePath(root, input.Key);
            if (input.Key != Path.GetRelativePath(root, fullPath).Replace('\\', '/'))
                throw new JsonException("Input paths must use normalized repository-relative names.");
            if (input.Value is null || input.Value.Length != 64 || input.Value.Any(c => !char.IsAsciiHexDigitLower(c)))
                throw new JsonException("Input fingerprints must be lowercase SHA-256 digests.");
        }
        var documents = ImmutableArray.CreateBuilder<ExecutionDefinitionDocument>(bundle.Documents.Length);
        var integrity = ImmutableDictionary.CreateBuilder<ExecutionDefinitionId, DocumentValidationResult>();
        var inspectionJson = ImmutableDictionary.CreateBuilder<ExecutionDefinitionId, JsonElement>();
        var valid = true;
        foreach (var json in bundle.Documents)
        {
            var validation = ExecutionDefinitionJsonSerializer.TryDeserialize(json.GetRawText(), out var document);
            if (document is null)
                throw new JsonException(string.Join("; ", validation.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
            valid &= validation.IsValid;
            if (!integrity.TryAdd(document.Metadata.DefinitionId, validation))
                throw new JsonException("A workspace export must contain one revision per definition identity.");
            documents.Add(document);
            // Normalize once per immutable snapshot, preserving the serializer's canonical inspection view.
            inspectionJson.Add(document.Metadata.DefinitionId,
                JsonSerializer.Deserialize<JsonElement>(ExecutionDefinitionJsonSerializer.Serialize(document)));
        }
        var ordered = documents.OrderBy(d => d.Metadata.DefinitionId.Value, StringComparer.Ordinal).ToImmutableArray();
        var inputRevision = SemanticWorkspaceExport.InputRevision(bundle.ProducerVersion, bundle.Inputs);
        var fingerprint = valid ? Convert.ToHexStringLower(StrictDocumentJson.ComputeCanonicalSha256(
            new { Schema = SemanticWorkspaceExport.Schema, Definitions = ordered.Select(d => new { Id = d.Metadata.DefinitionId.Value, d.Metadata.Fingerprint }) },
            SemanticWorkspaceExport.JsonOptions)) : null;
        return new(bundle with { Documents = [] }, ordered, inspectionJson.ToImmutable(), integrity.ToImmutable(), inputRevision, fingerprint);
    }

    static WorkspaceSubject Subject(ExecutionDefinitionDocument d) => new(d.Metadata.DefinitionId.Value, d.Kind.Value,
        d.Metadata.DisplayName, d.Metadata.Fingerprint.Value, d.Metadata.Provenance.Source.Reference);

    internal static WorkspacePage<T> Page<T>(IEnumerable<T> items, int offset, int limit)
    {
        ValidatePage(offset, limit);
        var page = ImmutableArray.CreateBuilder<T>(limit);
        var total = 0;
        foreach (var item in items)
        {
            if (total >= offset && page.Count < limit) page.Add(item);
            total++;
        }
        return new(page.ToImmutable(), total, offset < total - page.Count ? offset + page.Count : null);
    }

    static void ValidatePage(int offset, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (limit is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Limit must be 1–{MaximumPageSize}.");
    }

    static string Escape(string value) => value.Replace("~", "~0").Replace("/", "~1");
    static string RequireSnapshot(string snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot);
        return snapshot;
    }
    static JsonElement Select(JsonElement element, string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        if (pointer == "") return element;
        if (!pointer.StartsWith('/')) throw new ArgumentException("Use an RFC 6901 JSON pointer.", nameof(pointer));
        foreach (var encoded in pointer[1..].Split('/'))
        {
            for (var i = 0; i < encoded.Length; i++)
                if (encoded[i] == '~' && (++i == encoded.Length || encoded[i] is not ('0' or '1')))
                    throw new ArgumentException("Invalid JSON pointer escape.", nameof(pointer));
            var key = encoded.Replace("~1", "/").Replace("~0", "~");
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var property)) element = property;
            else if (element.ValueKind == JsonValueKind.Array && int.TryParse(key, out var index)
                && index >= 0 && index < element.GetArrayLength() && key == index.ToString(System.Globalization.CultureInfo.InvariantCulture)) element = element[index];
            else throw new KeyNotFoundException($"Unknown document pointer '{pointer}'.");
        }
        return element;
    }

    sealed record Snapshot(WorkspaceBundle Bundle, ImmutableArray<ExecutionDefinitionDocument> Documents,
        ImmutableDictionary<ExecutionDefinitionId, JsonElement> Json,
        ImmutableDictionary<ExecutionDefinitionId, DocumentValidationResult> Integrity, string InputRevision, string? Fingerprint);
}
