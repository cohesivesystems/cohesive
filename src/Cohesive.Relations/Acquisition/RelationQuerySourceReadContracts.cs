using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Acquisition;

/// <summary>Purpose for which a physical source field is selected.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQuerySourceReadFieldPurpose
{
    /// <summary>The field materializes one exact compiled semantic input.</summary>
    SemanticInput = 0,

    /// <summary>The field is selected only to correlate a physical relationship lookup.</summary>
    Correlation = 1,

    /// <summary>The field both materializes a compiled input and correlates a lookup.</summary>
    SemanticInputAndCorrelation = 2
}

/// <summary>One exact semantic or physical-only field selected from a source.</summary>
public sealed record RelationQuerySourceReadField
{
    /// <summary>Creates a source-read field selection.</summary>
    /// <param name="input">Compiled field-input identity, or <see langword="null"/> for a physical-only correlation field.</param>
    /// <param name="semanticPath">Canonical semantic path represented by the selection.</param>
    /// <param name="sourceSelector">Stable adapter-interpreted source selector.</param>
    /// <param name="purpose">Whether the field supplies semantic evidence, correlation, or both.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sourceSelector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or path is default, a selector is empty, or purpose and input conflict.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="purpose"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQuerySourceReadField(
        RelationQueryInputId? input,
        FieldPath semanticPath,
        string sourceSelector,
        RelationQuerySourceReadFieldPurpose purpose)
    {
        if (input is { } inputId && string.IsNullOrWhiteSpace(inputId.Value))
            throw new ArgumentException("A source-read field input cannot be default.", nameof(input));
        if (semanticPath.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A source-read field requires a semantic path.", nameof(semanticPath));
        if (!Enum.IsDefined(purpose))
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unsupported source-read field purpose.");
        if (purpose == RelationQuerySourceReadFieldPurpose.Correlation && input is not null)
            throw new ArgumentException("A correlation-only field cannot claim a compiled input.", nameof(input));
        if (purpose != RelationQuerySourceReadFieldPurpose.Correlation && input is null)
            throw new ArgumentException("A semantic source-read field requires a compiled input.", nameof(input));

        Input = input;
        SemanticPath = semanticPath;
        SourceSelector = Guard.RequireNotNullOrWhiteSpace(sourceSelector);
        Purpose = purpose;
    }

    /// <summary>Compiled field-input identity, or <see langword="null"/> for a physical-only field.</summary>
    public RelationQueryInputId? Input { get; }

    /// <summary>Canonical semantic path represented by the selection.</summary>
    public FieldPath SemanticPath { get; }

    /// <summary>Stable adapter-interpreted source selector.</summary>
    public string SourceSelector { get; }

    /// <summary>Whether the field supplies semantic evidence, physical correlation, or both.</summary>
    public RelationQuerySourceReadFieldPurpose Purpose { get; }
}

/// <summary>Closed constraint applied to one bounded physical source read.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$read")]
[JsonDerivedType(typeof(RelationQueryBoundedEnumeration), "boundedEnumeration")]
[JsonDerivedType(typeof(RelationQueryIdentityBatchLookup), "identityBatch")]
[JsonDerivedType(typeof(RelationQueryRelationshipKeyBatchLookup), "relationshipKeyBatch")]
public abstract record RelationQuerySourceReadConstraint
{
    /// <summary>Initializes a closed physical source-read constraint.</summary>
    private protected RelationQuerySourceReadConstraint()
    {
    }
}

/// <summary>Enumerates a complete logical source set within an explicit row bound.</summary>
public sealed record RelationQueryBoundedEnumeration : RelationQuerySourceReadConstraint
{
    /// <summary>Creates a bounded source enumeration.</summary>
    /// <param name="maximumRows">Maximum rows the reader may return.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumRows"/> is not positive or portable.</exception>
    [JsonConstructor]
    public RelationQueryBoundedEnumeration(long maximumRows) =>
        MaximumRows = RelationQuerySourcePlacementLimits.RequireLimit(maximumRows, nameof(maximumRows));

    /// <summary>Maximum rows the reader may return.</summary>
    [JsonConverter(typeof(Cohesive.Model.Serialization.StringEncodedInt64JsonConverter))]
    public long MaximumRows { get; }
}

/// <summary>Reads observations whose stable identities belong to one bounded key batch.</summary>
public sealed record RelationQueryIdentityBatchLookup : RelationQuerySourceReadConstraint
{
    /// <summary>Creates an identity-batch lookup.</summary>
    /// <param name="identities">Distinct identities in deterministic ordinal order.</param>
    /// <exception cref="ArgumentException"><paramref name="identities"/> is empty, contains an empty identity, or contains duplicates.</exception>
    [JsonConstructor]
    public RelationQueryIdentityBatchLookup(ImmutableArray<string> identities) =>
        Identities = NormalizeKeys(identities, nameof(identities));

    /// <summary>Distinct identities in deterministic ordinal order.</summary>
    public ImmutableArray<string> Identities { get; }

    internal static ImmutableArray<string> NormalizeKeys(ImmutableArray<string> values, string parameterName)
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException("A batched source lookup requires at least one key.", parameterName);
        if (normalized.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Batched source keys cannot be empty or white space.", parameterName);
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new ArgumentException("Batched source keys cannot be duplicated.", parameterName);
        return [.. normalized.Order(StringComparer.Ordinal)];
    }
}

/// <summary>Reads observations whose relationship-reference field matches one bounded key batch.</summary>
public sealed record RelationQueryRelationshipKeyBatchLookup : RelationQuerySourceReadConstraint
{
    /// <summary>Creates a batched relationship-key lookup.</summary>
    /// <param name="relationshipReference">Canonical reference field used by the predicate.</param>
    /// <param name="sourceSelector">Stable adapter-interpreted selector for the reference field.</param>
    /// <param name="keys">Distinct predicate values in deterministic ordinal order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sourceSelector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The path or selector is invalid, or <paramref name="keys"/> is invalid.</exception>
    [JsonConstructor]
    public RelationQueryRelationshipKeyBatchLookup(
        FieldPath relationshipReference,
        string sourceSelector,
        ImmutableArray<string> keys)
    {
        if (relationshipReference.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A relationship-key lookup requires a semantic reference path.", nameof(relationshipReference));
        RelationshipReference = relationshipReference;
        SourceSelector = Guard.RequireNotNullOrWhiteSpace(sourceSelector);
        Keys = RelationQueryIdentityBatchLookup.NormalizeKeys(keys, nameof(keys));
    }

    /// <summary>Canonical reference field used by the predicate.</summary>
    public FieldPath RelationshipReference { get; }

    /// <summary>Stable adapter-interpreted selector for the reference field.</summary>
    public string SourceSelector { get; }

    /// <summary>Distinct predicate values in deterministic ordinal order.</summary>
    public ImmutableArray<string> Keys { get; }
}

/// <summary>Immutable request issued to one physical source reader.</summary>
public sealed class RelationQuerySourceReadRequest
{
    /// <summary>Creates one exact bounded source read request.</summary>
    /// <param name="physicalPlan">Physical-plan fingerprint authorizing the read.</param>
    /// <param name="stage">Physical stage issuing the read.</param>
    /// <param name="placementBinding">Exact placement binding consumed by the stage.</param>
    /// <param name="source">Physical source instance receiving the request.</param>
    /// <param name="shape">Graph-qualified shape returned by the reader.</param>
    /// <param name="identitySelector">Adapter-interpreted stable identity selector.</param>
    /// <param name="fields">Exact semantic and correlation fields selected by the read.</param>
    /// <param name="constraint">Bounded enumeration or lookup constraint.</param>
    /// <param name="maximumBufferedRows">Maximum rows the executor will buffer from this request.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="physicalPlan"/>, <paramref name="identitySelector"/>, or <paramref name="constraint"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity, selector, shape, or field selection is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumBufferedRows"/> is not positive or portable.</exception>
    public RelationQuerySourceReadRequest(
        RelationQueryPhysicalPlanFingerprint physicalPlan,
        RelationQueryPhysicalStageId stage,
        RelationQuerySourcePlacementBindingId placementBinding,
        RelationQuerySourceInstanceId source,
        QualifiedShapeId shape,
        string identitySelector,
        ImmutableArray<RelationQuerySourceReadField> fields,
        RelationQuerySourceReadConstraint constraint,
        long maximumBufferedRows)
    {
        PhysicalPlan = Guard.RequireNotNull(physicalPlan);
        if (string.IsNullOrWhiteSpace(stage.Value) || string.IsNullOrWhiteSpace(placementBinding.Value)
            || string.IsNullOrWhiteSpace(source.Value))
            throw new ArgumentException("A source read requires complete physical identities.", nameof(stage));
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("A source read requires a graph-qualified shape.", nameof(shape));
        var normalizedFields = fields.IsDefault ? [] : fields;
        if (normalizedFields.Any(static field => field is null))
            throw new ArgumentException("Source-read fields cannot contain null entries.", nameof(fields));
        if (normalizedFields.GroupBy(static field => (field.Input, field.SemanticPath))
            .Any(static group => group.Count() > 1))
            throw new ArgumentException("Source-read fields cannot repeat a compiled input and path.", nameof(fields));

        Stage = stage;
        PlacementBinding = placementBinding;
        Source = source;
        Shape = shape;
        IdentitySelector = Guard.RequireNotNullOrWhiteSpace(identitySelector);
        Fields =
        [
            .. normalizedFields
                .OrderBy(static field => field.Input?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static field => field.SemanticPath.ToString(), StringComparer.Ordinal)
        ];
        Constraint = Guard.RequireNotNull(constraint);
        MaximumBufferedRows = RelationQuerySourcePlacementLimits.RequireLimit(
            maximumBufferedRows,
            nameof(maximumBufferedRows));
    }

    /// <summary>Physical-plan fingerprint authorizing the read.</summary>
    public RelationQueryPhysicalPlanFingerprint PhysicalPlan { get; }

    /// <summary>Physical stage issuing the read.</summary>
    public RelationQueryPhysicalStageId Stage { get; }

    /// <summary>Exact placement binding consumed by the stage.</summary>
    public RelationQuerySourcePlacementBindingId PlacementBinding { get; }

    /// <summary>Physical source instance receiving the request.</summary>
    public RelationQuerySourceInstanceId Source { get; }

    /// <summary>Graph-qualified shape returned by the reader.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Adapter-interpreted stable identity selector.</summary>
    public string IdentitySelector { get; }

    /// <summary>Exact semantic and correlation field selections in deterministic order.</summary>
    public ImmutableArray<RelationQuerySourceReadField> Fields { get; }

    /// <summary>Bounded enumeration or lookup constraint.</summary>
    public RelationQuerySourceReadConstraint Constraint { get; }

    /// <summary>Maximum rows the executor will buffer from this request.</summary>
    [JsonConverter(typeof(Cohesive.Model.Serialization.StringEncodedInt64JsonConverter))]
    public long MaximumBufferedRows { get; }
}

/// <summary>Observed state of one selected field in a physical source row.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQuerySourceReadFieldState
{
    /// <summary>A concrete non-null, non-missing value was read.</summary>
    Value = 0,

    /// <summary>The selected field contained explicit null.</summary>
    Null = 1,

    /// <summary>The selected field was authoritatively absent.</summary>
    Missing = 2,

    /// <summary>Reading or decoding the selected field failed.</summary>
    Failed = 3,

    /// <summary>The reader could not determine the selected field conclusively.</summary>
    Inconclusive = 4
}

/// <summary>Outcome for one exact selected field in a physical source row.</summary>
public sealed record RelationQuerySourceReadFieldResult
{
    /// <summary>Creates a selected-field outcome.</summary>
    /// <param name="field">Exact requested field selection.</param>
    /// <param name="state">Observed field state.</param>
    /// <param name="value">Concrete value for <see cref="RelationQuerySourceReadFieldState.Value"/>.</param>
    /// <param name="evidenceReference">Optional opaque acquisition or failure reference.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Value/state invariants conflict or the evidence reference is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQuerySourceReadFieldResult(
        RelationQuerySourceReadField field,
        RelationQuerySourceReadFieldState state,
        ObservationValue? value = null,
        string? evidenceReference = null)
    {
        Field = Guard.RequireNotNull(field);
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported source-read field state.");
        if (state == RelationQuerySourceReadFieldState.Value
            && (value is not { } concrete || concrete.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined))
            throw new ArgumentException("Value field results require a concrete non-null value.", nameof(value));
        if (state != RelationQuerySourceReadFieldState.Value && value is not null)
            throw new ArgumentException("Only a value field result can carry a value.", nameof(value));
        if (evidenceReference is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        State = state;
        Value = value;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Exact requested field selection.</summary>
    public RelationQuerySourceReadField Field { get; }

    /// <summary>Observed field state.</summary>
    public RelationQuerySourceReadFieldState State { get; }

    /// <summary>Concrete value, or <see langword="null"/> for another state.</summary>
    public ObservationValue? Value { get; }

    /// <summary>Opaque acquisition or failure reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>One identity-bearing shaped row returned by a physical source reader.</summary>
public sealed record RelationQuerySourceReadObservation
{
    /// <summary>Creates a source-read observation.</summary>
    /// <param name="identity">Stable semantic observation identity.</param>
    /// <param name="shape">Graph-qualified semantic shape.</param>
    /// <param name="fields">One outcome for every requested field.</param>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The identity or shape is invalid, or field outcomes conflict.</exception>
    [JsonConstructor]
    public RelationQuerySourceReadObservation(
        string identity,
        QualifiedShapeId shape,
        ImmutableArray<RelationQuerySourceReadFieldResult> fields)
    {
        Identity = Guard.RequireNotNullOrWhiteSpace(identity);
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("A source-read observation requires a graph-qualified shape.", nameof(shape));
        var normalized = fields.IsDefault ? [] : fields;
        if (normalized.Any(static field => field is null))
            throw new ArgumentException("Source-read field results cannot contain null entries.", nameof(fields));
        if (normalized.GroupBy(static field => (field.Field.Input, field.Field.SemanticPath))
            .Any(static group => group.Count() > 1))
            throw new ArgumentException("Source-read field results cannot repeat one selection.", nameof(fields));
        Shape = shape;
        Fields =
        [
            .. normalized
                .OrderBy(static field => field.Field.Input?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static field => field.Field.SemanticPath.ToString(), StringComparer.Ordinal)
        ];
    }

    /// <summary>Stable semantic observation identity.</summary>
    public string Identity { get; }

    /// <summary>Graph-qualified semantic shape.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Selected-field outcomes in deterministic order.</summary>
    public ImmutableArray<RelationQuerySourceReadFieldResult> Fields { get; }
}

/// <summary>Overall outcome of one bounded physical source request.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQuerySourceReadState
{
    /// <summary>The request completed authoritatively, including an empty result.</summary>
    Complete = 0,

    /// <summary>The request returned attributable rows but cannot claim complete results.</summary>
    Partial = 1,

    /// <summary>The request authoritatively proved that no matching observation exists.</summary>
    NotFound = 2,

    /// <summary>The source request failed.</summary>
    Failed = 3,

    /// <summary>
    /// The source could not determine whether a complete result exists and therefore returned no attributable rows.
    /// </summary>
    Inconclusive = 4
}

/// <summary>Immutable result of one bounded physical source request.</summary>
public sealed class RelationQuerySourceReadResult
{
    /// <summary>Creates a physical source-read result.</summary>
    /// <param name="state">Overall read outcome.</param>
    /// <param name="observations">Identity-bearing observations returned by complete or partial reads.</param>
    /// <param name="evidenceReference">Optional opaque acquisition, snapshot, or failure reference.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="observations"/> contains a <see langword="null"/> entry or duplicate identity, observations are
    /// supplied for a state that cannot carry rows, or <paramref name="evidenceReference"/> is empty or white space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQuerySourceReadResult(
        RelationQuerySourceReadState state,
        ImmutableArray<RelationQuerySourceReadObservation> observations = default,
        string? evidenceReference = null)
    {
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported source-read state.");
        var normalized = observations.IsDefault ? [] : observations;
        if (normalized.Any(static observation => observation is null))
            throw new ArgumentException("Source-read observations cannot contain null entries.", nameof(observations));
        if ((state is RelationQuerySourceReadState.NotFound
                or RelationQuerySourceReadState.Failed
                or RelationQuerySourceReadState.Inconclusive)
            && !normalized.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "Not-found, failed, and inconclusive source reads cannot contain observations.",
                nameof(observations));
        }
        if (normalized.GroupBy(static observation => observation.Identity, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1))
            throw new ArgumentException("A source read cannot repeat an observation identity.", nameof(observations));
        if (evidenceReference is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        State = state;
        Observations = [.. normalized.OrderBy(static observation => observation.Identity, StringComparer.Ordinal)];
        EvidenceReference = evidenceReference;
    }

    /// <summary>Overall read outcome.</summary>
    public RelationQuerySourceReadState State { get; }

    /// <summary>Returned observations in deterministic identity order.</summary>
    public ImmutableArray<RelationQuerySourceReadObservation> Observations { get; }

    /// <summary>Opaque acquisition, snapshot, or failure reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }

    /// <summary>Whether absence from this result is authoritative.</summary>
    [JsonIgnore]
    public RelationQueryEvidenceCompleteness Completeness =>
        State is RelationQuerySourceReadState.Complete or RelationQuerySourceReadState.NotFound
            ? RelationQueryEvidenceCompleteness.Complete
            : RelationQueryEvidenceCompleteness.Partial;
}

/// <summary>Exact provider identity exposed by one source reader.</summary>
public sealed record RelationQuerySourceReaderDescriptor
{
    /// <summary>Creates a source-reader descriptor.</summary>
    /// <param name="source">Physical source instance implemented by the reader.</param>
    /// <param name="executionDomain">Execution or consistency domain containing the source.</param>
    /// <param name="targetProfile">Exact capability profile implemented by the reader.</param>
    /// <exception cref="ArgumentException">An identity is default.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="targetProfile"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RelationQuerySourceReaderDescriptor(
        RelationQuerySourceInstanceId source,
        RelationQueryExecutionDomainId executionDomain,
        RelationQueryTargetCapabilityProfile targetProfile)
    {
        if (string.IsNullOrWhiteSpace(source.Value) || string.IsNullOrWhiteSpace(executionDomain.Value))
            throw new ArgumentException("A source-reader descriptor requires complete physical identities.", nameof(source));
        Source = source;
        ExecutionDomain = executionDomain;
        TargetProfile = Guard.RequireNotNull(targetProfile);
    }

    /// <summary>Physical source instance implemented by the reader.</summary>
    public RelationQuerySourceInstanceId Source { get; }

    /// <summary>Execution or consistency domain containing the source.</summary>
    public RelationQueryExecutionDomainId ExecutionDomain { get; }

    /// <summary>Exact capability profile implemented by the reader.</summary>
    public RelationQueryTargetCapabilityProfile TargetProfile { get; }
}

/// <summary>Narrow target-neutral port for bounded source enumeration and batched lookup.</summary>
public interface IRelationQuerySourceReader
{
    /// <summary>Exact source, execution-domain, and capability-profile identity implemented by this reader.</summary>
    RelationQuerySourceReaderDescriptor Descriptor { get; }

    /// <summary>Executes one bounded, exactly projected source request.</summary>
    /// <param name="request">Plan-attributed source request.</param>
    /// <param name="cancellationToken">Token that cancels source I/O and result materialization.</param>
    /// <returns>The complete, partial, not-found, failed, or inconclusive source outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    /// <remarks>Expected provider failures should be returned as evidence-bearing results; cancellation propagates.</remarks>
    ValueTask<RelationQuerySourceReadResult> ReadAsync(
        RelationQuerySourceReadRequest request,
        CancellationToken cancellationToken = default);
}
