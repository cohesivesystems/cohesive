using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Semantics;

/// <summary>Read-only inspection session over explicitly exported domain documents.</summary>
/// <remarks>
/// The session retains four immutable snapshots. Every request rereads the bounded bundle and checks declared
/// input hashes. It never runs application code, edits source, or treats a failed refresh as a successful one.
/// Concurrent operations are serialized. Freshness checks read all declared bytes twice per request.
/// </remarks>
public sealed class SemanticWorkspace
{
    const int MaximumSnapshots = 4;
    /// <summary>Maximum number of original and projected subjects in one indexed workspace.</summary>
    public const int MaximumSubjects = 4096;
    const int MaximumReferenceEdges = 4096;
    const int MaximumPageSize = 100;
    const int MaximumInspectionBytes = 16 * 1024;
    const int DiagnosticPageSize = 20;
    readonly IReadOnlyDictionary<string, Func<JsonElement, WorkspaceDocument>> documentReaders;
    readonly Func<ExecutionDefinitionDocument, ImmutableArray<WorkspaceDocumentReference>>? additionalReferences;
    readonly string root;
    readonly string bundlePath;
    readonly ImmutableDictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, DocumentValidationResult>> validators;
    readonly ImmutableDictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, IReadOnlyList<ExecutionDefinitionReference>>> referenceReaders;
    readonly Dictionary<string, Snapshot> snapshots = new(StringComparer.Ordinal);
    readonly Queue<string> retention = new();
    readonly object gate = new();
    static readonly ImmutableArray<string> Limitations =
    [
        "Only producer-declared inputs and exported domain documents are covered; input completeness is not proven.",
        "No target realization, cross-document type/contract validation, API/client compatibility, runtime safety, or invariant proof is claimed.",
        "Comparison reports document fingerprint changes, not compatibility or rename continuity. Edit owning source and re-export."
    ];

    /// <summary>Opens a session without executing or loading application assemblies.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="bundlePath">Relative path to a derived workspace bundle.</param>
    /// <param name="validators">Domain-owned validators keyed by their existing canonical kind; these supplement mandatory shared integrity validation.</param>
    /// <param name="referenceReaders">Domain-owned direct reference projections; absent kinds have explicitly incomplete coverage.</param>
    /// <param name="additionalReferences">Optional domain projection of non-execution references; coverage is conservatively partial when supplied.</param>
    /// <param name="documentReaders">Additional schema-keyed domain readers; each must validate original integrity and return an immutable inspection projection.</param>
    /// <exception cref="ArgumentException">The bundle path escapes the repository, traverses a symbolic link, or a validator/reference reader has an uninitialized kind or null delegate.</exception>
    public SemanticWorkspace(string root, string bundlePath,
        IReadOnlyDictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, DocumentValidationResult>>? validators = null,
        IReadOnlyDictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, IReadOnlyList<ExecutionDefinitionReference>>>? referenceReaders = null,
        IReadOnlyDictionary<string, Func<JsonElement, WorkspaceDocument>>? documentReaders = null,
        Func<ExecutionDefinitionDocument, ImmutableArray<WorkspaceDocumentReference>>? additionalReferences = null)
    {
        this.additionalReferences = additionalReferences;
        this.documentReaders = documentReaders?.ToImmutableDictionary(StringComparer.Ordinal) ?? ImmutableDictionary<string, Func<JsonElement, WorkspaceDocument>>.Empty;
        if (this.documentReaders.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
            throw new ArgumentException("Document readers require schema names and non-null delegates.", nameof(documentReaders));
        this.referenceReaders = referenceReaders?.ToImmutableDictionary()
            ?? ImmutableDictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, IReadOnlyList<ExecutionDefinitionReference>>>.Empty;
        if (this.referenceReaders.Any(pair => string.IsNullOrWhiteSpace(pair.Key.Value) || pair.Value is null))
            throw new ArgumentException("Reference readers require initialized kinds and non-null delegates.", nameof(referenceReaders));
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
            var document = snapshot.Documents.FirstOrDefault(d => d.Reference.DefinitionId.Value == id)
                ?? throw new KeyNotFoundException($"Unknown definition '{id}'. Use resolve first.");
            var json = document.Json;
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
            var references = document.SourceMap?.ResolveReferences(pointer, document.Source?.Reference ?? "") ?? (document.Source is { } source ? [source.Reference] : ImmutableArray<string>.Empty);
            var mappings = pointer is "" or "/definition"
                ? (document.SourceMap?.Entries ?? []).AsEnumerable()
                : (document.SourceMap?.Entries ?? []).Where(entry => references.Contains(entry.Reference));
            if (document.Source?.Location is not null
                && (pointer is "" or "/definition" || references.Contains(document.Source.Reference)))
                mappings = mappings.Prepend(document.Source);
            return new WorkspaceInspection(Subject(document), pointer,
                fits ? selected : null,
                Page(children, offset, limit),
                Page(references, sourceOffset, sourceLimit), Page(mappings, sourceOffset, sourceLimit), selected.ValueKind);
        });

    /// <summary>Finds direct consumers by target identity, preserving exact authored revision evidence.</summary>
    /// <param name="id">Target identity; it may be absent from this export.</param>
    /// <param name="expectedSnapshot">Exact snapshot to inspect.</param>
    /// <param name="offset">Zero-based consumer offset.</param>
    /// <param name="limit">Page size from 1 through 100.</param>
    /// <returns>Direct references: null Available means missing; content and optional revision evidence distinguish matches, mismatches and unknowns. Coverage is explicit.</returns>
    /// <exception cref="ArgumentException">The identity or snapshot is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Pagination is invalid.</exception>
    /// <exception cref="InvalidOperationException">The snapshot changed or shared integrity validation failed.</exception>
    /// <exception cref="IOException">A bundle cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Bundle access is denied.</exception>
    /// <exception cref="JsonException">The bundle or a projected domain document is malformed.</exception>
    /// <remarks>Reader exceptions propagate. Results do not establish transitive impact, type compatibility or runtime safety.</remarks>
    public WorkspaceResult<WorkspaceReferences> References(string id, string expectedSnapshot, int offset = 0, int limit = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ValidatePage(offset, limit);
        return Read(RequireSnapshot(expectedSnapshot), snapshot =>
        {
            var graph = Graph(snapshot);
            return new WorkspaceReferences(Page(graph.Edges.Where(edge => edge.Expected.DefinitionId.Value == id), offset, limit), graph.Incomplete);
        });
    }

    /// <summary>Traverses reverse dependencies with cycle suppression and deterministic shortest evidence paths.</summary>
    /// <param name="id">Starting identity, which may be absent from the export.</param>
    /// <param name="expectedSnapshot">Exact snapshot required.</param>
    /// <param name="maxDepth">Maximum path length, from 1 through 16.</param>
    /// <param name="limit">Maximum returned consumers, from 1 through 100.</param>
    /// <returns>Potential consumers including stale, missing and identity-only edges; inspect every edge's evidence.</returns>
    /// <exception cref="ArgumentException">Identity or snapshot is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Depth or result bound is invalid.</exception>
    /// <exception cref="InvalidOperationException">Snapshot or integrity is invalid, or the graph exceeds 4096 edges.</exception>
    /// <exception cref="IOException">The bundle cannot be read.</exception>
    /// <exception cref="JsonException">A domain document is malformed.</exception>
    /// <remarks>The export bounds traversal to 256 documents and 4096 edges. Each consumer appears once, even in cycles or diamonds.</remarks>
    public WorkspaceResult<WorkspaceImpact> Impact(string id, string expectedSnapshot, int maxDepth = 4, int limit = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (maxDepth is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        ValidatePage(0, limit);
        return Read(RequireSnapshot(expectedSnapshot), snapshot =>
        {
            var graph = Graph(snapshot);
            var reverse = graph.Edges.ToLookup(edge => edge.Expected.DefinitionId.Value, StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal) { id };
            var queue = new Queue<(string Id, ImmutableArray<WorkspaceReference> Path)>();
            queue.Enqueue((id, []));
            var found = new List<WorkspaceImpactItem>();
            var boundary = new List<string>();
            while (queue.TryDequeue(out var current))
            {
                if (current.Path.Length == maxDepth) { boundary.Add(current.Id); continue; }
                foreach (var edge in reverse[current.Id])
                {
                    var consumer = edge.Source.DefinitionId.Value;
                    if (!visited.Add(consumer)) continue;
                    var path = current.Path.Add(edge);
                    found.Add(new(edge.Source, path));
                    queue.Enqueue((consumer, path));
                }
            }
            return new WorkspaceImpact([.. found.Take(limit)],
                boundary.Any(target => reverse[target].Any(edge => !visited.Contains(edge.Source.DefinitionId.Value))),
                found.Count > limit, graph.Incomplete);
        });
    }

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
                var validation = document.ValidateDomain?.Invoke() ?? document.Validation;
                checks.Add(new(document.Reference.DefinitionId.Value, document.DomainValidationAvailable, validation.IsValid,
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
            var before = baseline.Documents.ToDictionary(d => d.Reference.DefinitionId.Value, d => d.Reference.Fingerprint, StringComparer.Ordinal);
            var after = snapshot.Documents.ToDictionary(d => d.Reference.DefinitionId.Value, d => d.Reference.Fingerprint, StringComparer.Ordinal);
            var changes = before.Keys.Union(after.Keys).Order(StringComparer.Ordinal)
                .Where(id => before.GetValueOrDefault(id) != after.GetValueOrDefault(id))
                .Select(id => new WorkspaceDifference(id, before.GetValueOrDefault(id), after.GetValueOrDefault(id)));
            return new WorkspaceComparison(baselineSnapshot, Page(changes, offset, limit));
        });

    /// <summary>Runs a domain-owned comparison over two verified documents without releasing snapshot guarantees.</summary>
    /// <typeparam name="T">Domain comparison result.</typeparam>
    /// <param name="id">Stable document identity present in at least one snapshot.</param><param name="baselineSnapshot">Retained baseline.</param>
    /// <param name="expectedSnapshot">Expected current snapshot.</param><param name="interpret">Read-only domain interpreter over original JSON; null represents absence.</param>
    /// <returns>Current freshness evidence with the interpreted comparison.</returns>
    /// <exception cref="KeyNotFoundException">The baseline or document is unavailable.</exception>
    /// <exception cref="InvalidOperationException">Integrity or expected snapshot validation fails.</exception>
    /// <exception cref="ArgumentNullException">The interpreter is null.</exception>
    public WorkspaceResult<T> CompareDocuments<T>(string id, string baselineSnapshot, string expectedSnapshot, Func<JsonElement?, JsonElement?, T> interpret)
    {
        ArgumentNullException.ThrowIfNull(interpret);
        return Read(RequireSnapshot(expectedSnapshot), current =>
        {
            if (!snapshots.TryGetValue(baselineSnapshot, out var before)) throw new KeyNotFoundException("Baseline snapshot is unavailable.");
            if (before.Fingerprint is null || current.Fingerprint is null) throw new InvalidOperationException("Comparison requires valid integrity.");
            JsonElement? Find(Snapshot snapshot) => snapshot.Documents.FirstOrDefault(d => d.Reference.DefinitionId.Value == id)?.Json;
            var old = Find(before);
            var proposed = Find(current);
            if (old is null && proposed is null) throw new KeyNotFoundException("Neither snapshot contains document: " + id);
            return interpret(old, proposed);
        });
    }

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
        var documents = ImmutableArray.CreateBuilder<WorkspaceDocument>(bundle.Documents.Length);
        var identities = new Dictionary<ExecutionDefinitionId, WorkspaceDocument>();
        var valid = true;
        foreach (var json in bundle.Documents)
        {
            var schema = json.TryGetProperty("schemaVersion", out var schemaValue) ? schemaValue.GetString()! : "";
            var document = documentReaders.TryGetValue(schema, out var reader) ? reader(json) : ReadExecution(json);
            Add(document);
        }
        void Add(WorkspaceDocument document, bool member = false)
        {
            if (documents.Count == MaximumSubjects) throw new JsonException("Workspace exceeds 4096 projected subjects; split the export scope.");
            if (document is null || string.IsNullOrWhiteSpace(document.Reference.DefinitionId.Value)
                || document.Reference.Fingerprint is null || document.Json.ValueKind != JsonValueKind.Object)
                throw new JsonException("Domain readers must return an identified document with fingerprint evidence and owned object JSON.");
            valid &= document.Validation.IsValid;
            if (!identities.TryAdd(document.Reference.DefinitionId, document))
            {
                var prior = identities[document.Reference.DefinitionId];
                if (member && prior.Reference == document.Reference && JsonElement.DeepEquals(prior.Json, document.Json)
                    && (prior.Dependencies ?? []).SequenceEqual(document.Dependencies ?? [])) return;
                throw new JsonException("Conflicting workspace identity: " + document.Reference.DefinitionId.Value);
            }
            documents.Add(document);
            foreach (var child in document.Members) Add(child, member: true);
        }
        var ordered = documents.OrderBy(d => d.Reference.DefinitionId.Value, StringComparer.Ordinal).ToImmutableArray();
        var inputRevision = SemanticWorkspaceExport.InputRevision(bundle.ProducerVersion, bundle.Inputs);
        var fingerprint = valid ? Convert.ToHexStringLower(StrictDocumentJson.ComputeCanonicalSha256(
            new { Schema = SemanticWorkspaceExport.Schema, Definitions = ordered.Select(d => new { Id = d.Reference.DefinitionId.Value, d.Reference.Fingerprint }) },
            SemanticWorkspaceExport.JsonOptions)) : null;
        return new(bundle with { Documents = [] }, ordered, inputRevision, fingerprint);
    }

    WorkspaceDocument ReadExecution(JsonElement json)
    {
        var integrity = ExecutionDefinitionJsonSerializer.TryDeserialize(json.GetRawText(), out var document);
        if (document is null) throw new JsonException(string.Join("; ", integrity.Diagnostics.Select(d => d.Message)));
        var available = validators.TryGetValue(document.Kind, out var validate);
        DocumentValidationResult Validate() => available ? new DocumentValidationResult(DocumentValidationDiagnostics.Normalize(
            [.. integrity.Diagnostics.Concat(validate!(document).Diagnostics).Distinct()])) : integrity;
        var hasReader = referenceReaders.TryGetValue(document.Kind, out var read);
        return new(WorkspaceDocumentReference.FromExecution(document.Reference), document.Kind.Value, document.Metadata.DisplayName,
            JsonSerializer.Deserialize<JsonElement>(ExecutionDefinitionJsonSerializer.Serialize(document)), document.Metadata.Provenance.Source,
            document.Metadata.SourceMap, integrity, available, null, hasReader && additionalReferences is null && document.Extensions.IsDefaultOrEmpty)
        {
            ValidateDomain = Validate,
            ReadDependencies = hasReader || additionalReferences is not null ? () =>
            {
                if (!Validate().IsValid) throw new InvalidOperationException("Reference inspection requires valid domain semantics. Use check for diagnostics.");
                var targets = hasReader ? read!(document) : [];
                if (targets.Count > MaximumReferenceEdges) throw new InvalidOperationException("Reference graph exceeds 4096 edges; split the export scope.");
                return [.. targets.Select(WorkspaceDocumentReference.FromExecution), .. additionalReferences?.Invoke(document) ?? []];
            } : null
        };
    }

    static WorkspaceSubject Subject(WorkspaceDocument d) => new(d.Reference.DefinitionId.Value, d.Kind,
        d.DisplayName, d.Reference.Fingerprint?.Value ?? "", d.Source?.Reference ?? "");

    static (ImmutableArray<WorkspaceReference> Edges, ImmutableArray<string> Incomplete) Graph(Snapshot snapshot)
    {
        if (snapshot.Fingerprint is null) throw new InvalidOperationException("Reference inspection requires valid document integrity.");
        var available = snapshot.Documents.ToDictionary(d => d.Reference.DefinitionId, d => d.Reference);
        var edges = ImmutableArray.CreateBuilder<WorkspaceReference>();
        var incomplete = ImmutableArray.CreateBuilder<string>();
        foreach (var document in snapshot.Documents)
        {
            if (!document.ReferencesComplete || (document.Dependencies is null && document.ReadDependencies is null)) incomplete.Add(document.Reference.DefinitionId.Value);
            foreach (var target in document.ReadDependencies?.Invoke() ?? document.Dependencies ?? [])
            {
                if (edges.Count == MaximumReferenceEdges) throw new InvalidOperationException("Reference graph exceeds 4096 edges; split the export scope.");
                edges.Add(new(document.Reference, target, available.GetValueOrDefault(target.DefinitionId)));
            }
        }
        return ([.. edges.Distinct().OrderBy(e => e.Source.DefinitionId.Value, StringComparer.Ordinal)
            .ThenBy(e => e.Expected.DefinitionId.Value, StringComparer.Ordinal)
            .ThenBy(e => e.Expected.RevisionId?.Value, StringComparer.Ordinal)
            .ThenBy(e => e.Expected.Fingerprint?.Algorithm, StringComparer.Ordinal)
            .ThenBy(e => e.Expected.Fingerprint?.Canonicalization, StringComparer.Ordinal)
            .ThenBy(e => e.Expected.Fingerprint?.Value, StringComparer.Ordinal)], incomplete.ToImmutable());
    }

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

    sealed record Snapshot(WorkspaceBundle Bundle, ImmutableArray<WorkspaceDocument> Documents,
        string InputRevision, string? Fingerprint);
}
