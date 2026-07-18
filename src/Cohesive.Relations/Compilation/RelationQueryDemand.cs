using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Compilation;

/// <summary>
/// Kind of output demand supplied to static relation/query compilation.
/// </summary>
public enum RelationQueryCompilationDemandKind
{
    /// <summary>
    /// Compile every semantically emitted relation field or declared query result; convention-derived
    /// demand omits optional output fields that have no producing assignment.
    /// </summary>
    AllDeclaredOutputs = 0,

    /// <summary>Compile an explicit set of relation-output fields.</summary>
    RelationFields = 1,

    /// <summary>Compile an explicit set of query results and their selected fields.</summary>
    QueryResults = 2
}

/// <summary>
/// Whether a query result demands every field or an explicit field subset.
/// </summary>
public enum RelationQueryFieldSelectionKind
{
    /// <summary>Every field emitted by the result is demanded.</summary>
    AllFields = 0,

    /// <summary>Only the explicitly selected fields are demanded.</summary>
    SelectedFields = 1
}

/// <summary>
/// Graph-qualified reference to a semantic field.
/// </summary>
public readonly record struct RelationQueryFieldReference
{
    /// <summary>Creates a graph-qualified field reference.</summary>
    /// <param name="shape">Shape that declares the field.</param>
    /// <param name="path">Field path relative to <paramref name="shape"/>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="shape"/> is default or incomplete, or <paramref name="path"/> is empty or malformed.
    /// </exception>
    [JsonConstructor]
    public RelationQueryFieldReference(QualifiedShapeId shape, FieldPath path)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value)
            || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
        {
            throw new ArgumentException("A field reference requires a graph-qualified shape.", nameof(shape));
        }

        if (!RelationQueryContractOrdering.IsValidFieldPath(path))
            throw new ArgumentException("A field reference requires a valid non-empty field path.", nameof(path));

        Shape = shape;
        Path = path;
    }

    /// <summary>Shape that declares the field.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Field path relative to <see cref="Shape"/>.</summary>
    public FieldPath Path { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Shape}:{Path}";
}

/// <summary>
/// Field demand for one named query result.
/// </summary>
public sealed record QueryResultDemand
{
    /// <summary>Creates a query-result demand.</summary>
    /// <param name="result">Named query result to compile.</param>
    /// <param name="selection">Whether all fields or selected fields are demanded.</param>
    /// <param name="fields">Selected fields; required only for <see cref="RelationQueryFieldSelectionKind.SelectedFields"/>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="result"/> is default, selected fields are empty, a field is invalid, or fields are supplied
    /// for <see cref="RelationQueryFieldSelectionKind.AllFields"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="selection"/> is unsupported.</exception>
    public QueryResultDemand(
        QueryResultId result,
        RelationQueryFieldSelectionKind selection,
        ImmutableArray<RelationQueryFieldReference> fields = default)
    {
        if (string.IsNullOrWhiteSpace(result.Value))
            throw new ArgumentException("A query-result demand requires a non-empty result identifier.", nameof(result));
        if (!Enum.IsDefined(selection))
            throw new ArgumentOutOfRangeException(nameof(selection), selection, "Unsupported field-selection kind.");

        var normalized = fields.IsDefault ? [] : fields;
        if (normalized.Any(static field => !RelationQueryContractOrdering.IsValid(field)))
            throw new ArgumentException("Query-result fields must be valid graph-qualified field references.", nameof(fields));
        if (selection == RelationQueryFieldSelectionKind.AllFields && !normalized.IsDefaultOrEmpty)
            throw new ArgumentException("An all-fields demand cannot also declare selected fields.", nameof(fields));
        if (selection == RelationQueryFieldSelectionKind.SelectedFields && normalized.IsDefaultOrEmpty)
            throw new ArgumentException("A selected-fields demand requires at least one field.", nameof(fields));

        Result = result;
        Selection = selection;
        Fields = RelationQueryContractOrdering.NormalizeFields(normalized);
    }

    /// <summary>Named query result to compile.</summary>
    public QueryResultId Result { get; }

    /// <summary>Whether every result field or an explicit subset is demanded.</summary>
    public RelationQueryFieldSelectionKind Selection { get; }

    /// <summary>Selected fields, or an empty array when <see cref="Selection"/> is all fields.</summary>
    public ImmutableArray<RelationQueryFieldReference> Fields { get; }

    /// <summary>Creates a demand for every field of a query result.</summary>
    /// <param name="result">Named query result to compile.</param>
    /// <returns>A query-result demand selecting every field.</returns>
    /// <exception cref="ArgumentException"><paramref name="result"/> is default.</exception>
    public static QueryResultDemand AllFields(QueryResultId result) =>
        new(result, RelationQueryFieldSelectionKind.AllFields);

    /// <summary>Creates a demand for selected fields of a query result.</summary>
    /// <param name="result">Named query result to compile.</param>
    /// <param name="fields">Graph-qualified fields to select.</param>
    /// <returns>A query-result demand selecting only <paramref name="fields"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="result"/> is default, or <paramref name="fields"/> is empty or contains an invalid field.
    /// </exception>
    public static QueryResultDemand SelectedFields(
        QueryResultId result,
        IEnumerable<RelationQueryFieldReference> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return new(result, RelationQueryFieldSelectionKind.SelectedFields, [.. fields]);
    }
}

/// <summary>
/// Output demand that determines the roots of demand-driven static compilation.
/// </summary>
public sealed class RelationQueryCompilationDemand
{
    /// <summary>
    /// Convention demand that compiles every semantically emitted output, omitting optional fields
    /// that have no producing assignment.
    /// </summary>
    public static RelationQueryCompilationDemand AllDeclaredOutputs { get; } =
        new(RelationQueryCompilationDemandKind.AllDeclaredOutputs, [], []);

    RelationQueryCompilationDemand(
        RelationQueryCompilationDemandKind kind,
        ImmutableArray<RelationQueryFieldReference> relationFields,
        ImmutableArray<QueryResultDemand> queryResults
        )
    {
        Kind = kind;
        RelationFields = relationFields;
        QueryResults = queryResults;
    }

    /// <summary>Kind of demand represented by this instance.</summary>
    public RelationQueryCompilationDemandKind Kind { get; }

    /// <summary>
    /// Selected relation-output fields, or an empty array for another <see cref="Kind"/>.
    /// </summary>
    public ImmutableArray<RelationQueryFieldReference> RelationFields { get; }

    /// <summary>
    /// Selected query results, or an empty array for another <see cref="Kind"/>.
    /// </summary>
    public ImmutableArray<QueryResultDemand> QueryResults { get; }

    /// <summary>Creates a demand for selected relation-output fields.</summary>
    /// <param name="fields">Graph-qualified relation-output fields to compile.</param>
    /// <returns>A relation-field compilation demand.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fields"/> is empty or contains an invalid field.</exception>
    public static RelationQueryCompilationDemand ForRelationFields(IEnumerable<RelationQueryFieldReference> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var normalized = fields.ToImmutableArray();
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException("A relation-field demand requires at least one field.", nameof(fields));
        if (normalized.Any(static field => !RelationQueryContractOrdering.IsValid(field)))
            throw new ArgumentException("Relation fields must be valid graph-qualified field references.", nameof(fields));

        return new(
            RelationQueryCompilationDemandKind.RelationFields,
            RelationQueryContractOrdering.NormalizeFields(normalized),
            []);
    }

    /// <summary>Creates a demand for selected query results.</summary>
    /// <param name="results">Named query-result demands to compile.</param>
    /// <returns>A query-result compilation demand.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="results"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="results"/> is empty, contains a <see langword="null"/> entry, or repeats a result identifier.
    /// </exception>
    public static RelationQueryCompilationDemand ForQueryResults(IEnumerable<QueryResultDemand> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var normalized = results.ToImmutableArray();
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException("A query-result demand requires at least one result.", nameof(results));
        if (normalized.Any(static result => result is null))
            throw new ArgumentException("Query-result demands cannot contain null entries.", nameof(results));
        if (normalized.GroupBy(static result => result.Result).Any(static group => group.Count() > 1))
            throw new ArgumentException("A query-result demand cannot repeat a result identifier.", nameof(results));

        return new(
            RelationQueryCompilationDemandKind.QueryResults,
            [],
            [.. normalized.OrderBy(static result => result.Result.Value, StringComparer.Ordinal)]
            );
    }
}

static class RelationQueryContractOrdering
{
    public static bool IsValid(RelationQueryFieldReference field) =>
        !string.IsNullOrWhiteSpace(field.Shape.GraphId.Value)
        && !string.IsNullOrWhiteSpace(field.Shape.ShapeId.Value)
        && IsValidFieldPath(field.Path);

    public static bool IsValidFieldPath(FieldPath path) =>
        !path.Segments.IsDefaultOrEmpty
        && path.Segments.All(static segment => segment.Kind switch
        {
            SegmentKind.Field => !string.IsNullOrWhiteSpace(segment.Segment),
            SegmentKind.Element => segment.Segment is null,
            _ => false
        });

    public static ImmutableArray<RelationQueryFieldReference> NormalizeFields(
        IEnumerable<RelationQueryFieldReference> fields) =>
    [
        .. fields.Distinct()
            .OrderBy(static field => field.Shape.GraphId.Value, StringComparer.Ordinal)
            .ThenBy(static field => field.Shape.ShapeId.Value, StringComparer.Ordinal)
            .ThenBy(static field => FieldPathKey(field.Path), StringComparer.Ordinal)
    ];

    public static string FieldPathKey(FieldPath path) =>
        path.Segments.IsDefaultOrEmpty
            ? string.Empty
            : string.Join(
                '\u001f',
                path.Segments.Select(static segment => string.Concat(
                    ((int)segment.Kind).ToString(CultureInfo.InvariantCulture),
                    ":",
                    (segment.Segment?.Length ?? -1).ToString(CultureInfo.InvariantCulture),
                    ":",
                    segment.Segment)));
}
