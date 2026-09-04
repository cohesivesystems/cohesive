using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Plan-bound view of one authored placement input for consumption by target-adapter authoring surfaces.
/// </summary>
public class RelationQueryPlacedInput
{
    readonly IReadOnlyDictionary<FieldPath, RelationQueryFieldInputContract> fieldsByPath;

    internal RelationQueryPlacedInput(
        CompiledRelationQueryPlan plan,
        RelationQuerySourcePlacement placement,
        RelationQuerySourcePlacementBinding binding,
        RelationQuerySourceInstance source,
        ImmutableArray<RelationQueryFieldInputContract> fields)
    {
        Plan = Guard.RequireNotNull(plan);
        Placement = Guard.RequireNotNull(placement);
        Binding = Guard.RequireNotNull(binding);
        Source = Guard.RequireNotNull(source);
        var normalized = fields.IsDefault ? [] : fields;
        if (normalized.Any(static field => field is null))
        {
            throw new ArgumentException("Placed-input fields cannot contain null entries.", nameof(fields));
        }

        if (normalized.GroupBy(static field => field.Input.Field.Path).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Placed-input fields cannot repeat a semantic path.", nameof(fields));
        }

        Fields = [.. normalized.OrderBy(static field => field.Input.Id.Value, StringComparer.Ordinal)];
        fieldsByPath = Fields.ToDictionary(static field => field.Input.Field.Path);
    }

    /// <summary>Exact demand-scoped compiled plan owning this placed input.</summary>
    public CompiledRelationQueryPlan Plan { get; }

    /// <summary>Complete normalized source-placement artifact containing this input.</summary>
    public RelationQuerySourcePlacement Placement { get; }

    /// <summary>Exact plan-scoped placement binding.</summary>
    public RelationQuerySourcePlacementBinding Binding { get; }

    /// <summary>Concrete source instance selected by <see cref="Binding"/>.</summary>
    public RelationQuerySourceInstance Source { get; }

    /// <summary>Semantic shape supplied by the placed input.</summary>
    public QualifiedShapeId Shape => Binding.Shape;

    /// <summary>Exact demand-scoped field contracts available from this placed input.</summary>
    public ImmutableArray<RelationQueryFieldInputContract> Fields { get; }

    /// <summary>Attempts to resolve an exact demanded field by semantic path.</summary>
    /// <param name="semanticPath">Semantic field path to resolve.</param>
    /// <param name="field">Receives the exact compiled field contract when found.</param>
    /// <returns><see langword="true"/> when the field is demanded from this input; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="semanticPath"/> is empty.</exception>
    public bool TryGetField(
        FieldPath semanticPath,
        [NotNullWhen(true)] out RelationQueryFieldInputContract? field)
    {
        if (semanticPath.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A placed-input field path cannot be empty.", nameof(semanticPath));
        }

        return fieldsByPath.TryGetValue(semanticPath, out field);
    }

    /// <summary>Resolves an exact demanded field by semantic path.</summary>
    /// <param name="semanticPath">Semantic field path to resolve.</param>
    /// <returns>The exact compiled field contract.</returns>
    /// <exception cref="ArgumentException"><paramref name="semanticPath"/> is empty.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="semanticPath"/> is not demanded from this input.</exception>
    public RelationQueryFieldInputContract GetField(FieldPath semanticPath) =>
        TryGetField(semanticPath, out var field)
            ? field
            : throw new KeyNotFoundException(
                $"Placed input '{Binding.Input.Value}' has no demanded field at semantic path '{semanticPath}'.");
}

/// <summary>Typed plan-bound view of one CLR-backed authored placement input.</summary>
/// <typeparam name="T">CLR type represented by <see cref="RelationQueryPlacedInput.Shape"/>.</typeparam>
public sealed class RelationQueryPlacedInput<T> : RelationQueryPlacedInput
    where T : notnull
{
    internal RelationQueryPlacedInput(
        CompiledRelationQueryPlan plan,
        RelationQuerySourcePlacement placement,
        RelationQuerySourcePlacementBinding binding,
        RelationQuerySourceInstance source,
        ImmutableArray<RelationQueryFieldInputContract> fields,
        RelationQueryClrShape<T> clrShape)
        : base(plan, placement, binding, source, fields)
    {
        ClrShape = Guard.RequireNotNull(clrShape);
        if (ClrShape.Id != binding.Shape)
        {
            throw new ArgumentException("The CLR shape does not match the placed semantic shape.", nameof(clrShape));
        }
    }

    /// <summary>Authoritative CLR metadata mapping used by typed field selectors.</summary>
    public RelationQueryClrShape<T> ClrShape { get; }

    /// <summary>Resolves a typed CLR property selector to its authoritative semantic path.</summary>
    /// <typeparam name="TValue">CLR value selected by the property chain.</typeparam>
    /// <param name="selector">Direct or nested readable-property chain rooted at its parameter.</param>
    /// <returns>The profile-derived or explicitly overridden semantic field path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="selector"/> is not a rooted readable-property chain.</exception>
    /// <exception cref="InvalidOperationException">The CLR metadata profile cannot resolve the selected path.</exception>
    public FieldPath ResolveFieldPath<TValue>(Expression<Func<T, TValue>> selector) =>
        FieldPath.Capture(selector, ClrShape.ResolveMemberPath);

    /// <summary>Resolves a typed CLR property selector to its exact demanded field contract.</summary>
    /// <typeparam name="TValue">CLR value selected by the property chain.</typeparam>
    /// <param name="selector">Direct or nested readable-property chain rooted at its parameter.</param>
    /// <returns>The exact compiled field contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="selector"/> is not a rooted readable-property chain.</exception>
    /// <exception cref="InvalidOperationException">The CLR metadata profile cannot resolve the selected path.</exception>
    /// <exception cref="KeyNotFoundException">The selected semantic path is not demanded from this input.</exception>
    public RelationQueryFieldInputContract GetField<TValue>(Expression<Func<T, TValue>> selector) =>
        GetField(ResolveFieldPath(selector));

    /// <summary>
    /// Resolves a CLR collection-element selector to its authoritative semantic path relative to one element.
    /// </summary>
    /// <typeparam name="TElement">CLR type of one selected collection element.</typeparam>
    /// <typeparam name="TValue">CLR value selected from one collection element.</typeparam>
    /// <param name="collectionSelector">
    /// Readable CLR property chain selecting an enumerable field on this placed input.
    /// </param>
    /// <param name="elementSelector">
    /// Readable CLR property chain rooted at one element of the selected collection.
    /// </param>
    /// <returns>
    /// The profile-derived or explicitly overridden semantic field path relative to one collection element.
    /// </returns>
    /// <exception cref="ArgumentNullException">A selector is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A selector is not a rooted readable-property chain.</exception>
    /// <exception cref="InvalidOperationException">
    /// The CLR metadata profile cannot resolve the selected collection or element path.
    /// </exception>
    /// <exception cref="KeyNotFoundException">
    /// The selected outer collection is not a demanded field on this placed input.
    /// </exception>
    public FieldPath ResolveCollectionElementFieldPath<TElement, TValue>(
        Expression<Func<T, IEnumerable<TElement>>> collectionSelector,
        Expression<Func<TElement, TValue>> elementSelector)
        where TElement : notnull
    {
        _ = GetField(ResolveFieldPath(collectionSelector));
        return FieldPath.Capture(elementSelector, ClrShape.ResolveMemberPath);
    }
}
