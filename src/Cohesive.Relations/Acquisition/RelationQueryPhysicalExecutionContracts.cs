using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Acquisition;

/// <summary>Observations supplied directly for one compiled relation-root source input.</summary>
public sealed record RelationQuerySuppliedSourceInput
{
    /// <summary>Creates directly supplied source input.</summary>
    /// <param name="input">Compiled relation-root source-set input.</param>
    /// <param name="completeness">Whether the supplied set is authoritative and complete.</param>
    /// <param name="observations">Identity-bearing supplied observations.</param>
    /// <param name="evidenceReference">Optional opaque provenance reference.</param>
    /// <exception cref="ArgumentException">The input is default, observations contain null or duplicate identities, or the reference is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="completeness"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQuerySuppliedSourceInput(
        RelationQueryInputId input,
        RelationQueryEvidenceCompleteness completeness,
        ImmutableArray<RelationQuerySourceReadObservation> observations,
        string? evidenceReference = null)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
            throw new ArgumentException("Supplied source input requires a compiled input identity.", nameof(input));
        if (!Enum.IsDefined(completeness))
            throw new ArgumentOutOfRangeException(nameof(completeness), completeness, "Unsupported evidence completeness.");
        var normalized = observations.IsDefault ? [] : observations;
        if (normalized.Any(static observation => observation is null))
            throw new ArgumentException("Supplied observations cannot contain null entries.", nameof(observations));
        if (normalized.GroupBy(static observation => observation.Identity, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1))
            throw new ArgumentException("Supplied observations cannot repeat an identity.", nameof(observations));
        if (evidenceReference is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        Input = input;
        Completeness = completeness;
        Observations = [.. normalized.OrderBy(static observation => observation.Identity, StringComparer.Ordinal)];
        EvidenceReference = evidenceReference;
    }

    /// <summary>Compiled relation-root source-set input.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Whether the supplied set is authoritative and complete.</summary>
    public RelationQueryEvidenceCompleteness Completeness { get; }

    /// <summary>Identity-bearing supplied observations in deterministic order.</summary>
    public ImmutableArray<RelationQuerySourceReadObservation> Observations { get; }

    /// <summary>Opaque supplied-input provenance reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Request for bounded physical acquisition followed by canonical semantic interpretation.</summary>
public sealed class RelationQueryPhysicalExecutionRequest
{
    /// <summary>Creates a physical execution request.</summary>
    /// <param name="plan">Exact successful semantic plan.</param>
    /// <param name="physicalPlan">Exact compiled physical plan to execute.</param>
    /// <param name="realization">Exact successful realization report cited by the physical plan.</param>
    /// <param name="evaluation">Stable identity for this runtime evaluation.</param>
    /// <param name="suppliedSources">Directly supplied relation-root inputs.</param>
    /// <param name="parameters">Invocation-parameter evidence.</param>
    /// <param name="capabilities">Invocation-scoped expression-capability evidence.</param>
    /// <param name="conversionFailures">Explicit conversion failures known before acquisition.</param>
    /// <param name="requirementGapPolicy">Runtime requirement-gap policy, or <see langword="null"/> for the conventional policy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/>, <paramref name="physicalPlan"/>, or <paramref name="realization"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="evaluation"/> is default, an evidence collection contains a <see langword="null"/> entry, or
    /// <paramref name="suppliedSources"/> repeats a compiled input.
    /// </exception>
    public RelationQueryPhysicalExecutionRequest(
        CompiledRelationQueryPlan plan,
        CompiledRelationQueryPhysicalPlan physicalPlan,
        RelationQueryRealizationReport realization,
        RelationQueryEvaluationId evaluation,
        ImmutableArray<RelationQuerySuppliedSourceInput> suppliedSources = default,
        ImmutableArray<RelationQueryParameterEvidence> parameters = default,
        ImmutableArray<RelationQueryCapabilityEvidence> capabilities = default,
        ImmutableArray<RelationQueryConversionFailureEvidence> conversionFailures = default,
        IRelationRequirementGapPolicy? requirementGapPolicy = null)
    {
        Plan = Guard.RequireNotNull(plan);
        PhysicalPlan = Guard.RequireNotNull(physicalPlan);
        Realization = Guard.RequireNotNull(realization);
        if (string.IsNullOrWhiteSpace(evaluation.Value))
            throw new ArgumentException("Physical execution requires an evaluation identity.", nameof(evaluation));
        Evaluation = evaluation;
        SuppliedSources = Normalize(suppliedSources, nameof(suppliedSources));
        if (SuppliedSources.GroupBy(static source => source.Input).Any(static group => group.Count() > 1))
            throw new ArgumentException("Supplied sources cannot repeat a compiled input.", nameof(suppliedSources));
        Parameters = Normalize(parameters, nameof(parameters));
        Capabilities = Normalize(capabilities, nameof(capabilities));
        ConversionFailures = Normalize(conversionFailures, nameof(conversionFailures));
        RequirementGapPolicy = requirementGapPolicy ?? RelationRequirementGapPolicy.Conventional;
    }

    /// <summary>Exact successful semantic plan.</summary>
    public CompiledRelationQueryPlan Plan { get; }

    /// <summary>Exact compiled physical plan to execute.</summary>
    public CompiledRelationQueryPhysicalPlan PhysicalPlan { get; }

    /// <summary>Exact successful realization report cited by the physical plan.</summary>
    public RelationQueryRealizationReport Realization { get; }

    /// <summary>Stable identity for this runtime evaluation.</summary>
    public RelationQueryEvaluationId Evaluation { get; }

    /// <summary>Directly supplied relation-root inputs in compiled-input order.</summary>
    public ImmutableArray<RelationQuerySuppliedSourceInput> SuppliedSources { get; }

    /// <summary>Invocation-parameter evidence.</summary>
    public ImmutableArray<RelationQueryParameterEvidence> Parameters { get; }

    /// <summary>Invocation-scoped expression-capability evidence.</summary>
    public ImmutableArray<RelationQueryCapabilityEvidence> Capabilities { get; }

    /// <summary>Explicit conversion failures known before acquisition.</summary>
    public ImmutableArray<RelationQueryConversionFailureEvidence> ConversionFailures { get; }

    /// <summary>Requirement-gap policy used by canonical interpretation.</summary>
    public IRelationRequirementGapPolicy RequirementGapPolicy { get; }

    static ImmutableArray<T> Normalize<T>(ImmutableArray<T> values, string parameterName)
        where T : class
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(static value => value is null))
            throw new ArgumentException("Physical execution evidence cannot contain null entries.", parameterName);
        return normalized;
    }
}

/// <summary>Kind of bounded source operation represented by an execution trace.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQuerySourceReadKind
{
    /// <summary>Bounded complete-set enumeration.</summary>
    BoundedEnumeration = 0,

    /// <summary>Bounded lookup by stable observation identity.</summary>
    IdentityBatch = 1,

    /// <summary>Bounded lookup by relationship-reference predicate.</summary>
    RelationshipKeyBatch = 2
}

/// <summary>Attributable trace of one physical reader request.</summary>
public sealed record RelationQuerySourceReadTrace
{
    /// <summary>Creates a source-read trace.</summary>
    /// <param name="stage">Physical stage that issued the request.</param>
    /// <param name="source">Physical source instance that handled the request.</param>
    /// <param name="kind">Bounded source operation kind.</param>
    /// <param name="batchOrdinal">Zero-based request ordinal within the stage.</param>
    /// <param name="keyCount">Number of lookup keys, or zero for enumeration.</param>
    /// <param name="state">Reader outcome.</param>
    /// <param name="completeness">Whether absence from the result is authoritative.</param>
    /// <param name="returnedRows">Number of returned physical observations.</param>
    /// <param name="evidenceReference">Optional opaque reader evidence reference.</param>
    /// <exception cref="ArgumentException">An identity or evidence reference is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum, ordinal, or count is invalid.</exception>
    [JsonConstructor]
    public RelationQuerySourceReadTrace(
        RelationQueryPhysicalStageId stage,
        RelationQuerySourceInstanceId source,
        RelationQuerySourceReadKind kind,
        int batchOrdinal,
        int keyCount,
        RelationQuerySourceReadState state,
        RelationQueryEvidenceCompleteness completeness,
        int returnedRows,
        string? evidenceReference = null)
    {
        if (string.IsNullOrWhiteSpace(stage.Value) || string.IsNullOrWhiteSpace(source.Value))
            throw new ArgumentException("A source-read trace requires complete physical identities.", nameof(stage));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported source-read kind.");
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported source-read state.");
        if (!Enum.IsDefined(completeness))
            throw new ArgumentOutOfRangeException(nameof(completeness), completeness, "Unsupported evidence completeness.");
        if (batchOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(batchOrdinal), batchOrdinal, "A batch ordinal cannot be negative.");
        if (keyCount < 0)
            throw new ArgumentOutOfRangeException(nameof(keyCount), keyCount, "A key count cannot be negative.");
        if (returnedRows < 0)
            throw new ArgumentOutOfRangeException(nameof(returnedRows), returnedRows, "A returned-row count cannot be negative.");
        if (evidenceReference is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        Stage = stage;
        Source = source;
        Kind = kind;
        BatchOrdinal = batchOrdinal;
        KeyCount = keyCount;
        State = state;
        Completeness = completeness;
        ReturnedRows = returnedRows;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Physical stage that issued the request.</summary>
    public RelationQueryPhysicalStageId Stage { get; }

    /// <summary>Physical source instance that handled the request.</summary>
    public RelationQuerySourceInstanceId Source { get; }

    /// <summary>Bounded source operation kind.</summary>
    public RelationQuerySourceReadKind Kind { get; }

    /// <summary>Zero-based request ordinal within the stage.</summary>
    public int BatchOrdinal { get; }

    /// <summary>Number of lookup keys, or zero for enumeration.</summary>
    public int KeyCount { get; }

    /// <summary>Reader outcome.</summary>
    public RelationQuerySourceReadState State { get; }

    /// <summary>Whether absence from the result is authoritative.</summary>
    public RelationQueryEvidenceCompleteness Completeness { get; }

    /// <summary>Number of returned physical observations.</summary>
    public int ReturnedRows { get; }

    /// <summary>Opaque reader evidence reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Structured failure detected by the physical executor before canonical interpretation.</summary>
public sealed record RelationQueryPhysicalExecutionDiagnostic
{
    /// <summary>Creates an attributable physical-execution diagnostic.</summary>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="severity">Diagnostic severity.</param>
    /// <param name="message">Human-readable explanation without source payload values.</param>
    /// <param name="stage">Affected physical stage, or <see langword="null"/>.</param>
    /// <param name="input">Affected compiled input, or <see langword="null"/>.</param>
    /// <param name="source">Affected physical source, or <see langword="null"/>.</param>
    /// <param name="evidenceReference">Optional opaque provider evidence reference.</param>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A required string or optional identity is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="severity"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQueryPhysicalExecutionDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        RelationQueryPhysicalStageId? stage = null,
        RelationQueryInputId? input = null,
        RelationQuerySourceInstanceId? source = null,
        string? evidenceReference = null)
    {
        Code = Guard.RequireNotNullOrWhiteSpace(code);
        if (!Enum.IsDefined(severity))
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unsupported diagnostic severity.");
        Message = Guard.RequireNotNullOrWhiteSpace(message);
        RequireOptional(stage?.Value, nameof(stage));
        RequireOptional(input?.Value, nameof(input));
        RequireOptional(source?.Value, nameof(source));
        RequireOptional(evidenceReference, nameof(evidenceReference));
        Severity = severity;
        Stage = stage;
        Input = input;
        Source = source;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Stable machine-readable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Diagnostic severity.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Human-readable explanation without source payload values.</summary>
    public string Message { get; }

    /// <summary>Affected physical stage, or <see langword="null"/>.</summary>
    public RelationQueryPhysicalStageId? Stage { get; }

    /// <summary>Affected compiled input, or <see langword="null"/>.</summary>
    public RelationQueryInputId? Input { get; }

    /// <summary>Affected physical source, or <see langword="null"/>.</summary>
    public RelationQuerySourceInstanceId? Source { get; }

    /// <summary>Opaque provider evidence reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }

    static void RequireOptional(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An optional physical-execution attribution cannot be empty.", parameterName);
    }
}

/// <summary>Stable machine-readable physical-execution diagnostic codes.</summary>
public static class RelationQueryPhysicalExecutionDiagnosticCodes
{
    /// <summary>The semantic, realization, placement, or physical plan is stale or mismatched.</summary>
    public const string PlanMismatch = "REL2201";

    /// <summary>A required physical source reader is absent.</summary>
    public const string SourceReaderMissing = "REL2202";

    /// <summary>A reader's source, execution-domain, or capability-profile identity is incompatible.</summary>
    public const string SourceReaderMismatch = "REL2203";

    /// <summary>Directly supplied relation-root input violates its exact placement or field contract.</summary>
    public const string SuppliedInputInvalid = "REL2204";

    /// <summary>A provider result violates the exact source-read request contract.</summary>
    public const string SourceResultInvalid = "REL2205";

    /// <summary>A provider returned more rows, fan-out, or keys than an explicit bound permits.</summary>
    public const string OperatingBoundaryExceeded = "REL2206";

    /// <summary>The physical stage graph is incompatible with the v1 executor.</summary>
    public const string StageInvalid = "REL2207";
}

/// <summary>Overall outcome of composed physical acquisition and canonical interpretation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryPhysicalExecutionStatus
{
    /// <summary>Acquisition and canonical interpretation completed conclusively.</summary>
    Succeeded = 0,

    /// <summary>Attributable partial or inconclusive evidence produced an incomplete interpretation.</summary>
    Incomplete = 1,

    /// <summary>A physical contract violation or canonical execution failure prevented success.</summary>
    Failed = 2
}

/// <summary>Immutable in-process result of bounded acquisition followed by canonical interpretation.</summary>
/// <remarks>
/// This composite runtime result is not a persisted JSON wire contract. When interpretation ran, <see cref="Evidence"/>
/// and <see cref="RelationQueryExecutionResult.Evidence"/> intentionally reference the same immutable snapshot so
/// acquisition attribution and interpreted outputs cannot drift apart.
/// </remarks>
public sealed class RelationQueryPhysicalExecutionResult
{
    /// <summary>Creates a physical execution result.</summary>
    /// <param name="status">Overall physical execution status.</param>
    /// <param name="evidence">
    /// Exact assembled runtime evidence instance, or <see langword="null"/> when preflight failed.
    /// </param>
    /// <param name="interpretation">
    /// Canonical interpretation sharing <paramref name="evidence"/>, or <see langword="null"/> when preflight or
    /// acquisition validation failed.
    /// </param>
    /// <param name="sourceReads">Attributable bounded source-read traces.</param>
    /// <param name="diagnostics">Physical preflight or acquisition-contract diagnostics.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// Result components conflict, interpretation does not share the exact <paramref name="evidence"/> instance,
    /// a collection contains <see langword="null"/>, or diagnostics contain duplicates.
    /// </exception>
    public RelationQueryPhysicalExecutionResult(
        RelationQueryPhysicalExecutionStatus status,
        RelationQueryRuntimeEvidence? evidence,
        RelationQueryExecutionResult? interpretation,
        ImmutableArray<RelationQuerySourceReadTrace> sourceReads = default,
        ImmutableArray<RelationQueryPhysicalExecutionDiagnostic> diagnostics = default)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported physical execution status.");
        if ((status is RelationQueryPhysicalExecutionStatus.Succeeded or RelationQueryPhysicalExecutionStatus.Incomplete)
            && (evidence is null || interpretation is null))
            throw new ArgumentException("A non-failed physical result requires evidence and canonical interpretation.", nameof(evidence));
        if (interpretation is not null && !ReferenceEquals(evidence, interpretation.Evidence))
            throw new ArgumentException("Physical result evidence must be the exact snapshot interpreted canonically.", nameof(interpretation));
        var reads = sourceReads.IsDefault ? [] : sourceReads;
        var normalizedDiagnostics = diagnostics.IsDefault ? [] : diagnostics;
        if (reads.Any(static trace => trace is null))
            throw new ArgumentException("Source-read traces cannot contain null entries.", nameof(sourceReads));
        if (normalizedDiagnostics.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Physical execution diagnostics cannot contain null entries.", nameof(diagnostics));
        if (normalizedDiagnostics.GroupBy(static diagnostic => diagnostic).Any(static group => group.Count() > 1))
            throw new ArgumentException("Physical execution diagnostics cannot contain duplicates.", nameof(diagnostics));
        Status = status;
        Evidence = evidence;
        Interpretation = interpretation;
        SourceReads =
        [
            .. reads.OrderBy(static trace => trace.Stage.Value, StringComparer.Ordinal)
                .ThenBy(static trace => trace.BatchOrdinal)
        ];
        Diagnostics =
        [
            .. normalizedDiagnostics
                .OrderBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Stage?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Input?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Source?.Value ?? string.Empty, StringComparer.Ordinal)
        ];
    }

    /// <summary>Overall physical execution status.</summary>
    public RelationQueryPhysicalExecutionStatus Status { get; }

    /// <summary>
    /// Exact assembled runtime evidence shared with <see cref="RelationQueryExecutionResult.Evidence"/>, or
    /// <see langword="null"/> when interpretation did not run.
    /// </summary>
    public RelationQueryRuntimeEvidence? Evidence { get; }

    /// <summary>Canonical interpretation sharing <see cref="Evidence"/>, or <see langword="null"/> when it could not run.</summary>
    public RelationQueryExecutionResult? Interpretation { get; }

    /// <summary>Bounded source-read traces in deterministic stage and batch order.</summary>
    public ImmutableArray<RelationQuerySourceReadTrace> SourceReads { get; }

    /// <summary>Physical preflight and acquisition-contract diagnostics.</summary>
    public ImmutableArray<RelationQueryPhysicalExecutionDiagnostic> Diagnostics { get; }

    /// <summary>Whether acquisition and canonical interpretation succeeded conclusively.</summary>
    [JsonIgnore]
    public bool IsSuccessful => Status == RelationQueryPhysicalExecutionStatus.Succeeded;
}
