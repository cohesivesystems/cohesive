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
