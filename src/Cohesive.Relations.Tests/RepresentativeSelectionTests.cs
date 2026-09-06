using System.Collections.Immutable;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Realization;
using Cohesive.Relations.TestFixtures;
using static Cohesive.Relations.TestFixtures.RepresentativeSelectionFixture;

namespace Cohesive.Relations.Tests;

public sealed class RepresentativeSelectionTests
{
    [Fact]
    public void SelectionIsPermutationIndependentAndRetainsOnlyWinnerProvenance()
    {
        var plan = Compile(Document());
        Candidate[] values = [new(1, S("a"), 5), new(2, S("a"), 8), new(3, S("b"), 4), new(4, S("b"), 4)];
        foreach (var input in new[] { values, [.. values.Reverse()], [values[2], values[0], values[3], values[1]] })
        {
            var result = Execute(plan, input);
            Assert.Equal([2L, 3L], Ids(result));
            Assert.Equal(["candidate/2", "candidate/3"],
                result.QueryResults.Single().Rows.Select(row => Assert.Single(row.InputOccurrences).Id.Value));
        }
        Assert.Contains(plan.ExecutionSlice.Nodes, node => node.CanonicalNode is SelectRepresentativeQueryNode);
        var selection = plan.ExecutionSlice.Nodes.Single(node => node.Id == Selection);
        Assert.Single(selection.RepresentativeKeys);
        Assert.Equal(2, selection.OrderKeys.Length);
        Assert.Equal(3, selection.ExpressionSites.Select(site => site.Analysis.Site.Id).Distinct().Count());
    }

    [Fact]
    public void PostSelectionFilterDoesNotFallBackToOlderEligibleRow()
    {
        var plan = Compile(Document(filterAfter: true));
        var result = Execute(plan, [new(1, S("a"), 5), new(2, S("a"), 8, Eligible: false), new(3, S("b"), 4)]);
        Assert.Equal([3L], Ids(result));
    }

    [Theory]
    [InlineData(QuerySortDirection.Ascending, QueryNullPlacement.First, 1L)]
    [InlineData(QuerySortDirection.Descending, QueryNullPlacement.First, 1L)]
    [InlineData(QuerySortDirection.Ascending, QueryNullPlacement.Last, 2L)]
    [InlineData(QuerySortDirection.Descending, QueryNullPlacement.Last, 3L)]
    public void NullPlacementIsIndependentOfDirection(QuerySortDirection direction, QueryNullPlacement nulls, long expected)
    {
        var plan = Compile(Document(direction: direction, nullPlacement: nulls));
        Assert.Equal([expected], Ids(Execute(plan, [new(1, S("a"), null), new(2, S("a"), 5), new(3, S("a"), 8)])));
    }

    [Fact]
    public void MissingAndNullPartitionKeysRemainDistinct()
    {
        var plan = Compile(Document());
        Assert.Equal([1L, 2L], Ids(Execute(plan, [new(1, ObservationValue.Null, 5), new(2, ObservationValue.Undefined, 8)])));
    }

    [Fact]
    public void BestTieFailsButTiesAmongLosingRowsDoNot()
    {
        var plan = Compile(Document(tieBreaker: false));
        Candidate[] tied = [new(1, S("a"), 8), new(2, S("a"), 8)];
        var failed = Execute(plan, tied);
        Assert.Equal(RelationQueryExecutionStatus.Failed, failed.Status);
        Assert.Contains(failed.Diagnostics, diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.ExecutionRepresentativeAmbiguous);
        Assert.Equal([3L], Ids(Execute(plan, [.. tied, new(3, S("a"), 9)])));
        Assert.Equal([3L], Ids(Execute(plan, [new(3, S("a"), 9), .. tied])));
    }

    [Fact]
    public void EmptyInputAndGlobalPartitionHaveExplicitCardinality()
    {
        var plan = Compile(Document(global: true));
        Assert.Empty(Ids(Execute(plan, [])));
        Assert.Equal([2L], Ids(Execute(plan, [new(1, S("a"), 5), new(2, S("b"), 8)])));
    }

    [Fact]
    public void GlobalSelectionKeepsRootOccurrencesSeparate()
    {
        var query = Assert.IsType<QueryDefinition>(Document(global: true, tieBreaker: false).Definition);
        var output = new QueryNodeId("root-output");
        var outputBinding = new ValueBindingId("output");
        var body = new LogicalQueryDefinition([.. query.Body.Nodes,
            new ProjectQueryNode(output, query.Body.Nodes[^1].Id, outputBinding, RepresentativeSelectionFixture.Shape,
                [new(new("id"), Id, Expr.Field(Binding, Id)),
                 new(new("key"), Key, Expr.Field(Binding, Key)),
                 new(new("preference"), Preference, Expr.Field(Binding, Preference)),
                 new(new("eligible"), Eligible, Expr.Field(Binding, Eligible))])]);
        var relation = new Cohesive.Relations.IR.RelationDefinition(
            new("root-representative"), new("RootRepresentative"), body, Binding,
            new(output, RepresentativeSelectionFixture.Shape, RelationOutputMode.OnePerRoot, Expr.Field(outputBinding, Id)));
        var plan = Compile(RelationQueryDocument.FromDefinition(relation));
        var result = Execute(plan, [new(1, S("a"), 8), new(2, S("a"), 8)]);
        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        var rows = Assert.IsType<RelationQueryRelationResult>(result.Relation).Rows;
        Assert.Equal([1L, 2L], rows.Select(row => row.Value.Fields![nameof(Candidate.Id)].Int64).Order());
        Assert.All(rows, row => Assert.Equal(row.Root!.Id, Assert.Single(row.InputOccurrences).Id));
    }

    [Fact]
    public void FilteringAwayATiedGroupDoesNotHideAnAmbiguousSelection()
    {
        var plan = Compile(Document(tieBreaker: false, filterAfter: true));
        var result = Execute(plan, [new(1, S("a"), 8, Eligible: false), new(2, S("a"), 8, Eligible: false)]);
        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.ExecutionRepresentativeAmbiguous);
    }

    [Fact]
    public void InvalidOrderingAndPartitionExpressionsAreRejectedBeforeExecution()
    {
        var query = Assert.IsType<QueryDefinition>(Document().Definition);
        var selected = Assert.IsType<SelectRepresentativeQueryNode>(query.Body.Nodes.Single(node => node.Id == Selection));
        Assert.Throws<ArgumentException>(() => new SelectRepresentativeQueryNode(Selection, Source, [], []));
        Assert.Throws<ArgumentException>(() => new SelectRepresentativeQueryNode(Selection, Source, [null!], selected.Orderings));
        SelectRepresentativeQueryNode[] invalid =
        [
            selected with { Orderings = default },
            selected with { Orderings = [null!] },
            selected with { Orderings = [selected.Orderings[0] with { Direction = (QuerySortDirection)99 }] },
            selected with { Orderings = [selected.Orderings[0] with { NullPlacement = (QueryNullPlacement)99 }] },
            selected with { Keys = [null!] },
            selected with { Keys = [Expr.Field(new ValueBindingId("unbound"), Key)] }
        ];
        foreach (var node in invalid)
        {
            var changed = query with { Body = new LogicalQueryDefinition(
                [.. query.Body.Nodes.Select(existing => existing.Id == Selection ? node : existing)]) };
            Assert.False(RelationQueryDefinitionValidator.Validate(changed).IsValid);
        }
    }

    [Fact]
    public void RealizationExplicitlyRequiresRepresentativeSelection()
    {
        var plan = Compile(Document());
        var report = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        Assert.True(report.IsRealizable);
        Assert.Contains(report.Requirements, requirement => requirement.Capability is LogicalRelationQueryCapability
            { Kind: RelationQueryLogicalCapabilityKind.SelectRepresentative });
    }

    [Fact]
    public void NarrowOutputDemandStillRetainsSelectionKeysAndOrdering()
    {
        var compiled = RelationQueryStaticCompiler.Compile(new(Document(), Shapes,
            demand: RelationQueryCompilationDemand.ForQueryResults(
                [QueryResultDemand.SelectedFields(new("rows"),
                    [new RelationQueryFieldReference(RepresentativeSelectionFixture.Shape, Eligible)])])));
        Assert.True(compiled.IsSuccessful);
        var plan = compiled.Plan!;
        var result = Execute(plan, [new(1, S("a"), 5), new(2, S("a"), 8, Eligible: false)]);
        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        var winner = Assert.Single(Assert.Single(result.QueryResults).Rows);
        Assert.Equal("candidate/2", Assert.Single(winner.InputOccurrences).Id.Value);
        Assert.Equal(ObservationValue.FromBool(false), winner.Value.Fields![nameof(Candidate.Eligible)]);
        var selection = plan.ExecutionSlice.Nodes.Single(node => node.Id == Selection);
        Assert.Single(selection.RepresentativeKeys);
        Assert.Equal(2, selection.OrderKeys.Length);
    }

    [Fact]
    public void UnavailablePreferenceDoesNotAuthorizeACompleteWinner()
    {
        var plan = Compile(Document());
        var evidence = Evidence(plan, [new(1, S("a"), 5), new(2, S("a"), 8)]);
        var preference = plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>()
            .Single(input => input.Field.Path == Preference);
        var incomplete = new RelationQueryRuntimeEvidence(evidence.Evaluation, plan,
            sources: evidence.Sources, capabilities: evidence.Capabilities,
            fields: [.. evidence.Fields.Where(field => field.Input != preference.Id || field.Owner.Value != "candidate/2")]);
        var result = RelationQueryInMemoryInterpreter.Default.Execute(new(plan, incomplete));
        Assert.NotEqual(RelationQueryExecutionStatus.Succeeded, result.Status);
    }

    [Fact]
    public void PortableRoundTripAndFingerprintRetainSelectionAndOrdering()
    {
        var document = Document();
        var json = RelationQueryJsonSerializer.Serialize(document);
        var roundTrip = RelationQueryJsonSerializer.Deserialize(json);
        Assert.Equal(json, RelationQueryJsonSerializer.Serialize(roundTrip));
        Assert.Equal(RelationQueryDefinitionFingerprinter.Compute(document.Definition),
            RelationQueryDefinitionFingerprinter.Compute(roundTrip.Definition));
        Assert.NotEqual(RelationQueryDefinitionFingerprinter.Compute(document.Definition),
            RelationQueryDefinitionFingerprinter.Compute(Document(direction: QuerySortDirection.Ascending).Definition));
        Assert.Contains("selectRepresentative", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedAuthoringLowersKeysOrderAndSourceAttribution()
    {
        var author = RelationQuery.Expression();
        var source = author.Source<AuthoringCandidate>();
        var winner = author.SelectRepresentative(source.Node, row => row.Key, source.Binding,
            [author.Ordering(row => row.Preference, source.Binding, QuerySortDirection.Descending),
             author.Ordering(row => row.Id, source.Binding)], sourceReference: "preferred-candidate");
        var query = author.BuildQuery(new("authored-representative"), new("AuthoredRepresentative"),
            author.Rows(winner, source.Binding));
        Assert.True(query.Validation.IsValid);
        Assert.Contains(query.Provenance.Sources, decision => decision.Target == winner.Id.Value
            && decision.Source.Reference == "preferred-candidate");
        var actual = Assert.IsType<SelectRepresentativeQueryNode>(query.Definition.Body.Nodes.Single(node => node.Id == winner.Id));
        var expected = new SelectRepresentativeQueryNode(winner.Id, source.Node.Id,
            [Expr.Field(source.Binding.Id, FieldPath.FromField(nameof(AuthoringCandidate.Key)))],
            [new(Expr.Field(source.Binding.Id, FieldPath.FromField(nameof(AuthoringCandidate.Preference))), QuerySortDirection.Descending),
             new(Expr.Field(source.Binding.Id, FieldPath.FromField(nameof(AuthoringCandidate.Id))))]);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected, RelationQueryJsonSerializer.CreateOptions()),
            System.Text.Json.JsonSerializer.Serialize(actual, RelationQueryJsonSerializer.CreateOptions()));
    }

    static ObservationValue S(string value) => ObservationValue.FromString(value);
    static RelationQueryExecutionResult Execute(CompiledRelationQueryPlan plan, IReadOnlyList<Candidate> rows) =>
        RelationQueryInMemoryInterpreter.Default.Execute(new(plan, Evidence(plan, rows)));
    static long[] Ids(RelationQueryExecutionResult result)
    {
        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        return [.. Assert.Single(result.QueryResults).Rows.Select(row => row.Value.Fields![nameof(Candidate.Id)].Int64)];
    }
    public sealed record AuthoringCandidate(long Id, string Key, long? Preference);
}
