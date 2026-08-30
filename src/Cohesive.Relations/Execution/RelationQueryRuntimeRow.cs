using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
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
        if (kind == RelationQueryRuntimeBindingKind.Observed && occurrence is null
            || kind == RelationQueryRuntimeBindingKind.Absent && occurrence is not null)
        {
            throw new ArgumentException(
                "Observed bindings require an occurrence, absent bindings cannot carry one, and computed bindings may carry derived occurrence lineage.",
                nameof(occurrence));
        }
        if (occurrence is not null
            && (shape is null || occurrence.Shape != shape.Value))
        {
            throw new ArgumentException(
                "A runtime binding occurrence must use the binding shape.",
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
        bool isAuthoritativeValue = false,
        RelationQueryObservationOccurrence? occurrence = null) =>
        new(
            RelationQueryRuntimeBindingKind.Computed,
            shape,
            value,
            occurrence,
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
sealed class RelationQueryRuntimeRow : IReadOnlyDictionary<ValueBindingId, RelationQueryExpressionBinding>
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
        Debug.Assert(IsCanonicalProvenance(provenance));
        Debug.Assert(root is null || ContainsOccurrence(provenance, root));
        Provenance = provenance;
        Root = root;
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
            CreateProvenance(value.Occurrence, root),
            root);
    }

    /// <summary>Bindings visible in this row.</summary>
    public IReadOnlyDictionary<ValueBindingId, RelationQueryRuntimeBinding> Bindings => bindings;

    /// <summary>Expression-evaluator projection of <see cref="Bindings"/>.</summary>
    public IReadOnlyDictionary<ValueBindingId, RelationQueryExpressionBinding> ExpressionBindings => this;

    /// <inheritdoc />
    public RelationQueryExpressionBinding this[ValueBindingId key] => bindings[key].ToExpressionBinding();

    /// <inheritdoc />
    public int Count => bindings.Count;

    /// <inheritdoc />
    public IEnumerable<ValueBindingId> Keys => bindings.Keys;

    /// <inheritdoc />
    public IEnumerable<RelationQueryExpressionBinding> Values
    {
        get
        {
            foreach (var binding in bindings.Values)
                yield return binding.ToExpressionBinding();
        }
    }

    /// <summary>Exact contributing occurrences sorted by occurrence identity.</summary>
    public ImmutableArray<RelationQueryObservationOccurrence> Provenance { get; }

    /// <summary>Relation-root occurrence from which this row was derived, or <see langword="null"/>.</summary>
    public RelationQueryObservationOccurrence? Root { get; }

    /// <summary>Tries to get one visible binding.</summary>
    public bool TryGetBinding(
        ValueBindingId binding,
        out RelationQueryRuntimeBinding value) =>
        Bindings.TryGetValue(binding, out value!);

    /// <inheritdoc />
    public bool ContainsKey(ValueBindingId key) => bindings.ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(
        ValueBindingId key,
        out RelationQueryExpressionBinding value)
    {
        if (bindings.TryGetValue(key, out var binding))
        {
            value = binding.ToExpressionBinding();
            return true;
        }

        value = default;
        return false;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<ValueBindingId, RelationQueryExpressionBinding>> GetEnumerator()
    {
        foreach (var (key, binding) in bindings)
            yield return KeyValuePair.Create(key, binding.ToExpressionBinding());
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

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
            AddOccurrence(Provenance, value.Occurrence),
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
            AddOccurrence(Provenance, value.Occurrence),
            Root);
    }

    /// <summary>Returns this row with explicit relation-root attribution.</summary>
    public RelationQueryRuntimeRow WithRoot(RelationQueryObservationOccurrence root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return new(
            bindings,
            AddOccurrence(Provenance, root),
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
        var additional = occurrences is ImmutableArray<RelationQueryObservationOccurrence> immutable
            ? immutable.IsDefault
                ? []
                : IsCanonicalProvenance(immutable)
                    ? immutable
                    : NormalizeProvenance(immutable)
            : NormalizeProvenance(occurrences);
        return new(
            bindings,
            MergeProvenance(Provenance, additional),
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
            MergeProvenance(Provenance, other.Provenance),
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
        RelationQueryExpressionContext.FromPrevalidated(
            ExpressionBindings,
            implicitBinding,
            parameters,
            currentItem,
            Root?.ObservationIdentity,
            sourceRows,
            isFieldAvailable,
            isParameterAvailable,
            isCapabilityAvailable);

    static ImmutableArray<RelationQueryObservationOccurrence> CreateProvenance(
        RelationQueryObservationOccurrence? occurrence,
        RelationQueryObservationOccurrence? root)
    {
        if (occurrence is null)
            return root is null ? [] : [root];
        if (root is null)
            return [occurrence];

        var comparison = CompareOccurrenceIds(occurrence, root);
        if (comparison == 0)
        {
            RequireSameOccurrence(occurrence, root);
            return [occurrence];
        }

        return comparison < 0 ? [occurrence, root] : [root, occurrence];
    }

    static ImmutableArray<RelationQueryObservationOccurrence> AddOccurrence(
        ImmutableArray<RelationQueryObservationOccurrence> provenance,
        RelationQueryObservationOccurrence? occurrence)
    {
        if (occurrence is null)
            return provenance;

        var lower = 0;
        var upper = provenance.Length;
        while (lower < upper)
        {
            var middle = lower + ((upper - lower) / 2);
            var comparison = CompareOccurrenceIds(provenance[middle], occurrence);
            if (comparison < 0)
                lower = middle + 1;
            else
                upper = middle;
        }

        if (lower < provenance.Length && CompareOccurrenceIds(provenance[lower], occurrence) == 0)
        {
            RequireSameOccurrence(provenance[lower], occurrence);
            return provenance;
        }

        var result = ImmutableArray.CreateBuilder<RelationQueryObservationOccurrence>(provenance.Length + 1);
        for (var index = 0; index < lower; index++)
            result.Add(provenance[index]);
        result.Add(occurrence);
        for (var index = lower; index < provenance.Length; index++)
            result.Add(provenance[index]);
        return result.MoveToImmutable();
    }

    static ImmutableArray<RelationQueryObservationOccurrence> MergeProvenance(
        ImmutableArray<RelationQueryObservationOccurrence> left,
        ImmutableArray<RelationQueryObservationOccurrence> right)
    {
        if (left.IsDefaultOrEmpty)
            return right.IsDefault ? [] : right;
        if (right.IsDefaultOrEmpty)
            return left;
        if (left == right)
            return left;

        var duplicateCount = 0;
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var comparison = CompareOccurrenceIds(left[leftIndex], right[rightIndex]);
            if (comparison < 0)
            {
                leftIndex++;
            }
            else if (comparison > 0)
            {
                rightIndex++;
            }
            else
            {
                RequireSameOccurrence(left[leftIndex], right[rightIndex]);
                duplicateCount++;
                leftIndex++;
                rightIndex++;
            }
        }

        var result = ImmutableArray.CreateBuilder<RelationQueryObservationOccurrence>(
            left.Length + right.Length - duplicateCount);
        leftIndex = 0;
        rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var comparison = CompareOccurrenceIds(left[leftIndex], right[rightIndex]);
            if (comparison < 0)
            {
                result.Add(left[leftIndex++]);
            }
            else if (comparison > 0)
            {
                result.Add(right[rightIndex++]);
            }
            else
            {
                result.Add(left[leftIndex]);
                leftIndex++;
                rightIndex++;
            }
        }
        while (leftIndex < left.Length)
            result.Add(left[leftIndex++]);
        while (rightIndex < right.Length)
            result.Add(right[rightIndex++]);
        return result.MoveToImmutable();
    }

    static ImmutableArray<RelationQueryObservationOccurrence> NormalizeProvenance(
        IEnumerable<RelationQueryObservationOccurrence> occurrences)
    {
        Dictionary<RelationQueryOccurrenceId, RelationQueryObservationOccurrence> normalized = [];
        foreach (var occurrence in occurrences)
        {
            if (occurrence is null)
                throw new ArgumentException("Row provenance cannot contain null occurrences.", nameof(occurrences));

            if (normalized.TryGetValue(occurrence.Id, out var existing))
            {
                RequireSameOccurrence(existing, occurrence);
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

    internal static bool IsCanonicalProvenance(ImmutableArray<RelationQueryObservationOccurrence> provenance)
    {
        if (provenance.IsDefault)
            return false;

        for (var index = 0; index < provenance.Length; index++)
        {
            if (provenance[index] is null)
                return false;
            if (index > 0 && CompareOccurrenceIds(provenance[index - 1], provenance[index]) >= 0)
                return false;
        }

        return true;
    }

    internal static bool ContainsOccurrence(
        ImmutableArray<RelationQueryObservationOccurrence> provenance,
        RelationQueryObservationOccurrence occurrence)
    {
        foreach (var candidate in provenance)
        {
            var comparison = CompareOccurrenceIds(candidate, occurrence);
            if (comparison == 0)
                return Equals(candidate, occurrence);
            if (comparison > 0)
                return false;
        }

        return false;
    }

    static int CompareOccurrenceIds(
        RelationQueryObservationOccurrence left,
        RelationQueryObservationOccurrence right) =>
        StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value);

    static void RequireSameOccurrence(
        RelationQueryObservationOccurrence existing,
        RelationQueryObservationOccurrence candidate)
    {
        if (!Equals(existing, candidate))
        {
            throw new ArgumentException(
                $"Row provenance contains conflicting occurrence '{candidate.Id.Value}'.",
                nameof(candidate));
        }
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
