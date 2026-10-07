using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Tests;

public sealed class SubplanCompilationTests
{
    [Fact]
    public void Semantic_failure_retains_original_structured_diagnostics()
    {
        var request = new RelationQueryCompilationRequest(SubplanFixture.Definition.CompilationRequest.DefinitionDocument);
        var error = Assert.Throws<RelationQueryPreparationException>(() =>
            RelationQuerySubplan.Compile(request, SubplanFixture.DemandProjection));
        Assert.Same(request, error.Compilation.Request);
        Assert.False(error.Compilation.IsSuccessful);
        Assert.NotEmpty(error.Compilation.Diagnostics);
        Assert.Null(error.Physical);
        Assert.IsAssignableFrom<Cohesive.Prelude.PreparationException>(error);
        Assert.Equal("relationQuery.preparation.semantic", error.Code);
    }

    [Fact]
    public void Derived_ids_are_distinct_deterministic_and_keep_original_provenance()
    {
        var request = SubplanFixture.Definition.CompilationRequest;
        var plan = RelationQuerySubplan.Compile(request, SubplanFixture.DemandProjection);
        var again = RelationQuerySubplan.Compile(request, SubplanFixture.DemandProjection);
        Assert.Same(request, plan.Original.Request);
        Assert.Equal(3, new[] { ((QueryDefinition)plan.Original.Plan!.Definition).Id, ((QueryDefinition)plan.Prefix.Plan!.Definition).Id,
            ((QueryDefinition)plan.Remainder.Plan!.Definition).Id }.Distinct().Count());
        Assert.Equal(plan.Remainder.Request.DefinitionDocument.DefinitionFingerprint,
            again.Remainder.Request.DefinitionDocument.DefinitionFingerprint);
    }

    [Fact]
    public void Interior_source_cannot_feed_another_branch()
    {
        var q = RelationQuery.Expression();
        var id = q.Parameter<string>("id");
        var source = q.Where(q.Source<SubplanStock>(), row => row.Sku == id.Value);
        var projected = q.Project(source.Node, (SubplanStock row) => new SubplanStock(row.Sku, row.Available), source.Binding);
        var joined = q.Join(projected.Node, source.Node, JoinKind.Inner,
            (left, right) => left.Sku == right.Sku, projected.Binding, source.Binding);
        var result = q.Project(joined, (SubplanStock row) => new SubplanStock(row.Sku, row.Available), projected.Binding);
        var definition = result.BuildArrayQuery(id: new("branch"), name: new("Branch"), parameter: id);
        var error = Assert.Throws<RelationQueryPreparationException>(() => RelationQuerySubplan.Compile(definition.CompilationRequest, projected.Node.Id));
        Assert.Equal("relationQuery.subplan.interiorEscapes", error.Code);
        Assert.True(error.Compilation.IsSuccessful);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("page")]
    public void Unsupported_ancestors_rejected_by_subplan_compilation(string kind)
    {
        var request = SubplanFixture.Definition.CompilationRequest;
        var query = (QueryDefinition)request.DefinitionDocument.Definition;
        var cut = query.Body.Nodes.OfType<ProjectQueryNode>().Single(node => node.Id == SubplanFixture.DemandProjection);
        LogicalQueryNode unsupported = kind == "page"
            ? new PageQueryNode(new("unsupported"), cut.Input, new OffsetPageDefinition(10))
            : new OrderQueryNode(new("unsupported"), cut.Input, [new(cut.Assignments[0].Value)]);
        var changed = query with { Body = new([.. query.Body.Nodes.Select(node => node.Id == cut.Id
            ? cut with { Input = unsupported.Id } : node), unsupported], query.Body.Parameters) };
        var modified = new RelationQueryCompilationRequest(RelationQueryDocument.FromDefinition(changed),
            request.ShapeDocuments, request.RelationshipCatalogDocument);
        var error = Assert.Throws<RelationQueryPreparationException>(() => RelationQuerySubplan.Compile(modified, cut.Id));
        Assert.Equal("relationQuery.subplan.ancestorUnsupported", error.Code);
    }

    [Fact]
    public void Aggregation_ancestor_is_rejected()
    {
        var q = RelationQuery.Expression();
        var id = q.Parameter<string>("id");
        var source = q.Where(q.Source<SubplanStock>(), row => row.Sku == id.Value);
        var aggregate = q.Aggregate<FilterQueryNode, CountRow>(source.Node, result => result.Count(row => row.Count));
        var cut = q.Project(aggregate.Node, (CountRow row) => new CountRow(row.Count), aggregate.Binding);
        var terminal = q.Project(cut.Node, (CountRow row) => new CountRow(row.Count), cut.Binding);
        var error = Assert.Throws<ArgumentException>(() =>
            terminal.BuildArrayQuery(id: new("count"), name: new("Count"), parameter: id));
        Assert.Contains("relationQuery.query.rowsResultNodeInvalid", error.Message);
    }
    [Fact]
    public void Aggregate_terminal_reaches_result_kind_rejection_after_successful_compilation()
    {
        var query = RelationQuery.Expression();
        var source = query.Source<SubplanStock>();
        var aggregate = query.Aggregate<SourceQueryNode, CountRow>(source.Node, result => result.Count(row => row.Count));
        var definition = query.BuildQuery(new("count-result"), new("CountResult"), query.Aggregation(aggregate));
        var request = new RelationQueryCompilationRequest(definition.CreateDocument(), query.ShapeDocuments);
        var error = Assert.Throws<RelationQueryPreparationException>(() => RelationQuerySubplan.Compile(request, aggregate.Node.Id));
        Assert.True(error.Compilation.IsSuccessful);
        Assert.Equal("relationQuery.subplan.resultUnsupported", error.Code);
    }

    public sealed record CountRow(long Count);
    [Fact]
    public void Hidden_binding_cannot_escape_projected_interface()
    {
        var request = SubplanFixture.Definition.CompilationRequest;
        var query = (QueryDefinition)request.DefinitionDocument.Definition;
        var cut = query.Body.Nodes.OfType<ProjectQueryNode>().Single(node => node.Id == SubplanFixture.DemandProjection);
        var terminal = query.Body.Nodes.OfType<ProjectQueryNode>().Single(node => node.Id == query.Results[0].Input);
        // The canonical projection already hides its input bindings. Preserve that
        // validation instead of inventing a second binding environment at the cut.
        var changed = terminal with { Assignments = terminal.Assignments.SetItem(0,
            terminal.Assignments[0] with { Value = cut.Assignments[0].Value }) };
        var leaked = query with { Body = new([.. query.Body.Nodes.Select(node => node.Id == terminal.Id ? changed : node)], query.Body.Parameters) };
        var error = Assert.Throws<ArgumentException>(() => RelationQueryDocument.FromDefinition(leaked));
        Assert.Contains("bindingMissing", error.Message);
    }
}
