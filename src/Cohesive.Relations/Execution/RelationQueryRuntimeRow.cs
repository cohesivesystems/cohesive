using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Execution;

/// <summary>
/// Distinguishes a binding reconstructed from runtime evidence from one computed by a logical node and
/// from a binding introduced as the absent side of an outer operation.
/// </summary>
enum RelationQueryRuntimeBindingKind
{
    /// <summary>The binding was reconstructed from one exact observation occurrence.</summary>
    Observed = 0,

    /// <summary>The binding was computed by a logical projection, aggregation, or collection operation.</summary>
    Computed = 1,

    /// <summary>The binding is semantically absent and is distinct from a present binding whose value is null.</summary>
    Absent = 2
}

/// <summary>One observed or computed binding carried by an in-memory relation/query row.</summary>
sealed record RelationQueryRuntimeBinding
{
    RelationQueryRuntimeBinding(
        RelationQueryRuntimeBindingKind kind,
        QualifiedShapeId? shape,
        ObservationValue value,
        RelationQueryObservationOccurrence? occurrence,
        ImmutableArray<FieldPath> unavailableFields,
        ImmutableArray<FieldPath> authoritativeFields,
        bool isAuthoritativeValue)
    {
        if (shape is { } qualifiedShape
            && (string.IsNullOrWhiteSpace(qualifiedShape.GraphId.Value)
                || string.IsNullOrWhiteSpace(qualifiedShape.ShapeId.Value)))
        {
            throw new ArgumentException("A runtime binding requires a graph-qualified shape.", nameof(shape));
        }
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported runtime-binding kind.");
        if ((kind == RelationQueryRuntimeBindingKind.Observed) != (occurrence is not null))
        {
            throw new ArgumentException(
                "Only an observed runtime binding can carry an observation occurrence.",
                nameof(occurrence));
        }
        if (kind == RelationQueryRuntimeBindingKind.Observed
            && (shape is null || occurrence!.Shape != shape.Value))
        {
            throw new ArgumentException(
                "An observed runtime binding must use the occurrence shape.",
                nameof(shape));
        }
        if (kind == RelationQueryRuntimeBindingKind.Absent
            && value.Kind != ObservationValueKind.Undefined)
        {
            throw new ArgumentException(
                "An absent runtime binding must carry the undefined sentinel.",
                nameof(value));
        }
        var normalizedUnavailableFields = unavailableFields.IsDefault ? [] : unavailableFields;
        if (kind != RelationQueryRuntimeBindingKind.Computed
            && !normalizedUnavailableFields.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "Only computed bindings can carry unavailable derived fields.",
                nameof(unavailableFields));
        }
        var normalizedAuthoritativeFields = authoritativeFields.IsDefault ? [] : authoritativeFields;
        if (kind == RelationQueryRuntimeBindingKind.Absent
            && (!normalizedAuthoritativeFields.IsDefaultOrEmpty || isAuthoritativeValue))
        {
            throw new ArgumentException(
                "An absent binding cannot carry authoritative value overrides.",
                nameof(authoritativeFields));
        }

        Kind = kind;
        Shape = shape;
        Value = value;
        Occurrence = occurrence;
        UnavailableFields =
        [
            .. normalizedUnavailableFields
                .Distinct()
                .OrderBy(static path => path.ToString(), StringComparer.Ordinal)
        ];
        AuthoritativeFields =
        [
            .. normalizedAuthoritativeFields
                .Distinct()
                .OrderBy(static path => path.ToString(), StringComparer.Ordinal)
        ];
        IsAuthoritativeValue = isAuthoritativeValue;
    }

    /// <summary>Creates a binding reconstructed from an observation occurrence.</summary>
    public static RelationQueryRuntimeBinding FromObservation(
        RelationQueryObservationOccurrence occurrence,
        ObservationValue value,
        ImmutableArray<FieldPath> authoritativeFields = default,
        bool isAuthoritativeValue = false)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return new(
            RelationQueryRuntimeBindingKind.Observed,
            occurrence.Shape,
            value,
            occurrence,
            unavailableFields: [],
            authoritativeFields,
            isAuthoritativeValue);
    }

    /// <summary>Creates a shaped or intentionally unshaped binding computed by a logical node.</summary>
    public static RelationQueryRuntimeBinding FromComputed(
        QualifiedShapeId? shape,
        ObservationValue value,
        ImmutableArray<FieldPath> unavailableFields = default,
        ImmutableArray<FieldPath> authoritativeFields = default,
        bool isAuthoritativeValue = false) =>
        new(
            RelationQueryRuntimeBindingKind.Computed,
            shape,
            value,
            occurrence: null,
            unavailableFields,
            authoritativeFields,
            isAuthoritativeValue);

    /// <summary>Creates a binding representing an absent outer-operation side.</summary>
    public static RelationQueryRuntimeBinding CreateAbsent(QualifiedShapeId? shape) =>
        new(
            RelationQueryRuntimeBindingKind.Absent,
            shape,
            ObservationValue.Undefined,
            occurrence: null,
            unavailableFields: [],
            authoritativeFields: [],
            isAuthoritativeValue: false);

    /// <summary>Origin of the binding.</summary>
    public RelationQueryRuntimeBindingKind Kind { get; }

    /// <summary>
    /// Canonical shape associated with the binding, or <see langword="null"/> for an intentionally unshaped
    /// computed or absent binding such as a scalar collection element.
    /// </summary>
    public QualifiedShapeId? Shape { get; }

    /// <summary>
    /// Materialized or computed value. An absent binding carries <see cref="ObservationValue.Undefined"/>,
    /// while <see cref="ObservationValue.Null"/> remains a distinct present semantic value.
    /// </summary>
    public ObservationValue Value { get; }

    /// <summary>Exact source occurrence for an observed binding, or <see langword="null"/>.</summary>
    public RelationQueryObservationOccurrence? Occurrence { get; }

    /// <summary>
    /// Derived field paths whose values could not be computed from available evidence.
    /// </summary>
    public ImmutableArray<FieldPath> UnavailableFields { get; }

    /// <summary>Policy-substituted field paths that override unavailable raw or derived evidence.</summary>
    public ImmutableArray<FieldPath> AuthoritativeFields { get; }

    /// <summary>Whether the complete binding value was supplied authoritatively by policy.</summary>
    public bool IsAuthoritativeValue { get; }

    /// <summary>Stable observation identity for an observed binding, or <see langword="null"/>.</summary>
    public string? ObservationIdentity => Occurrence?.ObservationIdentity;

    /// <summary>Projects this runtime binding into the expression evaluator's binding contract.</summary>
    public RelationQueryExpressionBinding ToExpressionBinding() =>
        Kind == RelationQueryRuntimeBindingKind.Absent
            ? RelationQueryExpressionBinding.Absent
            : new(Value, Occurrence?.Id, ObservationIdentity);
}

/// <summary>
/// Immutable binding environment for one in-memory row, including exact occurrence provenance and the
/// optional relation root from which the row was derived.
/// </summary>
sealed class RelationQueryRuntimeRow
{
    static readonly ImmutableDictionary<ValueBindingId, RelationQueryRuntimeBinding> EmptyBindings =
        ImmutableDictionary<ValueBindingId, RelationQueryRuntimeBinding>.Empty;
    readonly ImmutableDictionary<ValueBindingId, RelationQueryRuntimeBinding> bindings;

    RelationQueryRuntimeRow(
        ImmutableDictionary<ValueBindingId, RelationQueryRuntimeBinding> bindings,
        ImmutableArray<RelationQueryObservationOccurrence> provenance,
        RelationQueryObservationOccurrence? root)
    {
        this.bindings = bindings;
        Provenance = NormalizeProvenance(
            provenance,
            bindings.Values.Select(static binding => binding.Occurrence),
            root);
        if (root is not null
            && !Provenance.Any(occurrence => occurrence.Id == root.Id && Equals(occurrence, root)))
        {
            throw new ArgumentException(
                "A relation root must be included in the row provenance.",
                nameof(root));
        }

        Root = root;
        ExpressionBindings = new ReadOnlyDictionary<ValueBindingId, RelationQueryExpressionBinding>(
            bindings.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToExpressionBinding()));
    }

    /// <summary>Creates an empty runtime row.</summary>
    public static RelationQueryRuntimeRow Empty { get; } =
        new(EmptyBindings, [], root: null);

    /// <summary>Creates a row containing one binding and optional relation-root attribution.</summary>
    public static RelationQueryRuntimeRow FromBinding(
        ValueBindingId binding,
        RelationQueryRuntimeBinding value,
        RelationQueryObservationOccurrence? root = null)
    {
        RequireBinding(binding, nameof(binding));
        ArgumentNullException.ThrowIfNull(value);
        RequireBindingValue(binding, value, nameof(value));
        return new(
            EmptyBindings.Add(binding, value),
            [],
            root);
    }

    /// <summary>Bindings visible in this row.</summary>
    public IReadOnlyDictionary<ValueBindingId, RelationQueryRuntimeBinding> Bindings => bindings;

    /// <summary>Expression-evaluator projection of <see cref="Bindings"/>.</summary>
    public IReadOnlyDictionary<ValueBindingId, RelationQueryExpressionBinding> ExpressionBindings { get; }

    /// <summary>Exact contributing occurrences sorted by occurrence identity.</summary>
    public ImmutableArray<RelationQueryObservationOccurrence> Provenance { get; }

    /// <summary>Relation-root occurrence from which this row was derived, or <see langword="null"/>.</summary>
    public RelationQueryObservationOccurrence? Root { get; }

    /// <summary>Tries to get one visible binding.</summary>
    public bool TryGetBinding(
        ValueBindingId binding,
        out RelationQueryRuntimeBinding value) =>
        Bindings.TryGetValue(binding, out value!);

    /// <summary>Adds or replaces one binding while retaining row provenance and relation-root attribution.</summary>
    public RelationQueryRuntimeRow WithBinding(
        ValueBindingId binding,
        RelationQueryRuntimeBinding value)
    {
        RequireBinding(binding, nameof(binding));
        ArgumentNullException.ThrowIfNull(value);
        RequireBindingValue(binding, value, nameof(value));
        return new(
            bindings.SetItem(binding, value),
            Provenance,
            Root);
    }

    /// <summary>
    /// Replaces the visible binding environment with one binding while retaining provenance and root attribution.
    /// </summary>
    public RelationQueryRuntimeRow WithOnlyBinding(
        ValueBindingId binding,
        RelationQueryRuntimeBinding value)
    {
        RequireBinding(binding, nameof(binding));
        ArgumentNullException.ThrowIfNull(value);
        RequireBindingValue(binding, value, nameof(value));
        return new(
            EmptyBindings.Add(binding, value),
            Provenance,
            Root);
    }

    /// <summary>Returns this row with explicit relation-root attribution.</summary>
    public RelationQueryRuntimeRow WithRoot(RelationQueryObservationOccurrence root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return new(
            bindings,
            Provenance,
            root);
    }

    /// <summary>
    /// Removes per-root attribution while retaining the root occurrence as ordinary row provenance.
    /// </summary>
    public RelationQueryRuntimeRow WithoutRoot() =>
        Root is null
            ? this
            : new(
                bindings,
                Provenance,
                root: null);

    /// <summary>
    /// Unions additional exact occurrences into this row's provenance without changing its bindings or root.
    /// </summary>
    public RelationQueryRuntimeRow WithAdditionalProvenance(
        IEnumerable<RelationQueryObservationOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        return new(
            bindings,
            [.. Provenance, .. occurrences],
            Root);
    }

    /// <summary>
    /// Merges two disjoint binding environments and their provenance. Conflicting visible bindings or roots
    /// are rejected rather than silently overwritten.
    /// </summary>
    public RelationQueryRuntimeRow Merge(RelationQueryRuntimeRow other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var mergedBindings = bindings;
        foreach (var (binding, value) in other.Bindings)
        {
            if (mergedBindings.ContainsKey(binding))
            {
                throw new InvalidOperationException(
                    $"Runtime rows cannot merge colliding binding '{binding.Value}'.");
            }

            mergedBindings = mergedBindings.Add(binding, value);
        }

        RelationQueryObservationOccurrence? root;
        if (Root is null)
        {
            root = other.Root;
        }
        else if (other.Root is null || Equals(Root, other.Root))
        {
            root = Root;
        }
        else
        {
            throw new InvalidOperationException(
                $"Runtime rows cannot merge distinct relation roots '{Root.Id.Value}' and '{other.Root.Id.Value}'.");
        }

        return new(
            mergedBindings,
            [.. Provenance, .. other.Provenance],
            root);
    }

    /// <summary>Creates an expression evaluation context for this row.</summary>
    public RelationQueryExpressionContext CreateExpressionContext(
        ValueBindingId? implicitBinding = null,
        IReadOnlyDictionary<string, ObservationValue>? parameters = null,
        ObservationValue? currentItem = null,
        IReadOnlyList<ObservationValue>? sourceRows = null,
        Func<ValueBindingId, FieldPath, bool>? isFieldAvailable = null,
        Func<string, bool>? isParameterAvailable = null,
        Func<Cohesive.Model.Expressions.ExprCapabilityId, bool>? isCapabilityAvailable = null) =>
        new(
            ExpressionBindings,
            implicitBinding,
            parameters,
            currentItem,
            Root?.ObservationIdentity,
            sourceRows,
            isFieldAvailable,
            isParameterAvailable,
            isCapabilityAvailable);

    static ImmutableArray<RelationQueryObservationOccurrence> NormalizeProvenance(
        ImmutableArray<RelationQueryObservationOccurrence> provenance,
        IEnumerable<RelationQueryObservationOccurrence?> bindingOccurrences,
        RelationQueryObservationOccurrence? root)
    {
        var supplied = provenance.IsDefault ? [] : provenance;
        if (supplied.Any(static occurrence => occurrence is null))
            throw new ArgumentException("Row provenance cannot contain null occurrences.", nameof(provenance));

        Dictionary<RelationQueryOccurrenceId, RelationQueryObservationOccurrence> normalized = [];
        foreach (var occurrence in supplied
                     .Concat(bindingOccurrences.WhereNotNull())
                     .Concat(root is null ? [] : [root]))
        {
            if (normalized.TryGetValue(occurrence.Id, out var existing))
            {
                if (!Equals(existing, occurrence))
                {
                    throw new ArgumentException(
                        $"Row provenance contains conflicting occurrence '{occurrence.Id.Value}'.",
                        nameof(provenance));
                }

                continue;
            }

            normalized.Add(occurrence.Id, occurrence);
        }

        return
        [
            .. normalized.Values.OrderBy(
                static occurrence => occurrence.Id.Value,
                StringComparer.Ordinal)
        ];
    }

    static void RequireBinding(ValueBindingId binding, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(binding.Value))
            throw new ArgumentException("A runtime row requires a non-empty binding identity.", parameterName);
    }

    static void RequireBindingValue(
        ValueBindingId binding,
        RelationQueryRuntimeBinding value,
        string parameterName)
    {
        if (value.Occurrence is { } occurrence && occurrence.Binding != binding)
        {
            throw new ArgumentException(
                $"Observed occurrence '{occurrence.Id.Value}' belongs to binding '{occurrence.Binding.Value}', not '{binding.Value}'.",
                parameterName);
        }
    }
}
