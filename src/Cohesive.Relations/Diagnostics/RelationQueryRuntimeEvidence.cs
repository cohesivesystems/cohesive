using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Diagnostics;

/// <summary>Stable identity for one runtime evaluation of a compiled relation or query.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryEvaluationId
{
    /// <summary>Creates an evaluation identifier.</summary>
    /// <param name="value">Non-empty identity assigned by the caller.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryEvaluationId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw evaluation identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Stable identity for one binding occurrence within a runtime evaluation.
/// </summary>
/// <remarks>
/// An occurrence identifies participation in an evaluation, not semantic entity identity. The same
/// observation may therefore have more than one occurrence when it participates through different bindings.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryOccurrenceId
{
    /// <summary>Creates an occurrence identifier.</summary>
    /// <param name="value">Non-empty identity unique within an evaluation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryOccurrenceId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw occurrence identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Whether omitted evidence is unknown or is explicitly absent.</summary>
public enum RelationQueryEvidenceCompleteness
{
    /// <summary>Omitted evidence is unknown and cannot prove a runtime requirement gap.</summary>
    Partial = 0,

    /// <summary>Omitted evidence inside the snapshot boundary is explicitly unavailable.</summary>
    Complete = 1
}

/// <summary>Runtime occurrence of a shaped binding value.</summary>
public sealed record RelationQueryObservationOccurrence
{
    /// <summary>Creates an observation occurrence.</summary>
    /// <param name="id">Identity unique within the containing evaluation.</param>
    /// <param name="binding">Canonical binding in which the observation participates.</param>
    /// <param name="shape">Graph-qualified semantic shape of the observation.</param>
    /// <param name="observationIdentity">Optional stable semantic observation identity.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/>, <paramref name="binding"/>, or <paramref name="shape"/> is default,
    /// or <paramref name="observationIdentity"/> is empty or white space.
    /// </exception>
    public RelationQueryObservationOccurrence(
        RelationQueryOccurrenceId id,
        ValueBindingId binding,
        QualifiedShapeId shape,
        string? observationIdentity = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("An observation occurrence requires an identity.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(binding.Value))
        {
            throw new ArgumentException("An observation occurrence requires a binding.", nameof(binding));
        }

        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
        {
            throw new ArgumentException("An observation occurrence requires a graph-qualified shape.", nameof(shape));
        }

        if (observationIdentity is not null && string.IsNullOrWhiteSpace(observationIdentity))
        {
            throw new ArgumentException("An observation identity cannot be empty or white space.", nameof(observationIdentity));
        }

        Id = id;
        Binding = binding;
        Shape = shape;
        ObservationIdentity = observationIdentity;
    }

    /// <summary>Identity unique within the containing evaluation.</summary>
    public RelationQueryOccurrenceId Id { get; }

    /// <summary>Canonical binding in which the observation participates.</summary>
    public ValueBindingId Binding { get; }

    /// <summary>Graph-qualified semantic shape of the observation.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Stable semantic observation identity, or <see langword="null"/> when unavailable.</summary>
    public string? ObservationIdentity { get; }
}

/// <summary>Observed availability of a compiled source-set input.</summary>
public enum RelationQuerySourceEvidenceState
{
    /// <summary>The source was explicitly not supplied to the evaluation.</summary>
    NotProvided = 0,

    /// <summary>The source was supplied successfully, including when its result set is empty.</summary>
    Provided = 1,

    /// <summary>Acquiring the source failed.</summary>
    Failed = 2
}

/// <summary>Runtime evidence for one compiled source-set input.</summary>
public sealed record RelationQuerySourceEvidence
{
    /// <summary>Creates source evidence.</summary>
    /// <param name="input">Compiled source-set input identity.</param>
    /// <param name="state">Observed source state.</param>
    /// <param name="occurrences">Occurrences supplied by a successful source acquisition.</param>
    /// <param name="evidenceReference">Optional opaque reference to acquisition evidence or failure details.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> is default; <paramref name="occurrences"/> contains a null, duplicate, or invalid
    /// occurrence; occurrences are supplied for a state other than <see cref="RelationQuerySourceEvidenceState.Provided"/>;
    /// or <paramref name="evidenceReference"/> is empty or white space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is unsupported.</exception>
    public RelationQuerySourceEvidence(
        RelationQueryInputId input,
        RelationQuerySourceEvidenceState state,
        ImmutableArray<RelationQueryObservationOccurrence> occurrences = default,
        string? evidenceReference = null)
    {
        RequireInput(input, nameof(input));
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported source evidence state.");
        }

        var normalized = NormalizeOccurrences(occurrences, nameof(occurrences));
        if (state != RelationQuerySourceEvidenceState.Provided && !normalized.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Only provided source evidence can contain occurrences.", nameof(occurrences));
        }

        RequireOptionalReference(evidenceReference, nameof(evidenceReference));

        Input = input;
        State = state;
        Occurrences = normalized;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Compiled source-set input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Observed source state.</summary>
    public RelationQuerySourceEvidenceState State { get; }

    /// <summary>Supplied occurrences, or an empty array for another state or an empty successful source.</summary>
    public ImmutableArray<RelationQueryObservationOccurrence> Occurrences { get; }

    /// <summary>Opaque acquisition evidence or failure reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }

    internal static ImmutableArray<RelationQueryObservationOccurrence> NormalizeOccurrences(
        ImmutableArray<RelationQueryObservationOccurrence> occurrences,
        string parameterName)
    {
        var normalized = occurrences.IsDefault ? [] : occurrences;
        if (normalized.Any(static occurrence => occurrence is null))
        {
            throw new ArgumentException("Observation occurrences cannot contain null entries.", parameterName);
        }

        if (normalized.GroupBy(static occurrence => occurrence.Id).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Observation occurrences cannot repeat an occurrence identity.", parameterName);
        }

        return [.. normalized.OrderBy(static occurrence => occurrence.Id.Value, StringComparer.Ordinal)];
    }

    internal static void RequireInput(RelationQueryInputId input, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
        {
            throw new ArgumentException("Runtime evidence requires a compiled input identity.", parameterName);
        }
    }

    internal static void RequireOptionalReference(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("An evidence reference cannot be empty or white space.", parameterName);
        }
    }
}

/// <summary>Observed state of one compiled field input for one binding occurrence.</summary>
public enum RelationQueryFieldEvidenceState
{
    /// <summary>A non-null, non-undefined value was loaded.</summary>
    Value = 0,

    /// <summary>The field was loaded with an explicit null value.</summary>
    Null = 1,

    /// <summary>The field was loaded and is semantically absent.</summary>
    Missing = 2,

    /// <summary>The field was not selected or loaded.</summary>
    NotLoaded = 3,

    /// <summary>Acquiring the field failed.</summary>
    Failed = 4
}

/// <summary>Runtime evidence for one compiled field input and owner occurrence.</summary>
public sealed record RelationQueryFieldEvidence
{
    /// <summary>Creates field evidence.</summary>
    /// <param name="input">Compiled field input identity.</param>
    /// <param name="owner">Occurrence that owns the field.</param>
    /// <param name="state">Observed field state.</param>
    /// <param name="value">Loaded field value for <see cref="RelationQueryFieldEvidenceState.Value"/>.</param>
    /// <param name="evidenceReference">Optional opaque acquisition evidence or failure reference.</param>
    /// <exception cref="ArgumentException">
    /// An identity is default; <paramref name="value"/> is absent, null, or undefined for
    /// <see cref="RelationQueryFieldEvidenceState.Value"/>; a value is supplied for another state;
    /// or <paramref name="evidenceReference"/> is empty or white space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is unsupported.</exception>
    public RelationQueryFieldEvidence(
        RelationQueryInputId input,
        RelationQueryOccurrenceId owner,
        RelationQueryFieldEvidenceState state,
        ObservationValue? value = null,
        string? evidenceReference = null)
    {
        RelationQuerySourceEvidence.RequireInput(input, nameof(input));
        if (string.IsNullOrWhiteSpace(owner.Value))
        {
            throw new ArgumentException("Field evidence requires an owner occurrence.", nameof(owner));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported field evidence state.");
        }

        if (state == RelationQueryFieldEvidenceState.Value
            && (value is not { } observed
                || observed.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined))
        {
            throw new ArgumentException("Value field evidence requires a non-null, non-undefined value.", nameof(value));
        }
        if (state != RelationQueryFieldEvidenceState.Value && value is not null)
        {
            throw new ArgumentException("Only value field evidence can carry a value.", nameof(value));
        }

        RelationQuerySourceEvidence.RequireOptionalReference(evidenceReference, nameof(evidenceReference));

        Input = input;
        Owner = owner;
        State = state;
        Value = value;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Compiled field input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Occurrence that owns the field.</summary>
    public RelationQueryOccurrenceId Owner { get; }

    /// <summary>Observed field state.</summary>
    public RelationQueryFieldEvidenceState State { get; }

    /// <summary>Loaded non-null value, or <see langword="null"/> for another state.</summary>
    public ObservationValue? Value { get; }

    /// <summary>Opaque acquisition evidence or failure reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Observed state of one semantic relationship traversal.</summary>
public enum RelationQueryTraversalEvidenceState
{
    /// <summary>The source occurrence did not reach or apply to this traversal.</summary>
    NotApplicable = 0,

    /// <summary>Relationship resolution was not attempted.</summary>
    NotAttempted = 1,

    /// <summary>Relationship resolution completed and produced zero or more result occurrences.</summary>
    Completed = 2,

    /// <summary>Relationship resolution failed.</summary>
    Failed = 3,

    /// <summary>Candidate related observations were rejected by an explicit resolver or policy.</summary>
    Rejected = 4
}

/// <summary>Runtime evidence for one traversal input and source occurrence.</summary>
public sealed record RelationQueryTraversalEvidence
{
    /// <summary>Creates traversal evidence.</summary>
    /// <param name="input">Compiled relationship input identity.</param>
    /// <param name="from">Occurrence from which the traversal starts.</param>
    /// <param name="state">Observed traversal state.</param>
    /// <param name="results">Related occurrences produced by a completed traversal.</param>
    /// <param name="completeness">Whether a completed result set is authoritative and complete.</param>
    /// <param name="evidenceReference">Optional opaque attempt, rejection, or failure reference.</param>
    /// <exception cref="ArgumentException">
    /// An identity is default; <paramref name="results"/> contains an invalid occurrence; results are supplied for
    /// a non-completed state; a non-completed state declares complete results; or
    /// <paramref name="evidenceReference"/> is empty or white space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="state"/> or <paramref name="completeness"/> is unsupported.
    /// </exception>
    public RelationQueryTraversalEvidence(
        RelationQueryInputId input,
        RelationQueryOccurrenceId from,
        RelationQueryTraversalEvidenceState state,
        ImmutableArray<RelationQueryObservationOccurrence> results = default,
        RelationQueryEvidenceCompleteness completeness = RelationQueryEvidenceCompleteness.Partial,
        string? evidenceReference = null)
    {
        RelationQuerySourceEvidence.RequireInput(input, nameof(input));
        if (string.IsNullOrWhiteSpace(from.Value))
        {
            throw new ArgumentException("Traversal evidence requires a source occurrence.", nameof(from));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported traversal evidence state.");
        }

        if (!Enum.IsDefined(completeness))
        {
            throw new ArgumentOutOfRangeException(nameof(completeness), completeness, "Unsupported evidence completeness.");
        }

        var normalized = RelationQuerySourceEvidence.NormalizeOccurrences(results, nameof(results));
        if (state != RelationQueryTraversalEvidenceState.Completed && !normalized.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Only completed traversal evidence can contain result occurrences.", nameof(results));
        }

        if (state != RelationQueryTraversalEvidenceState.Completed
            && completeness == RelationQueryEvidenceCompleteness.Complete)
        {
            throw new ArgumentException("Only completed traversal evidence can declare complete results.", nameof(completeness));
        }
        RelationQuerySourceEvidence.RequireOptionalReference(evidenceReference, nameof(evidenceReference));

        Input = input;
        From = from;
        State = state;
        Results = normalized;
        Completeness = completeness;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Compiled relationship input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Occurrence from which the traversal starts.</summary>
    public RelationQueryOccurrenceId From { get; }

    /// <summary>Observed traversal state.</summary>
    public RelationQueryTraversalEvidenceState State { get; }

    /// <summary>Related occurrences produced by a completed traversal.</summary>
    public ImmutableArray<RelationQueryObservationOccurrence> Results { get; }

    /// <summary>Whether a completed result set is authoritative and complete.</summary>
    public RelationQueryEvidenceCompleteness Completeness { get; }

    /// <summary>Opaque attempt, rejection, or failure reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Observed availability of a compiled invocation parameter.</summary>
public enum RelationQueryParameterEvidenceState
{
    /// <summary>The invocation did not supply the parameter.</summary>
    NotProvided = 0,

    /// <summary>The parameter was provided with a concrete non-null, non-missing value.</summary>
    Provided = 1,

    /// <summary>Acquiring or decoding the parameter failed.</summary>
    Failed = 2,

    /// <summary>The parameter was provided with an explicit null value.</summary>
    Null = 3,

    /// <summary>The parameter was provided with a semantic missing or undefined value.</summary>
    Missing = 4
}

/// <summary>Runtime evidence for one compiled invocation parameter.</summary>
public sealed record RelationQueryParameterEvidence
{
    /// <summary>Creates parameter evidence.</summary>
    /// <param name="input">Compiled parameter input identity.</param>
    /// <param name="state">Observed parameter state.</param>
    /// <param name="value">
    /// Concrete value for <see cref="RelationQueryParameterEvidenceState.Provided"/>; otherwise
    /// <see langword="null"/>. Explicit null and missing values are represented by <paramref name="state"/>.
    /// </param>
    /// <param name="evidenceReference">Optional opaque decoding or failure reference.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> is default; <paramref name="value"/> is absent, null, or undefined for a provided
    /// parameter; a value is supplied for another state; or <paramref name="evidenceReference"/> is empty or white
    /// space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is unsupported.</exception>
    public RelationQueryParameterEvidence(
        RelationQueryInputId input,
        RelationQueryParameterEvidenceState state,
        ObservationValue? value = null,
        string? evidenceReference = null)
    {
        RelationQuerySourceEvidence.RequireInput(input, nameof(input));
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported parameter evidence state.");
        }

        if (state == RelationQueryParameterEvidenceState.Provided
            && (value is not { } provided
                || provided.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined))
        {
            throw new ArgumentException(
                "Provided parameter evidence requires a concrete non-null, non-missing value; use the Null or Missing state for those semantic values.",
                nameof(value));
        }

        if (state != RelationQueryParameterEvidenceState.Provided && value is not null)
        {
            throw new ArgumentException("Only provided parameter evidence can carry a value.", nameof(value));
        }

        RelationQuerySourceEvidence.RequireOptionalReference(evidenceReference, nameof(evidenceReference));

        Input = input;
        State = state;
        Value = value;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Compiled parameter input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Observed parameter state.</summary>
    public RelationQueryParameterEvidenceState State { get; }

    /// <summary>
    /// Concrete provided value, or <see langword="null"/> when <see cref="State"/> carries null, missing, absence,
    /// or failure semantics.
    /// </summary>
    public ObservationValue? Value { get; }

    /// <summary>Opaque decoding or failure reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Observed availability of a compiled expression capability.</summary>
public enum RelationQueryCapabilityEvidenceState
{
    /// <summary>The capability is available to the intended evaluator.</summary>
    Available = 0,

    /// <summary>The capability is unavailable to the intended evaluator.</summary>
    Unavailable = 1
}

/// <summary>Runtime evidence for one compiled expression capability.</summary>
public sealed record RelationQueryCapabilityEvidence
{
    /// <summary>Creates capability evidence.</summary>
    /// <param name="input">Compiled capability input identity.</param>
    /// <param name="state">Observed capability state.</param>
    /// <param name="evidenceReference">Optional opaque capability-probe reference.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> is default or <paramref name="evidenceReference"/> is empty or white space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is unsupported.</exception>
    public RelationQueryCapabilityEvidence(
        RelationQueryInputId input,
        RelationQueryCapabilityEvidenceState state,
        string? evidenceReference = null)
    {
        RelationQuerySourceEvidence.RequireInput(input, nameof(input));
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported capability evidence state.");
        }

        RelationQuerySourceEvidence.RequireOptionalReference(evidenceReference, nameof(evidenceReference));
        Input = input;
        State = state;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Compiled capability input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Observed capability state.</summary>
    public RelationQueryCapabilityEvidenceState State { get; }

    /// <summary>Opaque capability-probe reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Explicit conversion failure reported by a later adapter or evaluator.</summary>
public sealed record RelationQueryConversionFailureEvidence
{
    /// <summary>Creates conversion-failure evidence.</summary>
    /// <param name="input">Compiled input whose value could not be converted.</param>
    /// <param name="occurrence">Affected binding occurrence, or <see langword="null"/> for invocation-wide input.</param>
    /// <param name="evidenceReference">Opaque reference to structured conversion failure details.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> is default, <paramref name="occurrence"/> is default, or
    /// <paramref name="evidenceReference"/> is empty or white space.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="evidenceReference"/> is <see langword="null"/>.</exception>
    public RelationQueryConversionFailureEvidence(
        RelationQueryInputId input,
        RelationQueryOccurrenceId? occurrence,
        string evidenceReference)
    {
        RelationQuerySourceEvidence.RequireInput(input, nameof(input));
        if (occurrence is { } scoped && string.IsNullOrWhiteSpace(scoped.Value))
        {
            throw new ArgumentException("A conversion-failure occurrence cannot be default.", nameof(occurrence));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        Input = input;
        Occurrence = occurrence;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Compiled input whose value could not be converted.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Affected binding occurrence, or <see langword="null"/> for invocation-wide input.</summary>
    public RelationQueryOccurrenceId? Occurrence { get; }

    /// <summary>Opaque reference to structured conversion failure details.</summary>
    public string EvidenceReference { get; }
}

/// <summary>
/// Immutable runtime evidence snapshot for one evaluation of a compiled relation or query.
/// </summary>
public sealed class RelationQueryRuntimeEvidence
{
    /// <summary>Creates a runtime evidence snapshot.</summary>
    /// <param name="evaluation">Identity of the evaluation represented by the snapshot.</param>
    /// <param name="plan">Compiled plan whose exact input contract the evidence describes.</param>
    /// <param name="completeness">Whether omitted evidence inside this snapshot is explicitly unavailable.</param>
    /// <param name="sources">Source-set evidence.</param>
    /// <param name="fields">Field evidence scoped to observation occurrences.</param>
    /// <param name="traversals">Relationship traversal evidence scoped to source occurrences.</param>
    /// <param name="parameters">Invocation-parameter evidence.</param>
    /// <param name="capabilities">Expression-capability evidence.</param>
    /// <param name="conversionFailures">Explicit conversion failures reported by later interpreters.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="evaluation"/> is default, or an evidence array
    /// contains a <see langword="null"/> entry. Duplicate and cross-record conflicts are retained for analyzer diagnostics.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="completeness"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">
    /// A shape snapshot cannot be represented by the compiled-plan canonicalization profile.
    /// </exception>
    /// <exception cref="JsonException">A shape snapshot cannot be serialized as canonical JSON.</exception>
    /// <exception cref="NotSupportedException">
    /// A shape snapshot contains a runtime type unsupported by its JSON serializer.
    /// </exception>
    public RelationQueryRuntimeEvidence(
        RelationQueryEvaluationId evaluation,
        CompiledRelationQueryPlan plan,
        RelationQueryEvidenceCompleteness completeness = RelationQueryEvidenceCompleteness.Complete,
        ImmutableArray<RelationQuerySourceEvidence> sources = default,
        ImmutableArray<RelationQueryFieldEvidence> fields = default,
        ImmutableArray<RelationQueryTraversalEvidence> traversals = default,
        ImmutableArray<RelationQueryParameterEvidence> parameters = default,
        ImmutableArray<RelationQueryCapabilityEvidence> capabilities = default,
        ImmutableArray<RelationQueryConversionFailureEvidence> conversionFailures = default)
    {
        if (string.IsNullOrWhiteSpace(evaluation.Value))
        {
            throw new ArgumentException("Runtime evidence requires an evaluation identity.", nameof(evaluation));
        }

        ArgumentNullException.ThrowIfNull(plan);

        if (!Enum.IsDefined(completeness))
        {
            throw new ArgumentOutOfRangeException(nameof(completeness), completeness, "Unsupported evidence completeness.");
        }

        Evaluation = evaluation;
        PlanReference = RelationQueryCompiledPlanReference.From(plan);
        Completeness = completeness;
        Sources = Normalize(sources, static evidence => evidence.Input.Value, nameof(sources));
        Fields = Normalize(
            fields,
            static evidence => string.Concat(evidence.Input.Value, "\u001f", evidence.Owner.Value),
            nameof(fields));
        Traversals = Normalize(
            traversals,
            static evidence => string.Concat(evidence.Input.Value, "\u001f", evidence.From.Value),
            nameof(traversals));
        Parameters = Normalize(parameters, static evidence => evidence.Input.Value, nameof(parameters));
        Capabilities = Normalize(capabilities, static evidence => evidence.Input.Value, nameof(capabilities));
        ConversionFailures = Normalize(
            conversionFailures,
            static evidence => string.Concat(
                evidence.Input.Value,
                "\u001f",
                evidence.Occurrence?.Value ?? string.Empty,
                "\u001f",
                evidence.EvidenceReference),
            nameof(conversionFailures));
    }

    /// <summary>Identity of the evaluation represented by this snapshot.</summary>
    public RelationQueryEvaluationId Evaluation { get; }

    /// <summary>Exact compiled input-contract attribution for this evidence.</summary>
    public RelationQueryCompiledPlanReference PlanReference { get; }

    /// <summary>Definition fingerprint to which evidence input identities belong.</summary>
    public RelationQueryDefinitionFingerprint DefinitionFingerprint => PlanReference.DefinitionFingerprint;

    /// <summary>Whether omitted evidence inside this snapshot is explicitly unavailable.</summary>
    public RelationQueryEvidenceCompleteness Completeness { get; }

    /// <summary>Source-set evidence in deterministic key order.</summary>
    public ImmutableArray<RelationQuerySourceEvidence> Sources { get; }

    /// <summary>Field evidence in deterministic input/occurrence order.</summary>
    public ImmutableArray<RelationQueryFieldEvidence> Fields { get; }

    /// <summary>Traversal evidence in deterministic input/source-occurrence order.</summary>
    public ImmutableArray<RelationQueryTraversalEvidence> Traversals { get; }

    /// <summary>Parameter evidence in deterministic input order.</summary>
    public ImmutableArray<RelationQueryParameterEvidence> Parameters { get; }

    /// <summary>Capability evidence in deterministic input order.</summary>
    public ImmutableArray<RelationQueryCapabilityEvidence> Capabilities { get; }

    /// <summary>Explicit conversion failures in deterministic input/occurrence/reference order.</summary>
    public ImmutableArray<RelationQueryConversionFailureEvidence> ConversionFailures { get; }

    static ImmutableArray<T> Normalize<T>(
        ImmutableArray<T> values,
        Func<T, string> key,
        string parameterName)
        where T : class
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(static value => value is null))
        {
            throw new ArgumentException("Runtime evidence arrays cannot contain null entries.", parameterName);
        }

        return [.. normalized.OrderBy(key, StringComparer.Ordinal)];
    }
}
