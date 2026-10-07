using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryFluentWhereTests
{
    [Fact]
    public void Fluent_and_session_filters_produce_identical_canonical_queries()
    {
        var fluent = Define(true);
        var session = Define(false);
        Assert.Equal(session.CompilationRequest.DefinitionDocument.DefinitionFingerprint,
            fluent.CompilationRequest.DefinitionDocument.DefinitionFingerprint);
        Assert.Equal(2, fluent.CompilationRequest.DefinitionDocument.Definition.Body.Nodes.OfType<FilterQueryNode>().Count());
    }

    [Fact]
    public void Foreign_parameters_are_still_rejected()
    {
        var query = RelationQuery.Expression();
        var foreign = RelationQuery.Expression().Parameter<string>("id");
        Assert.Throws<RelationQueryExpressionAuthoringException>(() =>
            query.Source<Row>().Where(row => row.Id == foreign.Value));
    }

    [Fact]
    public void Null_predicate_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => RelationQuery.Expression().Source<Row>().Where(null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fluent_join_projection_matches_explicit_authoring_and_checks_inputs(bool leftJoin)
    {
        Assert.Equal(Joined(false, leftJoin).CompilationRequest.DefinitionDocument.DefinitionFingerprint,
            Joined(true, leftJoin).CompilationRequest.DefinitionDocument.DefinitionFingerprint);
        var query = RelationQuery.Expression();
        var left = query.Source<Row>();
        RelationQueryExpressionBoundNode<SourceQueryNode, Row> missing = null!;
        Assert.Throws<ArgumentNullException>(() => left.Join(missing, (a, b) => a.Id == b.Id));
        var right = query.Source<Row>();
        Assert.Throws<ArgumentNullException>(() => left.Join(right, null!));
        Assert.Throws<ArgumentException>(() => left.Join(RelationQuery.Expression().Source<Row>(), (a, b) => a.Id == b.Id));

        static RelationQuery<string, Row[]> Joined(bool fluent, bool leftJoin)
        {
            var query = RelationQuery.Expression();
            var id = query.Parameter<string>("id");
            var left = query.Source<Row>().Where(row => row.Id == id.Value);
            var right = query.Source<Row>();
            var projected = fluent
                ? (leftJoin ? left.LeftJoin(right, (a, b) => a.Id == b.Id) : left.Join(right, (a, b) => a.Id == b.Id)).Select((a, b) => new Row(a.Id, b.Enabled))
                : query.Project(query.Join(left.Node, right.Node, leftJoin ? JoinKind.Left : JoinKind.Inner, (a, b) => a.Id == b.Id, left.Binding, right.Binding),
                    (Row a, Row b) => new Row(a.Id, b.Enabled), left.Binding, right.Binding);
            return query.BuildQuery(new("joined"), new("Joined"), projected, id, rows => rows.ToArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Traversal_selector_and_array_terminal_preserve_canonical_query(bool inverse)
    {
        var explicitQuery = Traversal(false, inverse);
        var fluent = Traversal(true, inverse);
        Assert.Equal(explicitQuery.CompilationRequest.DefinitionDocument.DefinitionFingerprint,
            fluent.CompilationRequest.DefinitionDocument.DefinitionFingerprint);
        Assert.Empty(fluent.Project([]));
        var rows = System.Collections.Immutable.ImmutableArray.Create(
            Cohesive.Model.ObservationValue.FromObject(new Pair("parent", "child")));
        Assert.Equal(explicitQuery.Project(rows), fluent.Project(rows));
    }

    static RelationQuery<string, Pair[]> Traversal(bool fluent, bool inverse)
    {
        var query = RelationQuery.Expression();
        var id = query.Parameter<string>("id");
        var relationship = query.Relationship<Child, Parent>(child => child.ParentId);
        RelationQueryExpressionBoundNode<ProjectQueryNode, Pair> result;
        if (inverse)
        {
            var parents = query.Source<Parent>().Where(parent => parent.Id == id.Value);
            if (fluent)
                result = parents.TraverseInverse(relationship, (parent, child) => new Pair(parent.Id, child.Id));
            else
            {
                var children = parents.TraverseInverse(relationship);
                result = query.Project(children.Node, (Parent parent, Child child) => new Pair(parent.Id, child.Id),
                    parents.Binding, children.Binding);
            }
        }
        else
        {
            var children = query.Source<Child>().Where(child => child.Id == id.Value);
            if (fluent)
                result = children.Traverse(relationship, (child, parent) => new Pair(parent.Id, child.Id));
            else
            {
                var parents = children.Traverse(relationship);
                result = query.Project(parents.Node, (Child child, Parent parent) => new Pair(parent.Id, child.Id),
                    children.Binding, parents.Binding);
            }
        }
        return fluent ? result.ToArray(id: new("traversal"), name: new("Traversal"), parameter: id)
            : query.BuildQuery(new("traversal"), new("Traversal"), result, id, rows => rows.ToArray());
    }

    [Fact]
    public void Fluent_terminals_and_selectors_retain_session_and_null_guards()
    {
        var query = RelationQuery.Expression();
        var rows = query.Source<Row>();
        Assert.Throws<ArgumentNullException>(() => rows.Select<Row>(null!));
        Assert.Throws<ArgumentNullException>(() => rows.ToArray<string>(new("q"), new("Q"), null!));
        Assert.Throws<ArgumentException>(() => rows.ToArray(new("q"), new("Q"), RelationQuery.Expression().Parameter<string>("id")));
        var children = query.Source<Child>();
        var relationship = query.Relationship<Child, Parent>(child => child.ParentId);
        Assert.Throws<ArgumentNullException>(() => children.Traverse<Parent, Pair>(relationship, null!));
        var parents = query.Source<Parent>();
        Assert.Throws<ArgumentNullException>(() => parents.TraverseInverse<Child, Pair>(relationship, null!));
    }

    public sealed record Parent(string Id);
    public sealed record Child(string Id, string ParentId);
    public sealed record Pair(string ParentId, string ChildId);

    static RelationQuery<string, Row[]> Define(bool fluent)
    {
        var query = RelationQuery.Expression();
        var id = query.Parameter<string>("id");
        var source = query.Source<Row>();
        var first = fluent ? source.Where(row => row.Id == id.Value) : query.Where(source, row => row.Id == id.Value);
        var second = fluent ? first.Where(row => row.Enabled) : query.Where(first, row => row.Enabled);
        Assert.Same(source.Binding, second.Binding);
        return query.BuildQuery(new("fluent-filter"), new("FluentFilter"), second, id, rows => rows.ToArray());
    }

    public sealed record Row(string Id, bool Enabled);
}
