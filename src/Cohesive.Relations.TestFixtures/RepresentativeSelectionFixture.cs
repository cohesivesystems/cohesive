using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.TestFixtures;

/// <summary>Canonical representative semantics and evidence shared by interpreter, adapter and performance tests.</summary>
public static class RepresentativeSelectionFixture
{
    /// <summary>Exact candidate shape revision.</summary>
    public static QualifiedShapeId Shape { get; } = new(new("representative-fixture/v1"), new("Candidate"));
    /// <summary>Candidate value binding.</summary>
    public static ValueBindingId Binding { get; } = new("candidate");
    /// <summary>Candidate source node.</summary>
    public static QueryNodeId Source { get; } = new("candidates");
    /// <summary>Representative-selection node.</summary>
    public static QueryNodeId Selection { get; } = new("representative");
    /// <summary>Candidate identity path.</summary>
    public static FieldPath Id { get; } = FieldPath.FromField(nameof(Candidate.Id));
    /// <summary>Partition-key path.</summary>
    public static FieldPath Key { get; } = FieldPath.FromField(nameof(Candidate.Key));
    /// <summary>Primary ordering-key path.</summary>
    public static FieldPath Preference { get; } = FieldPath.FromField(nameof(Candidate.Preference));
    /// <summary>Post-selection eligibility path.</summary>
    public static FieldPath Eligible { get; } = FieldPath.FromField(nameof(Candidate.Eligible));
    /// <summary>Shape contracts for the canonical fixture.</summary>
    public static ImmutableArray<ShapeGraphDocument> Shapes { get; } =
    [
        ShapeGraphDocument.FromGraph(new(Shape.GraphId,
        [
            new(Shape.ShapeId,
            [
                new(new(nameof(Candidate.Id)), new ScalarTypeRef(ScalarTypeKind.Int64), role: FieldRole.Identity),
                new(new(nameof(Candidate.Key)), new ScalarTypeRef(ScalarTypeKind.String),
                    presence: FieldPresence.Optional, nullability: FieldNullability.Nullable),
                new(new(nameof(Candidate.Preference)), new ScalarTypeRef(ScalarTypeKind.Int64), nullability: FieldNullability.Nullable),
                new(new(nameof(Candidate.Eligible)), new ScalarTypeRef(ScalarTypeKind.Bool))
            ])
        ]))
    ];

    /// <summary>Creates a portable query with representative selection and final identity ordering.</summary>
    /// <param name="tieBreaker">Append ascending identity to resolve equal preferences.</param>
    /// <param name="filterAfter">Filter winners by eligibility after selection.</param>
    /// <param name="global">Use one partition instead of partitioning by Key.</param>
    /// <param name="direction">Preference direction.</param>
    /// <param name="nullPlacement">Placement of null preferences independent of direction.</param>
    /// <returns>A validated canonical query document.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Direction or null placement is unsupported.</exception>
    public static RelationQueryDocument Document(
        bool tieBreaker = true, bool filterAfter = false, bool global = false,
        QuerySortDirection direction = QuerySortDirection.Descending,
        QueryNullPlacement nullPlacement = QueryNullPlacement.Last)
    {
        List<LogicalQueryNode> nodes = [new SourceQueryNode(Source, Binding, Shape)];
        List<QueryOrdering> orderings = [new(Expr.Field(Binding, Preference), direction, nullPlacement)];
        if (tieBreaker) orderings.Add(new(Expr.Field(Binding, Id)));
        nodes.Add(new SelectRepresentativeQueryNode(Selection, Source,
            global ? [] : [Expr.Field(Binding, Key)], [.. orderings]));
        var terminal = Selection;
        if (filterAfter)
        {
            terminal = new("eligible-winners");
            nodes.Add(new FilterQueryNode(terminal, Selection, Expr.Field(Binding, Eligible)));
        }
        var ordered = new QueryNodeId("result-order");
        nodes.Add(new OrderQueryNode(ordered, terminal, [new(Expr.Field(Binding, Id))]));
        QueryDefinition query = new(new("representative-fixture"), new("RepresentativeFixture"),
            new LogicalQueryDefinition([.. nodes]), [new RowsQueryResultDefinition(new("rows"), ordered)]);
        return RelationQueryDocument.FromDefinition(query);
    }

    /// <summary>Compiles a query against the fixture shape revision.</summary>
    /// <param name="document">Canonical document using the fixture shape.</param>
    /// <returns>The complete static execution plan.</returns>
    /// <exception cref="InvalidOperationException">The document cannot be compiled against the fixture shapes.</exception>
    public static CompiledRelationQueryPlan Compile(RelationQueryDocument document)
    {
        var result = RelationQueryStaticCompiler.Compile(new(document, Shapes));
        if (!result.IsSuccessful)
            throw new InvalidOperationException(string.Join("\n", result.Validation.Diagnostics));
        return result.Plan!;
    }

    /// <summary>Provides source and demanded field evidence for the single-source fixture.</summary>
    /// <param name="plan">Compiled fixture plan.</param>
    /// <param name="rows">Candidate occurrences with unique identities; values retain explicit missing/null states.</param>
    /// <returns>Complete evidence pinned to the supplied plan.</returns>
    /// <exception cref="InvalidOperationException">The plan does not contain exactly one source or requests an unknown field.</exception>
    public static RelationQueryRuntimeEvidence Evidence(CompiledRelationQueryPlan plan, IReadOnlyList<Candidate> rows)
    {
        var source = plan.RequirementGraph.Inputs.OfType<RelationQuerySourceSetInput>().Single();
        var occurrences = rows.Select(row => new RelationQueryObservationOccurrence(
            new($"candidate/{row.Id.ToString(CultureInfo.InvariantCulture)}"), Binding, Shape,
            row.Id.ToString(CultureInfo.InvariantCulture))).ToArray();
        var fields = ImmutableArray.CreateBuilder<RelationQueryFieldEvidence>();
        foreach (var input in plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>())
        {
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                var value = input.Field.Path == Id ? ObservationValue.FromInt64(row.Id)
                    : input.Field.Path == Key ? row.Key
                    : input.Field.Path == Preference ? row.Preference is { } preference ? ObservationValue.FromInt64(preference) : ObservationValue.Null
                    : input.Field.Path == Eligible ? ObservationValue.FromBool(row.Eligible)
                    : throw new InvalidOperationException($"Unknown fixture field {input.Field.Path}.");
                fields.Add(new(input.Id, occurrences[index].Id,
                    value.Kind == ObservationValueKind.Undefined ? RelationQueryFieldEvidenceState.Missing
                    : value.Kind == ObservationValueKind.Null ? RelationQueryFieldEvidenceState.Null
                    : RelationQueryFieldEvidenceState.Value,
                    value.Kind is ObservationValueKind.Undefined or ObservationValueKind.Null ? null : value));
            }
        }
        return new(new("representative-fixture/run"), plan,
            sources: [new(source.Id, RelationQuerySourceEvidenceState.Provided, [.. occurrences])],
            fields: fields.ToImmutable(),
            capabilities: [.. plan.RequirementGraph.Inputs.OfType<RelationQueryCapabilityInput>()
                .Select(input => new RelationQueryCapabilityEvidence(input.Id, RelationQueryCapabilityEvidenceState.Available))]);
    }

    /// <summary>One source candidate.</summary>
    /// <param name="Id">Unique source identity and optional tie-breaker.</param>
    /// <param name="Key">String, null or missing partition key.</param>
    /// <param name="Preference">Primary preference, including null.</param>
    /// <param name="Eligible">Post-selection eligibility.</param>
    public sealed record Candidate(long Id, ObservationValue Key, long? Preference, bool Eligible = true);
}
