using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Execution;

/// <summary>Materialized availability state for one field or invocation parameter.</summary>
enum RelationQueryMaterializedValueState
{
    /// <summary>No evidence record exists in the runtime snapshot.</summary>
    Omitted = 0,

    /// <summary>A concrete non-null, non-missing value was supplied.</summary>
    Value = 1,

    /// <summary>An explicit semantic null was supplied.</summary>
    Null = 2,

    /// <summary>The value is explicitly semantically absent.</summary>
    Missing = 3,

    /// <summary>A field was explicitly not selected or loaded.</summary>
    NotLoaded = 4,

    /// <summary>An invocation parameter was explicitly not supplied.</summary>
    NotProvided = 5,

    /// <summary>Acquiring or decoding the value failed.</summary>
    Failed = 6,

    /// <summary>The canonical parameter declaration supplied its persisted default value.</summary>
    Defaulted = 7,

    /// <summary>Acquisition could not establish a semantic value, absence, or definitive failure.</summary>
    Inconclusive = 8
}

/// <summary>
/// Lossless runtime availability result that retains evidence state separately from any semantic value.
/// </summary>
readonly record struct RelationQueryMaterializedValue
{
    RelationQueryMaterializedValue(
        RelationQueryMaterializedValueState state,
        ObservationValue? value,
        string? evidenceReference)
    {
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported materialized-value state.");
        if (state == RelationQueryMaterializedValueState.Value
            && (value is not { } concrete
                || concrete.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined))
        {
            throw new ArgumentException(
                "A concrete materialized value cannot be null or undefined.",
                nameof(value));
        }
        if (state == RelationQueryMaterializedValueState.Defaulted
            && (value is not { } canonicalDefault
                || canonicalDefault.Kind == ObservationValueKind.Undefined))
        {
            throw new ArgumentException(
                "A defaulted value requires a non-undefined canonical default.",
                nameof(value));
        }
        if (state is not (RelationQueryMaterializedValueState.Value
            or RelationQueryMaterializedValueState.Defaulted)
            && value is not null)
        {
            throw new ArgumentException(
                "Only concrete or defaulted materialized values can carry a payload.",
                nameof(value));
        }
        if (evidenceReference is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);

        State = state;
        Value = value;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Exact evidence or default-resolution state.</summary>
    public RelationQueryMaterializedValueState State { get; }

    /// <summary>
    /// Concrete or canonical default payload, or <see langword="null"/> when the state itself represents
    /// null, missing, unavailability, or failure.
    /// </summary>
    public ObservationValue? Value { get; }

    /// <summary>Opaque source-evidence reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }

    /// <summary>
    /// Tries to project evidence into an expression value. Explicit null and missing remain distinct;
    /// unavailable, failed, and omitted evidence do not produce a value.
    /// </summary>
    public bool TryGetSemanticValue(out ObservationValue value)
    {
        switch (State)
        {
            case RelationQueryMaterializedValueState.Value:
            case RelationQueryMaterializedValueState.Defaulted:
                value = Value!.Value;
                return true;
            case RelationQueryMaterializedValueState.Null:
                value = ObservationValue.Null;
                return true;
            case RelationQueryMaterializedValueState.Missing:
                value = ObservationValue.Undefined;
                return true;
            case RelationQueryMaterializedValueState.NotLoaded:
            case RelationQueryMaterializedValueState.NotProvided:
            case RelationQueryMaterializedValueState.Failed:
            case RelationQueryMaterializedValueState.Inconclusive:
            case RelationQueryMaterializedValueState.Omitted:
                value = default;
                return false;
            default:
                throw new ArgumentOutOfRangeException(nameof(State), State, "Unsupported materialized-value state.");
        }
    }

    internal static RelationQueryMaterializedValue FromField(RelationQueryFieldEvidence? evidence) =>
        evidence?.State switch
        {
            null => new(RelationQueryMaterializedValueState.Omitted, value: null, evidenceReference: null),
            RelationQueryFieldEvidenceState.Value => new(
                RelationQueryMaterializedValueState.Value,
                evidence.Value,
                evidence.EvidenceReference),
            RelationQueryFieldEvidenceState.Null => new(
                RelationQueryMaterializedValueState.Null,
                value: null,
                evidence.EvidenceReference),
            RelationQueryFieldEvidenceState.Missing => new(
                RelationQueryMaterializedValueState.Missing,
                value: null,
                evidence.EvidenceReference),
            RelationQueryFieldEvidenceState.NotLoaded => new(
                RelationQueryMaterializedValueState.NotLoaded,
                value: null,
                evidence.EvidenceReference),
            RelationQueryFieldEvidenceState.Failed => new(
                RelationQueryMaterializedValueState.Failed,
                value: null,
                evidence.EvidenceReference),
            RelationQueryFieldEvidenceState.Inconclusive => new(
                RelationQueryMaterializedValueState.Inconclusive,
                value: null,
                evidence.EvidenceReference),
            _ => throw new ArgumentOutOfRangeException(
                nameof(evidence),
                evidence.State,
                "Unsupported field-evidence state.")
        };

    internal static RelationQueryMaterializedValue FromParameter(RelationQueryParameterEvidence? evidence) =>
        evidence?.State switch
        {
            null => new(RelationQueryMaterializedValueState.Omitted, value: null, evidenceReference: null),
            RelationQueryParameterEvidenceState.Provided => new(
                RelationQueryMaterializedValueState.Value,
                evidence.Value,
                evidence.EvidenceReference),
            RelationQueryParameterEvidenceState.Null => new(
                RelationQueryMaterializedValueState.Null,
                value: null,
                evidence.EvidenceReference),
            RelationQueryParameterEvidenceState.Missing => new(
                RelationQueryMaterializedValueState.Missing,
                value: null,
                evidence.EvidenceReference),
            RelationQueryParameterEvidenceState.NotProvided => new(
                RelationQueryMaterializedValueState.NotProvided,
                value: null,
                evidence.EvidenceReference),
            RelationQueryParameterEvidenceState.Failed => new(
                RelationQueryMaterializedValueState.Failed,
                value: null,
                evidence.EvidenceReference),
            _ => throw new ArgumentOutOfRangeException(
                nameof(evidence),
                evidence.State,
                "Unsupported parameter-evidence state.")
        };

    internal static RelationQueryMaterializedValue FromDefault(ObservationValue value) =>
        new(RelationQueryMaterializedValueState.Defaulted, value, evidenceReference: null);
}

/// <summary>
/// Validated-plan-oriented index over one materialized runtime evidence snapshot. The index never resolves
/// relationships or invents missing values; it only reconstructs the exact evidence supplied for compiled inputs.
/// </summary>
sealed class RelationQueryEvidenceIndex
{
    readonly RelationQueryRuntimeEvidence evidence;
    readonly IReadOnlyDictionary<RelationQueryInputId, RelationQueryRequirementInput> inputs;
    readonly IReadOnlyDictionary<RelationQueryInputId, RelationQuerySourceEvidence> sources;
    readonly IReadOnlyDictionary<(RelationQueryInputId Input, RelationQueryOccurrenceId Owner), RelationQueryFieldEvidence> fields;
    readonly IReadOnlyDictionary<(RelationQueryInputId Input, RelationQueryOccurrenceId From), RelationQueryTraversalEvidence> traversals;
    readonly IReadOnlyDictionary<RelationQueryInputId, RelationQueryParameterEvidence> parameters;
    readonly IReadOnlyDictionary<QueryParameterId, RelationQueryParameterInput> parameterInputs;
    readonly IReadOnlyDictionary<(ValueBindingId Binding, QualifiedShapeId Shape), ImmutableArray<RelationQueryFieldInput>> bindingFields;
    readonly IReadOnlyDictionary<RelationQueryOccurrenceId, RelationQueryObservationOccurrence> occurrences;

    /// <summary>Builds an index for runtime evidence already validated against the compiled plan.</summary>
    public RelationQueryEvidenceIndex(
        CompiledRelationQueryPlan plan,
        RelationQueryRuntimeEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(plan);
        this.evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));

        inputs = plan.RequirementGraph.Inputs.ToDictionary(static input => input.Id);
        sources = IndexUnique(
            evidence.Sources,
            static item => item.Input,
            "source evidence");
        fields = IndexUnique(
            evidence.Fields,
            static item => (item.Input, item.Owner),
            "field evidence");
        traversals = IndexUnique(
            evidence.Traversals,
            static item => (item.Input, item.From),
            "traversal evidence");
        parameters = IndexUnique(
            evidence.Parameters,
            static item => item.Input,
            "parameter evidence");
        parameterInputs = plan.RequirementGraph.Inputs
            .OfType<RelationQueryParameterInput>()
            .ToDictionary(static input => input.Parameter);
        bindingFields = plan.RequirementGraph.Inputs
            .OfType<RelationQueryFieldInput>()
            .GroupBy(static input => (input.Binding, input.Field.Shape))
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static input => input.Id.Value, StringComparer.Ordinal)
                    .ToImmutableArray());

        Dictionary<RelationQueryOccurrenceId, RelationQueryObservationOccurrence> occurrenceIndex = [];
        foreach (var occurrence in evidence.Sources.SelectMany(static source => source.Occurrences)
                     .Concat(evidence.Traversals.SelectMany(static traversal => traversal.Results))
                     .Concat(evidence.CollectionOccurrences.Select(static item => item.Occurrence)))
        {
            if (!occurrenceIndex.TryAdd(occurrence.Id, occurrence))
            {
                throw new InvalidOperationException(
                    $"Runtime evidence contains duplicate occurrence '{occurrence.Id.Value}'; analyze evidence before indexing it.");
            }
        }

        occurrences = new ReadOnlyDictionary<RelationQueryOccurrenceId, RelationQueryObservationOccurrence>(
            occurrenceIndex);
    }

    /// <summary>Whether omitted records in the indexed snapshot are explicitly unavailable.</summary>
    public RelationQueryEvidenceCompleteness Completeness => evidence.Completeness;

    /// <summary>Compiled requirement inputs indexed by stable input identity.</summary>
    public IReadOnlyDictionary<RelationQueryInputId, RelationQueryRequirementInput> Inputs => inputs;

    /// <summary>All declared source and traversal-result occurrences indexed by occurrence identity.</summary>
    public IReadOnlyDictionary<RelationQueryOccurrenceId, RelationQueryObservationOccurrence> Occurrences => occurrences;

    /// <summary>Tries to get exact source evidence for a compiled source-set input.</summary>
    public bool TryGetSource(
        RelationQuerySourceSetInput input,
        out RelationQuerySourceEvidence source)
    {
        RequirePlanInput(input);
        return sources.TryGetValue(input.Id, out source!);
    }

    /// <summary>
    /// Tries to reconstruct source rows. A successfully provided empty source returns <see langword="true"/>
    /// with an empty row array; omitted, unavailable, or failed sources return <see langword="false"/>.
    /// </summary>
    public bool TryCreateSourceRows(
        RelationQuerySourceSetInput input,
        out ImmutableArray<RelationQueryRuntimeRow> rows)
    {
        RequirePlanInput(input);
        if (!sources.TryGetValue(input.Id, out var source)
            || source.State != RelationQuerySourceEvidenceState.Provided)
        {
            rows = [];
            return false;
        }

        ImmutableArray<RelationQueryRuntimeRow>.Builder builder =
            ImmutableArray.CreateBuilder<RelationQueryRuntimeRow>(source.Occurrences.Length);
        foreach (var occurrence in source.Occurrences)
        {
            if (occurrence.Binding != input.Binding || occurrence.Shape != input.Shape)
            {
                throw new InvalidOperationException(
                    $"Source occurrence '{occurrence.Id.Value}' does not match compiled source input '{input.Id.Value}'.");
            }

            var binding = CreateObservedBinding(occurrence);
            builder.Add(RelationQueryRuntimeRow.FromBinding(
                input.Binding,
                binding,
                input.Role == RelationQuerySourceInputRole.RelationRoot ? occurrence : null));
        }

        rows = builder.MoveToImmutable();
        return true;
    }

    /// <summary>Tries to get exact traversal evidence for one compiled relationship input and source occurrence.</summary>
    public bool TryGetTraversal(
        RelationQueryRelationshipInput input,
        RelationQueryObservationOccurrence from,
        out RelationQueryTraversalEvidence traversal)
    {
        ArgumentNullException.ThrowIfNull(from);
        RequirePlanInput(input);
        if (from.Binding != input.From || from.Shape != input.FromShape)
        {
            throw new ArgumentException(
                "The traversal source occurrence does not match the compiled relationship input.",
                nameof(from));
        }

        RequireOccurrence(from);

        return traversals.TryGetValue((input.Id, from.Id), out traversal!);
    }

    /// <summary>Tries to get exact traversal evidence by source-occurrence identity.</summary>
    public bool TryGetTraversal(
        RelationQueryRelationshipInput input,
        RelationQueryOccurrenceId from,
        out RelationQueryTraversalEvidence traversal)
    {
        RequirePlanInput(input);
        if (!TryGetOccurrence(from, out var occurrence))
        {
            traversal = null!;
            return false;
        }

        return TryGetTraversal(input, occurrence, out traversal);
    }

    /// <summary>Tries to get one exact declared occurrence.</summary>
    public bool TryGetOccurrence(
        RelationQueryOccurrenceId id,
        out RelationQueryObservationOccurrence occurrence)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An occurrence lookup requires a non-empty identity.", nameof(id));
        return occurrences.TryGetValue(id, out occurrence!);
    }

    /// <summary>Resolves exact field evidence for one compiled field input and owner occurrence.</summary>
    public RelationQueryMaterializedValue ResolveField(
        RelationQueryFieldInput input,
        RelationQueryObservationOccurrence owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        RequirePlanInput(input);
        RequireOccurrence(owner);
        if (owner.Binding != input.Binding || owner.Shape != input.Field.Shape)
        {
            throw new ArgumentException(
                "The field owner occurrence does not match the compiled field input.",
                nameof(owner));
        }

        fields.TryGetValue((input.Id, owner.Id), out var field);
        return RelationQueryMaterializedValue.FromField(field);
    }

    /// <summary>
    /// Reconstructs the sparse object value of one observed binding from its compiled field inputs. Explicit
    /// null is assigned; missing, unloaded, failed, and omitted fields remain absent from the sparse object.
    /// </summary>
    public RelationQueryRuntimeBinding CreateObservedBinding(
        RelationQueryObservationOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        RequireOccurrence(occurrence);

        var value = RelationQueryObjectValues.Empty;
        if (bindingFields.TryGetValue((occurrence.Binding, occurrence.Shape), out var compiledFields))
        {
            foreach (var input in compiledFields)
            {
                var materialized = ResolveField(input, occurrence);
                if (materialized.State == RelationQueryMaterializedValueState.Missing
                    || !materialized.TryGetSemanticValue(out var fieldValue))
                {
                    continue;
                }

                if (input.Field.Path.Segments.Any(static segment => segment.Kind == SegmentKind.Element))
                {
                    throw new RelationQueryExpressionEvaluationException(
                        RelationQueryExpressionEvaluationError.UnsupportedFieldPath,
                        $"Field input '{input.Id.Value}' uses collection-element path '{input.Field.Path}', "
                        + "which cannot be reconstructed losslessly from one occurrence-scoped field evidence value.");
                }

                value = RelationQueryObjectValues.Set(value, input.Field.Path, fieldValue);
            }
        }

        return RelationQueryRuntimeBinding.FromObservation(occurrence, value);
    }

    /// <summary>Resolves raw invocation evidence for a compiled query parameter.</summary>
    public RelationQueryMaterializedValue ResolveParameter(QueryParameterId parameter)
    {
        var input = RequireParameter(parameter);
        parameters.TryGetValue(input.Id, out var observed);
        return RelationQueryMaterializedValue.FromParameter(observed);
    }

    /// <summary>Resolves raw invocation evidence for a compiled query parameter name.</summary>
    public RelationQueryMaterializedValue ResolveParameter(string parameter) =>
        ResolveParameter(new QueryParameterId(parameter));

    /// <summary>
    /// Resolves an effective invocation value, applying a persisted canonical default only when the invocation
    /// explicitly omitted the parameter or a complete evidence snapshot omitted its record.
    /// </summary>
    public RelationQueryMaterializedValue ResolveEffectiveParameter(QueryParameterId parameter)
    {
        var input = RequireParameter(parameter);
        var resolved = ResolveParameter(parameter);
        var mayDefault = resolved.State == RelationQueryMaterializedValueState.NotProvided
            || (resolved.State == RelationQueryMaterializedValueState.Omitted
                && evidence.Completeness == RelationQueryEvidenceCompleteness.Complete);
        return mayDefault && input.Definition.DefaultKind == QueryParameterDefaultKind.Value
            ? RelationQueryMaterializedValue.FromDefault(
                input.Definition.DefaultValue ?? ObservationValue.Null)
            : resolved;
    }

    /// <summary>Resolves an effective invocation value for a compiled query parameter name.</summary>
    public RelationQueryMaterializedValue ResolveEffectiveParameter(string parameter) =>
        ResolveEffectiveParameter(new QueryParameterId(parameter));

    /// <summary>
    /// Creates the expression evaluator's effective parameter bag. Entries without a semantic value remain
    /// omitted so the evaluator observes them as undefined while gap analysis retains their exact cause.
    /// </summary>
    public IReadOnlyDictionary<string, ObservationValue> CreateEffectiveParameterValues()
    {
        Dictionary<string, ObservationValue> values = new(StringComparer.Ordinal);
        foreach (var input in parameterInputs.Values.OrderBy(
                     static input => input.Parameter.Value,
                     StringComparer.Ordinal))
        {
            var resolved = ResolveEffectiveParameter(input.Parameter);
            if (resolved.TryGetSemanticValue(out var value))
                values.Add(input.Parameter.Value, value);
        }

        return new ReadOnlyDictionary<string, ObservationValue>(values);
    }

    TInput RequirePlanInput<TInput>(TInput input)
        where TInput : RelationQueryRequirementInput
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!inputs.TryGetValue(input.Id, out var found)
            || found is not TInput typed
            || !Equals(typed, input))
        {
            throw new ArgumentException(
                $"Input '{input.Id.Value}' does not belong to the indexed compiled plan.",
                nameof(input));
        }

        return typed;
    }

    RelationQueryParameterInput RequireParameter(QueryParameterId parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter.Value))
            throw new ArgumentException("A parameter lookup requires a non-empty identity.", nameof(parameter));
        return parameterInputs.TryGetValue(parameter, out var input)
            ? input
            : throw new ArgumentException(
                $"Parameter '{parameter.Value}' is not required by the indexed compiled plan.",
                nameof(parameter));
    }

    void RequireOccurrence(RelationQueryObservationOccurrence occurrence)
    {
        if (!occurrences.TryGetValue(occurrence.Id, out var indexed)
            || !Equals(indexed, occurrence))
        {
            throw new ArgumentException(
                $"Occurrence '{occurrence.Id.Value}' does not belong to the indexed runtime evidence.",
                nameof(occurrence));
        }
    }

    static IReadOnlyDictionary<TKey, TValue> IndexUnique<TKey, TValue>(
        IEnumerable<TValue> values,
        Func<TValue, TKey> keySelector,
        string description)
        where TKey : notnull
        where TValue : class
    {
        Dictionary<TKey, TValue> result = [];
        foreach (var value in values)
        {
            var key = keySelector(value);
            if (!result.TryAdd(key, value))
            {
                throw new InvalidOperationException(
                    $"Runtime evidence contains duplicate {description}; analyze evidence before indexing it.");
            }
        }

        return new ReadOnlyDictionary<TKey, TValue>(result);
    }
}
