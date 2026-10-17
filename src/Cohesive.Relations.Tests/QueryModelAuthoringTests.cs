using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class QueryModelAuthoringTests
{
    public sealed record Item(string Id, int Quantity);
    public sealed record Row(string Id);
    public sealed record Detail(string Id, IReadOnlyList<Row> Children);

    static RelationQueryExpressionBoundNode<ProjectQueryNode, Row> Rows(
        RelationQueryExpressionAuthoring query, RelationQueryExpressionParameter<string> id) =>
        query.Source<Item>().Where(item => item.Id == id.Value).Select(item => new Row(item.Id));

    [Fact]
    public void Model_array_uses_the_same_canonical_query_as_direct_authoring()
    {
        var builder = new QueryModelBuilder();
        var declared = builder.WithInput<string>("id").Array("items", Rows, name: "Items");
        var session = RelationQuery.Expression();
        var parameter = session.Parameter<string>("id");
        var direct = Rows(session, parameter).BuildArrayQuery(new("items"), new("Items"), parameter);
        Assert.Equal(direct.CompilationRequest.DefinitionDocument.DefinitionFingerprint,
            declared.CompilationRequest.DefinitionDocument.DefinitionFingerprint);
        Assert.Same(declared.CompilationRequest, Assert.Single(builder.Definition));
    }

    [Fact]
    public void Nested_key_mappings_preserve_fingerprint_and_reuse_identity_slots()
    {
        var model = new QueryModelBuilder();
        var declared = model.WithInput<string>("id").SingleOrDefault<Detail>("details", (q, id, output) =>
        {
            var rows = q.Source<Item>().Where(item => item.Id == id.Value);
            return output.From(rows).Key(rows, item => item.Id, detail => detail.Id)
                .Collection(detail => detail.Children, rows, item => item.Id, child => child.Id, _ => { });
        }, name: "Details");
        var author = RelationQuery.Expression();
        var parameter = author.Parameter<string>("id");
        var source = author.Source<Item>().Where(item => item.Id == parameter.Value);
        var direct = author.SingleOrDefault<Detail>().From(source, source, item => item.Id)
            .Identity(detail => detail.Id)
            .Collection(detail => detail.Children, source, item => item.Id, child => child.Identity(row => row.Id))
            .Build(new("details"), new("Details"), parameter);
        Assert.Equal(direct.CompilationRequest.DefinitionDocument.DefinitionFingerprint,
            declared.CompilationRequest.DefinitionDocument.DefinitionFingerprint);
        var definition = (QueryDefinition)declared.CompilationRequest.DefinitionDocument.Definition;
        Assert.Equal(2, definition.Body.Nodes.OfType<ProjectQueryNode>().Single().Assignments.Length);
        Assert.Null(declared.AssembleResult([]));
    }

    [Fact]
    public void Failed_recipe_does_not_reserve_identity_or_append_partial_registration()
    {
        var model = new QueryModelBuilder();
        var input = model.WithInput<string>("id");
        Assert.Throws<InvalidOperationException>(() => input.Array<ProjectQueryNode, Row>("items", (_, _) => throw new InvalidOperationException("recipe")));
        var valid = input.Array("items", Rows);
        Assert.Same(valid.CompilationRequest, Assert.Single(model.Definition));
    }

    [Fact]
    public void Duplicate_and_frozen_registration_do_not_invoke_the_recipe()
    {
        var model = new QueryModelBuilder();
        var input = model.WithInput<string>("id");
        input.Array("items", Rows);
        var calls = 0;
        RelationQueryExpressionBoundNode<ProjectQueryNode, Row> Recipe(RelationQueryExpressionAuthoring q, RelationQueryExpressionParameter<string> id)
        { calls++; return Rows(q, id); }
        Assert.Throws<ArgumentException>(() => input.Array("items", Recipe));
        _ = model.Definition;
        Assert.Throws<InvalidOperationException>(() => input.Array("another", Recipe));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Recipe_cannot_freeze_or_contribute_to_its_model_reentrantly()
    {
        var model = new QueryModelBuilder();
        model.WithInput<string>("id").Array("items", (q, id) =>
        {
            Assert.Throws<InvalidOperationException>(() => model.Definition);
            Assert.Throws<InvalidOperationException>(() => model.WithInput<string>("id").Array("other", Rows));
            return Rows(q, id);
        });
        Assert.Single(model.Definition);
    }

    [Fact]
    public void Foreign_result_session_is_rejected_without_registration()
    {
        var model = new QueryModelBuilder();
        var other = RelationQuery.Expression();
        var rows = Rows(other, other.Parameter<string>("id"));
        Assert.Throws<ArgumentException>(() => model.WithInput<string>("id").Array("foreign", (_, _) => rows));
        Assert.Empty(model.Definition);
    }

    [Fact]
    public void Nested_recipe_rejects_foreign_result_builder_without_registering_query()
    {
        var model = new QueryModelBuilder();
        var foreign = RelationQuery.Expression();
        var source = foreign.Source<Item>();
        var mapping = foreign.SingleOrDefault<Detail>().From(source).Key(source, item => item.Id, detail => detail.Id);
        Assert.Throws<ArgumentException>(() => model.WithInput<string>("id")
            .SingleOrDefault<Detail>("foreign", (_, _, _) => mapping));
        Assert.Empty(model.Definition);
    }

    [Fact]
    public void Key_mapping_rejects_foreign_parent_and_invalid_target()
    {
        var query = RelationQuery.Expression();
        var source = query.Source<Item>();
        var foreign = RelationQuery.Expression().Source<Item>();
        var rows = query.SingleOrDefault<Detail>().From(source);
        Assert.Throws<ArgumentException>(() => rows.Key(foreign, item => item.Id, detail => detail.Id));
        Assert.Throws<ArgumentException>(() => rows.Key(source, item => item.Id, detail => detail.Id + "invalid"));
    }
}
