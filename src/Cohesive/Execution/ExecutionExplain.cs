using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable names for target-neutral execution explanation queries.</summary>
public static class ExecutionExplainWireNames
{
    /// <summary>Semantic authority that owns execution explanation.</summary>
    public const string SemanticAuthority = "cohesive.execution.explain";

    /// <summary>Canonical explanation query action.</summary>
    public const string Explain = "explain";

    /// <summary>Canonical semantic path of the explanation query.</summary>
    public static ExecutionSemanticPath QueryPath { get; } = new(["queries", Explain]);
}

/// <summary>Stable diagnostics produced while projecting execution explain artifacts.</summary>
public static class ExecutionExplainDiagnosticCodes
{
    /// <summary>The selected interpreter profile does not declare support for the supplied definition.</summary>
    public const string ProfileUnsupported = "execution.explain.profileUnsupported";

    /// <summary>Trace evidence belongs to another execution definition.</summary>
    public const string TraceDefinitionMismatch = "execution.explain.traceDefinitionMismatch";

    /// <summary>Runtime status belongs to another execution definition or continuation.</summary>
    public const string RuntimeStatusMismatch = "execution.explain.runtimeStatusMismatch";
}

/// <summary>Common stable lifecycle-stage names used by execution explain projections.</summary>
public static class ExecutionExplainStageNames
{
    /// <summary>Canonical definition availability and provenance.</summary>
    public const string Definition = "definition";

    /// <summary>Interpreter-profile compatibility.</summary>
    public const string InterpreterProfile = "interpreterProfile";

    /// <summary>Target-independent compilation and requirement extraction.</summary>
    public const string StaticCompilation = "staticCompilation";

    /// <summary>Capability matching and realization planning.</summary>
    public const string Realization = "realization";

    /// <summary>Finite semantic execution decision.</summary>
    public const string ExecutionTrace = "executionTrace";

    /// <summary>Ephemeral runtime-status observation.</summary>
    public const string RuntimeStatus = "runtimeStatus";

    /// <summary>Operational Control recommendation or actuation.</summary>
    public const string Control = "control";

    /// <summary>Storage materialization requirement or realization.</summary>
    public const string Materialization = "materialization";
}

/// <summary>Authority that supplied one normalized execution-explain claim.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum ExecutionExplainEvidenceAuthority
{
    /// <summary>The claim is stated directly by canonical IR or an explicit semantic declaration.</summary>
    Declared = 0,

    /// <summary>The claim is deterministically derived from canonical semantics.</summary>
    Derived = 1,

    /// <summary>The claim was selected by an attributable convention.</summary>
    ConventionDerived = 2,

    /// <summary>The claim was supplied by a target adapter or concrete interpreter.</summary>
    AdapterSupplied = 3,

    /// <summary>The claim is a deterministic semantic interpretation result.</summary>
    Interpreted = 4,

    /// <summary>The claim is an ephemeral runtime observation excluded from deterministic explain identity.</summary>
    Measured = 5,

    /// <summary>The claim is a non-authoritative operational recommendation.</summary>
    Recommended = 6,

    /// <summary>The claim is an applied operational decision or durable receipt.</summary>
    Applied = 7
}

/// <summary>Exact portable declaration of one execution interpreter profile.</summary>
public sealed record ExecutionInterpreterProfileReference
{
    /// <summary>Creates an interpreter-profile reference.</summary>
    /// <param name="id">Stable profile identity.</param>
    /// <param name="version">Exact profile version.</param>
    /// <param name="schemaCompatibility">Exact execution-document schemas supported by the profile.</param>
    /// <param name="definitionKinds">Non-empty set of execution-definition kinds supported by the profile.</param>
    /// <param name="provenance">Attributable producer and source of the profile declaration.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaCompatibility"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity is empty, or <paramref name="definitionKinds"/> is empty, contains a default value, or contains
    /// a duplicate kind.
    /// </exception>
    [JsonConstructor]
    public ExecutionInterpreterProfileReference(
        string id,
        string version,
        ExecutionIrSchemaCompatibilityDeclaration schemaCompatibility,
        ImmutableArray<ExecutionDefinitionKind> definitionKinds,
        ExecutionProvenance provenance)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Version = Guard.RequireNotNullOrWhiteSpace(version);
        SchemaCompatibility = Guard.RequireNotNull(schemaCompatibility);
        Provenance = Guard.RequireNotNull(provenance);
        if (definitionKinds.IsDefaultOrEmpty
            || definitionKinds.Any(static kind => string.IsNullOrWhiteSpace(kind.Value)))
        {
            throw new ArgumentException(
                "An interpreter profile requires one or more initialized definition kinds.",
                nameof(definitionKinds));
        }

        var observed = new HashSet<ExecutionDefinitionKind>();
        foreach (var kind in definitionKinds)
        {
            if (!observed.Add(kind))
                throw new ArgumentException($"Definition kind '{kind.Value}' is duplicated.", nameof(definitionKinds));
        }

        DefinitionKinds = CanonicalDocumentCollections.SortIfNeeded(
            definitionKinds,
            static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));
    }

    /// <summary>Stable interpreter-profile identity.</summary>
    public string Id { get; }

    /// <summary>Exact interpreter-profile version.</summary>
    public string Version { get; }

    /// <summary>Exact supported execution-document schemas.</summary>
    public ExecutionIrSchemaCompatibilityDeclaration SchemaCompatibility { get; }

    /// <summary>Supported definition kinds in deterministic ordinal order.</summary>
    public ImmutableArray<ExecutionDefinitionKind> DefinitionKinds { get; }

    /// <summary>Attributable producer and source of this profile declaration.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Determines whether this profile declares exact support for a definition family and schema.</summary>
    /// <param name="kind">Definition family to inspect.</param>
    /// <param name="schemaVersion">Exact shared execution-document schema.</param>
    /// <returns><see langword="true"/> only when both exact values are declared.</returns>
    /// <exception cref="ArgumentException">An argument is a default uninitialized identity.</exception>
    public bool Supports(ExecutionDefinitionKind kind, ExecutionIrSchemaVersion schemaVersion)
    {
        if (string.IsNullOrWhiteSpace(kind.Value))
            throw new ArgumentException("A default definition kind cannot be inspected.", nameof(kind));
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
            throw new ArgumentException("A default schema version cannot be inspected.", nameof(schemaVersion));

        return SchemaCompatibility.Supports(schemaVersion)
            && CanonicalDocumentCollections.BinarySearchIndex(
                DefinitionKinds,
                kind,
                static (candidate, requested) =>
                    StringComparer.Ordinal.Compare(candidate.Value, requested.Value)) >= 0;
    }
}

/// <summary>Payload-free reference to the canonical definition explained by an artifact.</summary>
public sealed record ExecutionExplainDefinitionReference
{
    /// <summary>Creates a canonical definition reference for explanation.</summary>
    /// <param name="kind">Stable definition family.</param>
    /// <param name="schemaVersion">Exact shared execution-document schema.</param>
    /// <param name="definition">Exact definition identity, revision, and fingerprint.</param>
    /// <param name="provenance">Required definition producer and source attribution.</param>
    /// <param name="sourceMap">Normalized per-construct producer source mappings.</param>
    /// <exception cref="ArgumentException">A kind or schema identity is default.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/>, <paramref name="provenance"/>, or <paramref name="sourceMap"/> is
    /// <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ExecutionExplainDefinitionReference(
        ExecutionDefinitionKind kind,
        ExecutionIrSchemaVersion schemaVersion,
        ExecutionDefinitionReference definition,
        ExecutionProvenance provenance,
        ExecutionSourceMap sourceMap)
    {
        if (string.IsNullOrWhiteSpace(kind.Value))
            throw new ArgumentException("An explained definition requires a stable kind.", nameof(kind));
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
            throw new ArgumentException("An explained definition requires an exact schema.", nameof(schemaVersion));

        Kind = kind;
        SchemaVersion = schemaVersion;
        Definition = Guard.RequireNotNull(definition);
        Provenance = Guard.RequireNotNull(provenance);
        SourceMap = Guard.RequireNotNull(sourceMap);
    }

    /// <summary>Stable definition family.</summary>
    public ExecutionDefinitionKind Kind { get; }

    /// <summary>Exact shared execution-document schema.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Exact definition identity, revision, and fingerprint.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Required definition producer and source attribution.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Normalized per-construct producer source mappings.</summary>
    public ExecutionSourceMap SourceMap { get; }

    /// <summary>Projects the payload-free explain reference of one canonical document.</summary>
    /// <param name="document">Canonical execution-definition document.</param>
    /// <returns>A reference retaining exact identity, schema, provenance, and source paths.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static ExecutionExplainDefinitionReference From(ExecutionDefinitionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new(
            document.Kind,
            document.Metadata.SchemaVersion,
            new(
                document.Metadata.DefinitionId,
                document.Metadata.RevisionId,
                document.Metadata.Fingerprint),
            document.Metadata.Provenance,
            document.Metadata.SourceMap);
    }
}

/// <summary>One payload-free claim projected from an existing execution lifecycle authority.</summary>
public sealed record ExecutionExplainEvidence
{
    /// <summary>Creates normalized explain evidence.</summary>
    /// <param name="stage">Lifecycle stage that produced the claim.</param>
    /// <param name="kind">Block-owned stable claim kind.</param>
    /// <param name="subject">Stable semantic subject.</param>
    /// <param name="authority">Authority that supplied the claim.</param>
    /// <param name="status">Block-owned stable status or disposition.</param>
    /// <param name="realization">Optional shared capability-realization classification.</param>
    /// <param name="relatedSubjects">Related semantic subjects or causal identities.</param>
    /// <param name="sourceReferences">Producer or interpretation evidence references.</param>
    /// <exception cref="ArgumentException">An identity or collection entry is empty or duplicated.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="authority"/> or <paramref name="realization"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public ExecutionExplainEvidence(
        string stage,
        string kind,
        string subject,
        ExecutionExplainEvidenceAuthority authority,
        string status,
        CapabilityRealizationKind? realization = null,
        ImmutableArray<string> relatedSubjects = default,
        ImmutableArray<string> sourceReferences = default)
    {
        if (!Enum.IsDefined(authority))
            throw new ArgumentOutOfRangeException(nameof(authority), authority, "Unsupported explain evidence authority.");
        if (realization is { } capability
            && (!Enum.IsDefined(capability) || capability == CapabilityRealizationKind.Unknown))
        {
            throw new ArgumentOutOfRangeException(nameof(realization), realization, "Unsupported realization claim.");
        }

        Stage = Guard.RequireNotNullOrWhiteSpace(stage);
        Kind = Guard.RequireNotNullOrWhiteSpace(kind);
        Subject = Guard.RequireNotNullOrWhiteSpace(subject);
        Authority = authority;
        Status = Guard.RequireNotNullOrWhiteSpace(status);
        Realization = realization;
        RelatedSubjects = NormalizeStrings(relatedSubjects, nameof(relatedSubjects));
        SourceReferences = NormalizeStrings(sourceReferences, nameof(sourceReferences));
    }

    /// <summary>Lifecycle stage that produced the claim.</summary>
    public string Stage { get; }

    /// <summary>Block-owned stable claim kind.</summary>
    public string Kind { get; }

    /// <summary>Stable semantic subject.</summary>
    public string Subject { get; }

    /// <summary>Authority that supplied the claim.</summary>
    public ExecutionExplainEvidenceAuthority Authority { get; }

    /// <summary>Block-owned stable status or disposition.</summary>
    public string Status { get; }

    /// <summary>Optional shared capability-realization classification.</summary>
    public CapabilityRealizationKind? Realization { get; }

    /// <summary>Related semantic subjects and causal identities in deterministic order.</summary>
    public ImmutableArray<string> RelatedSubjects { get; }

    /// <summary>Attributable producer or interpretation evidence references in deterministic order.</summary>
    public ImmutableArray<string> SourceReferences { get; }

    static ImmutableArray<string> NormalizeStrings(ImmutableArray<string> values, string parameterName)
    {
        if (values.IsDefaultOrEmpty)
            return [];
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Explain evidence references cannot be empty.", parameterName);

        var normalized = values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        if (normalized.Length != values.Length)
            throw new ArgumentException("Explain evidence references cannot be duplicated.", parameterName);
        return normalized.SequenceEqual(values) ? values : normalized;
    }
}

/// <summary>Payload-free semantic identity of one normalized execution trace.</summary>
public sealed record ExecutionExplainTraceReference
{
    /// <summary>Creates a normalized-trace reference.</summary>
    /// <param name="definition">Exact definition interpreted by the trace.</param>
    /// <param name="continuation">Process instance and attempt, or null for a direct Transition.</param>
    /// <param name="activation">Finite activation identity.</param>
    /// <param name="fingerprint">Deterministic semantic trace fingerprint.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="fingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="activation"/> is default.</exception>
    [JsonConstructor]
    public ExecutionExplainTraceReference(
        ExecutionDefinitionReference definition,
        ProcessContinuationIdentity? continuation,
        ActivationId activation,
        ExecutionTraceFingerprint fingerprint)
    {
        if (string.IsNullOrWhiteSpace(activation.Value))
            throw new ArgumentException("An explain trace reference requires an activation identity.", nameof(activation));

        Definition = Guard.RequireNotNull(definition);
        Continuation = continuation;
        Activation = activation;
        Fingerprint = Guard.RequireNotNull(fingerprint);
    }

    /// <summary>Exact definition interpreted by the trace.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Process instance and attempt, or null for a direct Transition.</summary>
    public ProcessContinuationIdentity? Continuation { get; }

    /// <summary>Finite activation identity.</summary>
    public ActivationId Activation { get; }

    /// <summary>Deterministic semantic trace fingerprint.</summary>
    public ExecutionTraceFingerprint Fingerprint { get; }

    /// <summary>Projects a semantic reference without retaining trace events or durable commit observations.</summary>
    /// <param name="trace">Normalized trace to reference.</param>
    /// <returns>A payload-free semantic trace identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="trace"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Trace content cannot be materialized for fingerprinting.</exception>
    /// <exception cref="JsonException">Trace content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Trace content contains an unsupported serialization type.</exception>
    public static ExecutionExplainTraceReference From(NormalizedExecutionTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        return new(
            trace.Definition,
            trace.Continuation,
            trace.Activation,
            ExecutionTraceFingerprinter.ComputeSemantic(trace));
    }
}

/// <summary>Versioned cryptographic identity of one deterministic execution explain artifact.</summary>
public sealed record ExecutionExplainFingerprint
{
    /// <summary>Creates an execution-explain fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonical explain profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    [JsonConstructor]
    public ExecutionExplainFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Canonical explain profile identifier.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal hash value.</summary>
    public string Value { get; }
}

/// <summary>Portable explanation of available execution lifecycle evidence without duplicating canonical IR.</summary>
public sealed class ExecutionExplainArtifact
{
    /// <summary>Current portable execution-explain schema.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } =
        new("cohesive-execution-explain/v1");

    /// <summary>Creates and verifies one execution explain artifact.</summary>
    /// <param name="schemaVersion">Exact portable execution-explain schema.</param>
    /// <param name="definition">Payload-free canonical definition identity and provenance.</param>
    /// <param name="interpreter">Exact interpreter profile used or assessed.</param>
    /// <param name="evidence">Available payload-free lifecycle claims.</param>
    /// <param name="trace">Optional semantic trace identity.</param>
    /// <param name="runtimeStatus">
    /// Optional safe runtime observation; excluded from deterministic explain identity.
    /// </param>
    /// <param name="diagnostics">Structured lifecycle diagnostics.</param>
    /// <param name="fingerprint">Persisted fingerprint to verify, or null to compute it.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="interpreter"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Schema, evidence, diagnostics, trace affinity, status affinity, or persisted fingerprint is inconsistent.
    /// </exception>
    /// <exception cref="InvalidOperationException">Explain content cannot be materialized for fingerprinting.</exception>
    /// <exception cref="JsonException">Explain content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Explain content contains an unsupported serialization type.</exception>
    [JsonConstructor]
    public ExecutionExplainArtifact(
        ExecutionIrSchemaVersion schemaVersion,
        ExecutionExplainDefinitionReference definition,
        ExecutionInterpreterProfileReference interpreter,
        ImmutableArray<ExecutionExplainEvidence> evidence,
        ExecutionExplainTraceReference? trace = null,
        ExecutionStatus? runtimeStatus = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default,
        ExecutionExplainFingerprint? fingerprint = null)
    {
        if (schemaVersion != CurrentSchemaVersion)
            throw new ArgumentException("Unsupported execution-explain schema version.", nameof(schemaVersion));

        SchemaVersion = schemaVersion;
        Definition = Guard.RequireNotNull(definition);
        Interpreter = Guard.RequireNotNull(interpreter);
        Evidence = NormalizeEvidence(evidence);
        ValidateAffinity(definition, trace, runtimeStatus);
        Trace = trace;
        RuntimeStatus = runtimeStatus;
        Diagnostics = NormalizeDiagnostics(diagnostics);
        var computed = ExecutionExplainFingerprinter.Compute(this);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The explain fingerprint does not match normalized semantic content.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact portable execution-explain schema.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Payload-free canonical definition identity and provenance.</summary>
    public ExecutionExplainDefinitionReference Definition { get; }

    /// <summary>Exact interpreter profile used or assessed.</summary>
    public ExecutionInterpreterProfileReference Interpreter { get; }

    /// <summary>Available payload-free lifecycle claims in deterministic order.</summary>
    public ImmutableArray<ExecutionExplainEvidence> Evidence { get; }

    /// <summary>Optional deterministic semantic trace identity.</summary>
    public ExecutionExplainTraceReference? Trace { get; }

    /// <summary>Optional safe runtime observation excluded from deterministic explain identity.</summary>
    public ExecutionStatus? RuntimeStatus { get; }

    /// <summary>Structured lifecycle diagnostics in deterministic order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Deterministic identity excluding runtime observations and human prose.</summary>
    public ExecutionExplainFingerprint Fingerprint { get; }

    static void ValidateAffinity(
        ExecutionExplainDefinitionReference definition,
        ExecutionExplainTraceReference? trace,
        ExecutionStatus? runtimeStatus)
    {
        if (trace is not null && trace.Definition != definition.Definition)
            throw new ArgumentException("Explain trace belongs to another definition.", nameof(trace));
        if (runtimeStatus is not null && runtimeStatus.Definition != definition.Definition)
            throw new ArgumentException("Runtime status belongs to another definition.", nameof(runtimeStatus));
        if (trace?.Continuation is not { } continuation || runtimeStatus is null)
            return;
        if (runtimeStatus.ProcessInstanceId != continuation.ProcessInstanceId
            || runtimeStatus.CurrentAttemptId != continuation.ProcessAttemptId)
        {
            throw new ArgumentException("Runtime status and trace name different Process continuations.", nameof(runtimeStatus));
        }
    }

    static ImmutableArray<ExecutionExplainEvidence> NormalizeEvidence(
        ImmutableArray<ExecutionExplainEvidence> evidence)
    {
        if (evidence.IsDefaultOrEmpty)
            throw new ArgumentException("An execution explain artifact requires definition evidence.", nameof(evidence));
        if (evidence.Any(static item => item is null))
            throw new ArgumentException("Execution explain evidence cannot contain null entries.", nameof(evidence));

        var ordered = evidence.Sort(ExecutionExplainEvidenceComparer.Instance);
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ExecutionExplainEvidenceComparer.SameIdentity(ordered[index - 1], ordered[index]))
            {
                throw new ArgumentException(
                    $"Explain evidence '{ordered[index].Stage}/{ordered[index].Kind}/{ordered[index].Subject}' is duplicated.",
                    nameof(evidence));
            }
        }
        return ordered;
    }

    static ImmutableArray<DocumentValidationDiagnostic> NormalizeDiagnostics(
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        if (diagnostics.IsDefaultOrEmpty)
            return [];
        if (diagnostics.Any(static item => item is null
            || string.IsNullOrWhiteSpace(item.Code)
            || string.IsNullOrWhiteSpace(item.Message)
            || !Enum.IsDefined(item.Severity)))
        {
            throw new ArgumentException("Explain diagnostics must be initialized and structured.", nameof(diagnostics));
        }
        return diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
    }
}

sealed class ExecutionExplainEvidenceComparer : IComparer<ExecutionExplainEvidence>
{
    public static ExecutionExplainEvidenceComparer Instance { get; } = new();

    ExecutionExplainEvidenceComparer()
    {
    }

    public int Compare(ExecutionExplainEvidence? left, ExecutionExplainEvidence? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var comparison = StringComparer.Ordinal.Compare(left.Stage, right.Stage);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.Kind, right.Kind);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.Subject, right.Subject);
        if (comparison != 0)
            return comparison;
        comparison = left.Authority.CompareTo(right.Authority);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.Status, right.Status);
        if (comparison != 0)
            return comparison;
        return Nullable.Compare(left.Realization, right.Realization);
    }

    public static bool SameIdentity(ExecutionExplainEvidence left, ExecutionExplainEvidence right) =>
        string.Equals(left.Stage, right.Stage, StringComparison.Ordinal)
        && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
        && string.Equals(left.Subject, right.Subject, StringComparison.Ordinal)
        && left.Authority == right.Authority
        && string.Equals(left.Status, right.Status, StringComparison.Ordinal)
        && left.Realization == right.Realization;
}

/// <summary>Result of projecting authoritative lifecycle evidence into an execution explain artifact.</summary>
public sealed record ExecutionExplainProjectionResult
{
    /// <summary>Creates an explain projection result.</summary>
    /// <param name="artifact">Explain artifact when structural affinity is valid.</param>
    /// <param name="validation">Structured projection diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="validation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Artifact presence contradicts projection validity.</exception>
    public ExecutionExplainProjectionResult(
        ExecutionExplainArtifact? artifact,
        DocumentValidationResult validation)
    {
        Validation = Guard.RequireNotNull(validation);
        if ((artifact is not null) != validation.IsValid)
            throw new ArgumentException("An explain artifact exists exactly when structural projection is valid.", nameof(artifact));
        Artifact = artifact;
    }

    /// <summary>Explain artifact when structural affinity is valid.</summary>
    public ExecutionExplainArtifact? Artifact { get; }

    /// <summary>Structured projection diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Whether projection produced an artifact.</summary>
    public bool IsSuccessful => Artifact is not null;

    /// <summary>Creates a successful projection.</summary>
    /// <param name="artifact">Verified explain artifact.</param>
    /// <returns>A successful projection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="artifact"/> is <see langword="null"/>.</exception>
    public static ExecutionExplainProjectionResult Success(ExecutionExplainArtifact artifact) =>
        new(Guard.RequireNotNull(artifact), DocumentValidationResult.Valid);

    /// <summary>Creates a failed structural projection.</summary>
    /// <param name="diagnostics">One or more structured error diagnostics.</param>
    /// <returns>A failed projection with deterministic diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="diagnostics"/> contains no error.</exception>
    public static ExecutionExplainProjectionResult Failure(
        IEnumerable<DocumentValidationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var validation = DocumentValidationResult.FromDiagnostics(
            diagnostics.OrderBy(static item => item, DocumentValidationDiagnosticComparer.Ordinal));
        if (validation.IsValid)
            throw new ArgumentException("A failed explain projection requires an error diagnostic.", nameof(diagnostics));
        return new(null, validation);
    }
}

/// <summary>Projects existing canonical execution artifacts into the shared explain envelope.</summary>
public static class ExecutionExplainArtifactProjector
{
    /// <summary>Projects an available lifecycle prefix without rerunning compilation or execution.</summary>
    /// <param name="document">Canonical definition document being explained.</param>
    /// <param name="interpreter">Exact interpreter profile used or assessed.</param>
    /// <param name="evidence">Optional block-owned requirement, realization, decision, or receipt references.</param>
    /// <param name="trace">Optional normalized semantic trace.</param>
    /// <param name="runtimeStatus">Optional safe runtime-status observation.</param>
    /// <param name="diagnostics">Optional lifecycle diagnostics from existing authorities.</param>
    /// <returns>An explain artifact, or structured diagnostics when supplied artifacts disagree on affinity.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="document"/> or <paramref name="interpreter"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An evidence or diagnostic collection is malformed.</exception>
    /// <exception cref="InvalidOperationException">Explain content cannot be materialized for fingerprinting.</exception>
    /// <exception cref="JsonException">Explain content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Explain content contains an unsupported serialization type.</exception>
    public static ExecutionExplainProjectionResult Project(
        ExecutionDefinitionDocument document,
        ExecutionInterpreterProfileReference interpreter,
        ImmutableArray<ExecutionExplainEvidence> evidence = default,
        NormalizedExecutionTrace? trace = null,
        ExecutionStatus? runtimeStatus = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(interpreter);
        var definition = ExecutionExplainDefinitionReference.From(document);
        List<DocumentValidationDiagnostic> affinityDiagnostics = [];
        if (trace is not null && (trace.Definition != definition.Definition || trace.Kind != definition.Kind))
        {
            affinityDiagnostics.Add(Error(
                ExecutionExplainDiagnosticCodes.TraceDefinitionMismatch,
                "Normalized trace belongs to another exact execution definition.",
                ExecutionExplainStageNames.ExecutionTrace,
                definition.Definition.DefinitionId.Value,
                expected: DefinitionIdentity(definition.Definition),
                observed: DefinitionIdentity(trace.Definition)));
        }
        if (runtimeStatus is not null && runtimeStatus.Definition != definition.Definition)
        {
            affinityDiagnostics.Add(Error(
                ExecutionExplainDiagnosticCodes.RuntimeStatusMismatch,
                "Runtime status belongs to another exact execution definition.",
                ExecutionExplainStageNames.RuntimeStatus,
                definition.Definition.DefinitionId.Value,
                expected: DefinitionIdentity(definition.Definition),
                observed: DefinitionIdentity(runtimeStatus.Definition)));
        }
        if (trace?.Continuation is { } continuation
            && runtimeStatus is not null
            && (runtimeStatus.ProcessInstanceId != continuation.ProcessInstanceId
                || runtimeStatus.CurrentAttemptId != continuation.ProcessAttemptId))
        {
            affinityDiagnostics.Add(Error(
                ExecutionExplainDiagnosticCodes.RuntimeStatusMismatch,
                "Runtime status and trace name different Process continuations.",
                ExecutionExplainStageNames.RuntimeStatus,
                continuation.ProcessInstanceId.Value,
                expected: $"{continuation.ProcessInstanceId.Value}/{continuation.ProcessAttemptId.Value}",
                observed: $"{runtimeStatus.ProcessInstanceId.Value}/{runtimeStatus.CurrentAttemptId.Value}"));
        }
        if (affinityDiagnostics.Count != 0)
            return ExecutionExplainProjectionResult.Failure(affinityDiagnostics);

        List<ExecutionExplainEvidence> projectedEvidence =
        [
            new(
                ExecutionExplainStageNames.Definition,
                definition.Kind.Value,
                definition.Definition.DefinitionId.Value,
                ExecutionExplainEvidenceAuthority.Declared,
                "Available",
                sourceReferences: [definition.Provenance.Source.Reference]),
            new(
                ExecutionExplainStageNames.InterpreterProfile,
                "execution.interpreterProfile",
                interpreter.Id,
                ExecutionExplainEvidenceAuthority.Declared,
                interpreter.Supports(definition.Kind, definition.SchemaVersion) ? "Supported" : "Unavailable",
                relatedSubjects: [definition.Kind.Value, definition.SchemaVersion.Value],
                sourceReferences: [interpreter.Provenance.Source.Reference])
        ];
        if (!evidence.IsDefaultOrEmpty)
            projectedEvidence.AddRange(evidence);

        if (trace is not null)
        {
            projectedEvidence.Add(new(
                ExecutionExplainStageNames.ExecutionTrace,
                "execution.normalizedTrace",
                trace.Activation.Value,
                ExecutionExplainEvidenceAuthority.Interpreted,
                trace.Disposition,
                relatedSubjects: trace.Continuation is null
                    ? [DefinitionIdentity(trace.Definition)]
                    :
                    [
                        DefinitionIdentity(trace.Definition),
                        trace.Continuation.ProcessInstanceId.Value,
                        trace.Continuation.ProcessAttemptId.Value
                    ],
                sourceReferences:
                [
                    .. trace.Events.SelectMany(static item => item.SourceReferences).Distinct(StringComparer.Ordinal)
                ]));
        }

        if (runtimeStatus is not null)
        {
            projectedEvidence.Add(new(
                ExecutionExplainStageNames.RuntimeStatus,
                "execution.runtimeStatus",
                runtimeStatus.ProcessInstanceId.Value,
                ExecutionExplainEvidenceAuthority.Measured,
                runtimeStatus.CurrentAttempt.Disposition.ToString(),
                relatedSubjects: [runtimeStatus.CurrentAttemptId.Value],
                sourceReferences: [definition.Provenance.Source.Reference]));
            foreach (var extension in runtimeStatus.Runtime.Extensions)
            {
                projectedEvidence.Add(new(
                    ExecutionExplainStageNames.RuntimeStatus,
                    extension.Id.Value,
                    runtimeStatus.ProcessInstanceId.Value,
                    ExecutionExplainEvidenceAuthority.Measured,
                    extension.Value.Disclosure.ToString(),
                    relatedSubjects: [extension.SchemaVersion.Value],
                    sourceReferences: [extension.Provenance.Source.Reference]));
            }
        }

        List<DocumentValidationDiagnostic> projectedDiagnostics = [];
        foreach (var diagnostic in document.Metadata.Diagnostics)
        {
            projectedDiagnostics.Add(document.Metadata.SourceMap.WithResolvedSourceReferences(
                diagnostic,
                document.Metadata.Provenance.Source.Reference,
                ExecutionExplainStageNames.Definition));
        }
        if (!diagnostics.IsDefaultOrEmpty)
        {
            foreach (var diagnostic in diagnostics)
            {
                projectedDiagnostics.Add(document.Metadata.SourceMap.WithResolvedSourceReferences(
                    diagnostic,
                    document.Metadata.Provenance.Source.Reference,
                    ExecutionExplainStageNames.StaticCompilation));
            }
        }
        if (!interpreter.Supports(definition.Kind, definition.SchemaVersion))
        {
            projectedDiagnostics.Add(Error(
                ExecutionExplainDiagnosticCodes.ProfileUnsupported,
                "The interpreter profile does not declare support for this exact definition kind and schema.",
                ExecutionExplainStageNames.InterpreterProfile,
                interpreter.Id,
                expected: $"{definition.Kind.Value}@{definition.SchemaVersion.Value}",
                observed: $"{interpreter.Id}@{interpreter.Version}",
                sourceReferences: [interpreter.Provenance.Source.Reference]));
        }

        return ExecutionExplainProjectionResult.Success(new(
            ExecutionExplainArtifact.CurrentSchemaVersion,
            definition,
            interpreter,
            [.. projectedEvidence],
            trace is null ? null : ExecutionExplainTraceReference.From(trace),
            runtimeStatus,
            [.. projectedDiagnostics]));
    }

    static DocumentValidationDiagnostic Error(
        string code,
        string message,
        string stage,
        string subject,
        string? expected = null,
        string? observed = null,
        ImmutableArray<string> sourceReferences = default) => new(
            code,
            DiagnosticSeverity.Error,
            message,
            Evidence: new(
                stage: stage,
                subject: subject,
                sourceReferences: sourceReferences,
                expected: expected,
                observed: observed));

    static string DefinitionIdentity(ExecutionDefinitionReference definition) =>
        $"{definition.DefinitionId.Value}@{definition.RevisionId.Value}#{definition.Fingerprint.Value}";
}
