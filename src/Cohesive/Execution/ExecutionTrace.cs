using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable diagnostic codes produced while normalizing execution traces.</summary>
public static class ExecutionTraceDiagnosticCodes
{
    /// <summary>The supplied evidence names a different definition than its interpretation plan or checkpoint.</summary>
    public const string DefinitionMismatch = "execution.trace.definitionMismatch";

    /// <summary>The supplied evidence names a different activation than its decision or durable receipt.</summary>
    public const string ActivationMismatch = "execution.trace.activationMismatch";

    /// <summary>The supplied Process evidence names a different continuation than its decision or checkpoint.</summary>
    public const string ContinuationMismatch = "execution.trace.continuationMismatch";

    /// <summary>An event sequence is discontinuous or an event contradicts its enclosing activation lineage.</summary>
    public const string EventInvalid = "execution.trace.eventInvalid";

    /// <summary>An emitted interaction cannot be correlated with its canonical envelope evidence.</summary>
    public const string EmissionEvidenceMismatch = "execution.trace.emissionEvidenceMismatch";

    /// <summary>A durable activation receipt has an invalid or contradictory commit sequence.</summary>
    public const string CommitEvidenceInvalid = "execution.trace.commitEvidenceInvalid";
}

/// <summary>Availability of one payload-safe trace-evidence facet.</summary>
public enum ExecutionTraceEvidenceDisclosure
{
    /// <summary>The producer supports the facet but has no authoritative observation for this occurrence.</summary>
    Unknown = 0,

    /// <summary>The producer disclosed complete authoritative evidence for the facet.</summary>
    Disclosed = 1,

    /// <summary>The producer has authoritative evidence but policy intentionally withheld it.</summary>
    Redacted = 2,

    /// <summary>The producer expected the evidence, but it could not be acquired or retained.</summary>
    Unavailable = 3,

    /// <summary>The producing interpreter cannot observe this evidence facet.</summary>
    Unsupported = 4
}

/// <summary>Semantic kind of one replay-stable Process occurrence.</summary>
public enum ProcessTraceOccurrenceKind
{
    /// <summary>No occurrence kind was supplied; invalid for occurrence evidence.</summary>
    Unspecified = 0,

    /// <summary>One exact child Process invocation.</summary>
    Child = 1,

    /// <summary>One bounded partition coordinator occurrence.</summary>
    Partition = 2,

    /// <summary>One explicit recurrence occurrence.</summary>
    Recurrence = 3
}

/// <summary>Payload-safe replay-stable identity and lineage for one Process occurrence.</summary>
/// <remarks>
/// Disclosed child evidence retains the exact child definition and continuation. A partitioned child may also
/// retain its authored progress identity, which identifies the item without retaining the partition payload.
/// Partition and recurrence evidence retain their opaque reference-interpreter registrations and progress counts.
/// Non-disclosed evidence retains only its semantic kind and explicit disclosure reason.
/// </remarks>
public sealed record ProcessTraceOccurrenceEvidence
{
    /// <summary>Creates one Process occurrence-evidence value.</summary>
    /// <param name="disclosure">Whether authoritative occurrence evidence is disclosed.</param>
    /// <param name="kind">Child, partition, or recurrence occurrence kind.</param>
    /// <param name="registrationId">Replay-stable opaque occurrence registration when disclosed.</param>
    /// <param name="ownerToken">Parent coordination token, or recurrence token, when disclosed.</param>
    /// <param name="occurrence">Zero-based occurrence in the owning token history when disclosed.</param>
    /// <param name="progressIdentity">Payload-safe partition progress identity for a partitioned child.</param>
    /// <param name="definition">Exact child Process definition for disclosed child evidence.</param>
    /// <param name="continuation">Exact child Process continuation for disclosed child evidence.</param>
    /// <param name="repeatCount">Number of admitted repeats for disclosed recurrence evidence.</param>
    /// <param name="unchangedProgressCount">
    /// Consecutive unchanged-progress repeats for disclosed recurrence evidence.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="disclosure"/> or <paramref name="kind"/> is unsupported; a count is negative; or unchanged
    /// progress exceeds the repeat count.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Disclosed evidence is incomplete or contradicts its occurrence kind, or non-disclosed evidence carries
    /// occurrence details.
    /// </exception>
    [JsonConstructor]
    public ProcessTraceOccurrenceEvidence(
        ExecutionTraceEvidenceDisclosure disclosure,
        ProcessTraceOccurrenceKind kind,
        string? registrationId = null,
        TokenId? ownerToken = null,
        long? occurrence = null,
        string? progressIdentity = null,
        ExecutionDefinitionReference? definition = null,
        ProcessContinuationIdentity? continuation = null,
        int? repeatCount = null,
        int? unchangedProgressCount = null)
    {
        if (!Enum.IsDefined(disclosure))
            throw new ArgumentOutOfRangeException(nameof(disclosure), disclosure, "Unsupported trace disclosure.");
        if (!Enum.IsDefined(kind) || kind == ProcessTraceOccurrenceKind.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A Process occurrence kind must be explicit.");
        if (occurrence < 0)
            throw new ArgumentOutOfRangeException(nameof(occurrence), occurrence, "An occurrence cannot be negative.");
        if (repeatCount < 0)
            throw new ArgumentOutOfRangeException(nameof(repeatCount), repeatCount, "A repeat count cannot be negative.");
        if (unchangedProgressCount < 0 || unchangedProgressCount > repeatCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(unchangedProgressCount),
                unchangedProgressCount,
                "An unchanged-progress count must be nonnegative and cannot exceed the repeat count.");
        }

        var hasDisclosedIdentity = !string.IsNullOrWhiteSpace(registrationId)
            && ownerToken is { } owner && !string.IsNullOrWhiteSpace(owner.Value)
            && occurrence is not null;
        if (disclosure != ExecutionTraceEvidenceDisclosure.Disclosed)
        {
            if (registrationId is not null
                || ownerToken is not null
                || occurrence is not null
                || progressIdentity is not null
                || definition is not null
                || continuation is not null
                || repeatCount is not null
                || unchangedProgressCount is not null)
            {
                throw new ArgumentException(
                    "Non-disclosed Process occurrence evidence cannot carry occurrence details.",
                    nameof(disclosure));
            }
        }
        else if (!hasDisclosedIdentity)
        {
            throw new ArgumentException(
                "Disclosed Process occurrence evidence requires registration, owner-token, and occurrence identities.",
                nameof(registrationId));
        }
        else
        {
            if (progressIdentity is not null && string.IsNullOrWhiteSpace(progressIdentity))
            {
                throw new ArgumentException(
                    "A present partition progress identity cannot be empty or white-space.",
                    nameof(progressIdentity));
            }

            var isChild = kind == ProcessTraceOccurrenceKind.Child;
            if (isChild != (definition is not null) || isChild != (continuation is not null))
            {
                throw new ArgumentException(
                    "Only disclosed child evidence requires an exact related definition and continuation.",
                    nameof(definition));
            }
            if (kind != ProcessTraceOccurrenceKind.Child && progressIdentity is not null)
            {
                throw new ArgumentException(
                    "Only a disclosed partitioned child can carry a progress identity.",
                    nameof(progressIdentity));
            }

            var isRecurrence = kind == ProcessTraceOccurrenceKind.Recurrence;
            if (isRecurrence != (repeatCount is not null) || isRecurrence != (unchangedProgressCount is not null))
            {
                throw new ArgumentException(
                    "Only disclosed recurrence evidence requires repeat and unchanged-progress counts.",
                    nameof(repeatCount));
            }
        }

        Disclosure = disclosure;
        Kind = kind;
        RegistrationId = registrationId;
        OwnerToken = ownerToken;
        Occurrence = occurrence;
        ProgressIdentity = progressIdentity;
        Definition = definition;
        Continuation = continuation;
        RepeatCount = repeatCount;
        UnchangedProgressCount = unchangedProgressCount;
    }

    /// <summary>Whether authoritative occurrence evidence is disclosed.</summary>
    public ExecutionTraceEvidenceDisclosure Disclosure { get; }

    /// <summary>Child, partition, or recurrence occurrence kind.</summary>
    public ProcessTraceOccurrenceKind Kind { get; }

    /// <summary>Replay-stable opaque occurrence registration when disclosed.</summary>
    public string? RegistrationId { get; }

    /// <summary>Parent coordination token, or recurrence token, when disclosed.</summary>
    public TokenId? OwnerToken { get; }

    /// <summary>Zero-based occurrence in the owning token history when disclosed.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? Occurrence { get; }

    /// <summary>Payload-safe partition progress identity for a disclosed partitioned child.</summary>
    public string? ProgressIdentity { get; }

    /// <summary>Exact related child Process definition when disclosed.</summary>
    public ExecutionDefinitionReference? Definition { get; }

    /// <summary>Exact related child Process instance and attempt when disclosed.</summary>
    public ProcessContinuationIdentity? Continuation { get; }

    /// <summary>Number of admitted repeats for disclosed recurrence evidence.</summary>
    public int? RepeatCount { get; }

    /// <summary>Consecutive unchanged-progress repeats for disclosed recurrence evidence.</summary>
    public int? UnchangedProgressCount { get; }
}

/// <summary>One payload-safe event in normalized semantic execution order.</summary>
/// <remarks>
/// The event retains identities and decision evidence already present in an authoritative block trace. It never
/// carries invocation, observation, patch, interaction, or result payload values. <see cref="Kind"/> is the
/// camel-case convention projection of the originating block event kind, not a second event taxonomy.
/// </remarks>
public sealed record NormalizedExecutionTraceEvent
{
    /// <summary>Creates one normalized trace event.</summary>
    /// <param name="sequence">Zero-based sequence within the semantic activation.</param>
    /// <param name="kind">Stable block-owned event-kind name.</param>
    /// <param name="node">Canonical execution node associated with the event.</param>
    /// <param name="token">Durable Process token, or <see langword="null"/> for a direct Transition event.</param>
    /// <param name="branchOrClause">Selected branch, case, clause, or related node when present.</param>
    /// <param name="emission">Logical interaction emission identity when present.</param>
    /// <param name="correlation">Interaction correlation identity when envelope evidence is available.</param>
    /// <param name="causation">Causal emission identity when envelope evidence is available.</param>
    /// <param name="idempotencyKey">Logical interaction deduplication identity when envelope evidence is available.</param>
    /// <param name="emissionFingerprint">Complete canonical envelope fingerprint when present.</param>
    /// <param name="relatedDefinition">Referenced interaction or Machine definition when present.</param>
    /// <param name="relatedNode">Referenced Machine edge or other related semantic node when present.</param>
    /// <param name="semanticPath">Aggregate-relative observation or change path when present.</param>
    /// <param name="changed">Whether a Transition write changed semantic value when present.</param>
    /// <param name="operationOccurrence">Zero-based Process operation occurrence when present.</param>
    /// <param name="inputDisposition">Block-owned Process input disposition name when present.</param>
    /// <param name="inputReason">Block-owned Process input-reason name when present.</param>
    /// <param name="waitRegistrationId">Exact Process wait occurrence when present.</param>
    /// <param name="processOccurrence">Typed child, partition, or recurrence lineage evidence when applicable.</param>
    /// <param name="requestOutcome">Exact terminal Request outcome identity when a Reply participated.</param>
    /// <param name="detail">Stable non-sensitive detail retained by the authoritative block trace.</param>
    /// <param name="sourceReferences">Producer source references in deterministic ordinal order.</param>
    /// <exception cref="ArgumentException">An identity, kind, path, or source reference is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sequence"/> or <paramref name="operationOccurrence"/> is negative.
    /// </exception>
    [JsonConstructor]
    public NormalizedExecutionTraceEvent(
        int sequence,
        string kind,
        ExecutionNodeId node,
        TokenId? token = null,
        ExecutionNodeId? branchOrClause = null,
        EmissionId? emission = null,
        InteractionCorrelationId? correlation = null,
        EmissionId? causation = null,
        InteractionIdempotencyKey? idempotencyKey = null,
        InteractionEnvelopeContentFingerprint? emissionFingerprint = null,
        ExecutionDefinitionReference? relatedDefinition = null,
        ExecutionNodeId? relatedNode = null,
        FieldPath? semanticPath = null,
        bool? changed = null,
        long? operationOccurrence = null,
        string? inputDisposition = null,
        string? inputReason = null,
        ProcessWaitRegistrationId? waitRegistrationId = null,
        ProcessTraceOccurrenceEvidence? processOccurrence = null,
        RequestTerminalOutcomeId? requestOutcome = null,
        string? detail = null,
        ImmutableArray<string> sourceReferences = default)
    {
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "A trace-event sequence cannot be negative.");
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A normalized trace event requires a stable node identity.", nameof(node));
        RequireOptionalIdentity(token?.Value, nameof(token));
        RequireOptionalIdentity(branchOrClause?.Value, nameof(branchOrClause));
        RequireOptionalIdentity(emission?.Value, nameof(emission));
        RequireOptionalIdentity(correlation?.Value, nameof(correlation));
        RequireOptionalIdentity(causation?.Value, nameof(causation));
        RequireOptionalIdentity(idempotencyKey?.Value, nameof(idempotencyKey));
        RequireOptionalIdentity(emissionFingerprint?.Value, nameof(emissionFingerprint));
        RequireOptionalIdentity(relatedNode?.Value, nameof(relatedNode));
        RequireOptionalIdentity(waitRegistrationId?.Value, nameof(waitRegistrationId));
        RequireOptionalIdentity(requestOutcome?.Value, nameof(requestOutcome));
        if (semanticPath is { Segments.IsDefaultOrEmpty: true })
            throw new ArgumentException("A normalized semantic path cannot be empty.", nameof(semanticPath));
        if (operationOccurrence < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationOccurrence),
                operationOccurrence,
                "An operation occurrence cannot be negative.");
        }
        if (emission is null
            && (correlation is not null || causation is not null || idempotencyKey is not null || emissionFingerprint is not null))
        {
            throw new ArgumentException(
                "Interaction correlation, causation, idempotency, and envelope fingerprints require an emission identity.",
                nameof(emission));
        }

        Sequence = sequence;
        Kind = Guard.RequireNotNullOrWhiteSpace(kind);
        Node = node;
        Token = token;
        BranchOrClause = branchOrClause;
        Emission = emission;
        Correlation = correlation;
        Causation = causation;
        IdempotencyKey = idempotencyKey;
        EmissionFingerprint = emissionFingerprint;
        RelatedDefinition = relatedDefinition;
        RelatedNode = relatedNode;
        SemanticPath = semanticPath;
        Changed = changed;
        OperationOccurrence = operationOccurrence;
        InputDisposition = inputDisposition.TrimmedEmptyOrWhiteSpaceAs();
        InputReason = inputReason.TrimmedEmptyOrWhiteSpaceAs();
        WaitRegistrationId = waitRegistrationId;
        ProcessOccurrence = processOccurrence;
        RequestOutcome = requestOutcome;
        Detail = detail.TrimmedEmptyOrWhiteSpaceAs();
        SourceReferences = NormalizeSourceReferences(sourceReferences);
    }

    /// <summary>Zero-based sequence within the semantic activation.</summary>
    public int Sequence { get; }

    /// <summary>Stable block-owned event-kind name.</summary>
    public string Kind { get; }

    /// <summary>Canonical execution node associated with the event.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Durable Process token, or <see langword="null"/> for a direct Transition event.</summary>
    public TokenId? Token { get; }

    /// <summary>Selected branch, case, clause, or related node when present.</summary>
    public ExecutionNodeId? BranchOrClause { get; }

    /// <summary>Logical interaction emission identity when present.</summary>
    public EmissionId? Emission { get; }

    /// <summary>Interaction correlation identity when canonical envelope evidence is available.</summary>
    public InteractionCorrelationId? Correlation { get; }

    /// <summary>Causal emission identity when canonical envelope evidence is available.</summary>
    public EmissionId? Causation { get; }

    /// <summary>Logical interaction deduplication identity when canonical envelope evidence is available.</summary>
    public InteractionIdempotencyKey? IdempotencyKey { get; }

    /// <summary>Complete canonical interaction-envelope fingerprint when present.</summary>
    public InteractionEnvelopeContentFingerprint? EmissionFingerprint { get; }

    /// <summary>Referenced interaction or Machine definition when present.</summary>
    public ExecutionDefinitionReference? RelatedDefinition { get; }

    /// <summary>Referenced Machine edge or other related semantic node when present.</summary>
    public ExecutionNodeId? RelatedNode { get; }

    /// <summary>Aggregate-relative observation or change path when present.</summary>
    public FieldPath? SemanticPath { get; }

    /// <summary>Whether a Transition write changed semantic value when present.</summary>
    public bool? Changed { get; }

    /// <summary>Zero-based Process operation occurrence when present.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? OperationOccurrence { get; }

    /// <summary>Block-owned Process input disposition name when present.</summary>
    public string? InputDisposition { get; }

    /// <summary>Block-owned Process input-reason name when present.</summary>
    public string? InputReason { get; }

    /// <summary>Exact Process wait occurrence when present.</summary>
    public ProcessWaitRegistrationId? WaitRegistrationId { get; }

    /// <summary>Typed child, partition, or recurrence lineage evidence when applicable.</summary>
    public ProcessTraceOccurrenceEvidence? ProcessOccurrence { get; }

    /// <summary>Exact terminal Request outcome identity when a Reply participated.</summary>
    public RequestTerminalOutcomeId? RequestOutcome { get; }

    /// <summary>Stable non-sensitive detail retained by the authoritative block trace.</summary>
    public string? Detail { get; }

    /// <summary>Producer source references in deterministic ordinal order.</summary>
    public ImmutableArray<string> SourceReferences { get; }

    static void RequireOptionalIdentity(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An optional trace identity cannot be empty.", parameterName);
    }

    static ImmutableArray<string> NormalizeSourceReferences(ImmutableArray<string> sourceReferences)
    {
        if (sourceReferences.IsDefaultOrEmpty)
            return [];
        if (sourceReferences.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Trace source references cannot be empty.", nameof(sourceReferences));

        var normalized = sourceReferences
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        return normalized.SequenceEqual(sourceReferences) ? sourceReferences : normalized;
    }
}

/// <summary>One versioned normalized semantic trace for a finite Transition or Process activation.</summary>
public sealed record NormalizedExecutionTrace
{
    /// <summary>Current normalized execution-trace schema.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } =
        new("cohesive-execution-trace/v2");

    /// <summary>Creates one normalized execution trace.</summary>
    /// <param name="schemaVersion">Exact normalized-trace schema.</param>
    /// <param name="kind">Canonical Transition or Process definition kind.</param>
    /// <param name="definition">Exact definition identity, revision, and fingerprint.</param>
    /// <param name="continuation">Logical Process instance and attempt, or null for a direct Transition.</param>
    /// <param name="activation">Finite semantic activation identity.</param>
    /// <param name="disposition">Block-owned terminal activation-decision name.</param>
    /// <param name="safePointNode">First deterministic durable boundary when one stopped a Process activation.</param>
    /// <param name="durableCommitSequence">
    /// Positive durable commit sequence, or null when the projection is semantic reference evidence only.
    /// </param>
    /// <param name="events">Complete normalized event sequence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Schema, kind, lineage, or event evidence is inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="durableCommitSequence"/> is not positive.</exception>
    [JsonConstructor]
    public NormalizedExecutionTrace(
        ExecutionIrSchemaVersion schemaVersion,
        ExecutionDefinitionKind kind,
        ExecutionDefinitionReference definition,
        ProcessContinuationIdentity? continuation,
        ActivationId activation,
        string disposition,
        ExecutionNodeId? safePointNode,
        long? durableCommitSequence,
        ImmutableArray<NormalizedExecutionTraceEvent> events)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
            throw new ArgumentException("A normalized trace requires an explicit schema version.", nameof(schemaVersion));
        if (string.IsNullOrWhiteSpace(kind.Value))
            throw new ArgumentException("A normalized trace requires an explicit definition kind.", nameof(kind));
        if (string.IsNullOrWhiteSpace(activation.Value))
            throw new ArgumentException("A normalized trace requires a stable activation identity.", nameof(activation));
        if (safePointNode is { } safePoint && string.IsNullOrWhiteSpace(safePoint.Value))
            throw new ArgumentException("A normalized safe-point node cannot be default.", nameof(safePointNode));
        if (durableCommitSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durableCommitSequence),
                durableCommitSequence,
                "A durable commit sequence must be positive.");
        }
        if (events.IsDefault || events.Any(static item => item is null))
            throw new ArgumentException("Normalized trace events must be initialized and non-null.", nameof(events));
        for (var index = 0; index < events.Length; index++)
        {
            if (events[index].Sequence != index)
            {
                throw new ArgumentException(
                    "Normalized trace events must have a continuous zero-based sequence.",
                    nameof(events));
            }

            if ((continuation is null) == events[index].Token.HasValue)
            {
                throw new ArgumentException(
                    "Process trace events require tokens and direct Transition trace events cannot carry them.",
                    nameof(events));
            }
        }

        SchemaVersion = schemaVersion;
        Kind = kind;
        Definition = Guard.RequireNotNull(definition);
        Continuation = continuation;
        Activation = activation;
        Disposition = Guard.RequireNotNullOrWhiteSpace(disposition);
        SafePointNode = safePointNode;
        DurableCommitSequence = durableCommitSequence;
        Events = events;
    }

    /// <summary>Exact normalized-trace schema.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Canonical Transition or Process definition kind.</summary>
    public ExecutionDefinitionKind Kind { get; }

    /// <summary>Exact definition identity, revision, and fingerprint.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Logical Process instance and attempt, or null for a direct Transition.</summary>
    public ProcessContinuationIdentity? Continuation { get; }

    /// <summary>Finite semantic activation identity.</summary>
    public ActivationId Activation { get; }

    /// <summary>Block-owned terminal activation-decision name.</summary>
    public string Disposition { get; }

    /// <summary>First deterministic durable boundary when one stopped a Process activation.</summary>
    public ExecutionNodeId? SafePointNode { get; }

    /// <summary>Positive durable commit sequence, or null for semantic reference evidence.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? DurableCommitSequence { get; }

    /// <summary>Complete normalized event sequence.</summary>
    public ImmutableArray<NormalizedExecutionTraceEvent> Events { get; }
}

/// <summary>Result of projecting canonical block evidence into one normalized trace.</summary>
public sealed record ExecutionTraceProjectionResult
{
    /// <summary>Creates a trace projection result.</summary>
    /// <param name="trace">Normalized trace when projection succeeded.</param>
    /// <param name="validation">Structured deterministic projection diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="validation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Trace presence contradicts diagnostic validity.</exception>
    public ExecutionTraceProjectionResult(
        NormalizedExecutionTrace? trace,
        DocumentValidationResult validation)
    {
        Validation = Guard.RequireNotNull(validation);
        if ((trace is not null) != validation.IsValid)
        {
            throw new ArgumentException(
                "A normalized trace exists exactly when projection diagnostics contain no errors.",
                nameof(trace));
        }

        Trace = trace;
    }

    /// <summary>Normalized trace when projection succeeded.</summary>
    public NormalizedExecutionTrace? Trace { get; }

    /// <summary>Structured deterministic projection diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Whether projection produced a normalized trace.</summary>
    public bool IsSuccessful => Trace is not null;

    /// <summary>Creates a successful projection.</summary>
    /// <param name="trace">Normalized trace.</param>
    /// <returns>A valid projection result containing <paramref name="trace"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="trace"/> is <see langword="null"/>.</exception>
    public static ExecutionTraceProjectionResult Success(NormalizedExecutionTrace trace) =>
        new(Guard.RequireNotNull(trace), DocumentValidationResult.Valid);

    /// <summary>Creates a failed projection.</summary>
    /// <param name="diagnostics">One or more projection errors.</param>
    /// <returns>A failed projection containing deterministically ordered diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="diagnostics"/> contains no errors.</exception>
    public static ExecutionTraceProjectionResult Failure(
        IEnumerable<DocumentValidationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var normalized = diagnostics.Order(DocumentValidationDiagnosticComparer.Ordinal).ToImmutableArray();
        var validation = DocumentValidationResult.FromDiagnostics(normalized);
        if (validation.IsValid)
            throw new ArgumentException("A failed trace projection requires at least one error diagnostic.", nameof(diagnostics));
        return new(trace: null, validation);
    }
}
