using System.Text.Json.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Tests.Relations;

public sealed class DomainEntityCompositionTests
{
    public sealed record Parent([property: JsonPropertyName("id")] string Id);
    public sealed record Child([property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("parentId")] string ParentId);
    public sealed record Row { public string ParentId { get; init; } = ""; public string? ChildId { get; init; } }

    [Fact]
    public void Separate_relationship_is_captured_when_traversed_and_reuses_entity_graphs()
    {
        var domain = new DomainModelBuilder();
        var parents = domain.Entity<Parent>("parent");
        var children = domain.Entity<Child>("child");
        var relationship = children.References(child => child.ParentId, parents);
        var snapshot = domain.Build();
        Assert.Same(parents.Definition, snapshot.Entities[0]);
        Assert.Throws<ArgumentException>(() => domain.Entity<Parent>("parent"));
        Assert.Throws<ArgumentException>(() => children.References(child => child.ParentId.ToUpper(), parents));
        var author = RelationQuery.Expression();
        var parentShape = parents.QueryShape(author);
        children.QueryShape(author);
        Assert.Equal(parents.Definition.StateShape.QualifiedId, parentShape.Id);
        var source = author.Source(parentShape);
        var related = author.TraverseInverse(source.Node, source.Binding, relationship);
        var rows = author.Project(related.Node,
            (Parent parent, Child child) => new Row { ParentId = parent.Id, ChildId = child.Id },
            source.Binding, related.Binding);
        var query = author.BuildQuery(new("test/domain-join"), new("DomainJoin"), author.Rows(rows));
        Assert.Same(relationship.Definition, Assert.Single(author.RelationshipCatalog.Relationships));
        var compilation = RelationQueryStaticCompiler.Compile(new(query.CreateDocument(), author.ShapeDocuments,
            author.CreateRelationshipCatalogDocument()));
        Assert.True(compilation.IsSuccessful, string.Join("; ", compilation.Diagnostics));
        Assert.Equal("parentId", relationship.Definition.SourceReference.ToString());
    }

    [Fact]
    public void Typed_query_captures_rows_and_rejects_foreign_or_additional_parameters()
    {
        var author = RelationQuery.Expression();
        var source = author.Source(author.Clr.Shape<Parent>());
        var parameter = author.Parameter<string>("id");
        var filtered = author.Where(source, parent => parent.Id == parameter.Value);
        var rows = author.Project(filtered, parent => new { parent.Id });
        var typed = author.BuildQuery(new("typed"), new("Typed"), rows, parameter,
            result: values => values.Count == 0 ? null : values[0].Id);
        Assert.Null(typed.AssembleResult([]));
        Assert.Equal("one", typed.AssembleResult([Cohesive.Model.ObservationValue.FromObject(new { Id = "one" })]));
        var foreign = RelationQuery.Expression().Parameter<string>("id");
        Assert.Throws<ArgumentException>(() => author.BuildQuery(new("foreign"), new("Foreign"), rows, foreign, values => values.Count));
        var extra = author.Parameter<string>("extra");
        Assert.Throws<ArgumentException>(() => author.BuildQuery(new("wrong"), new("Wrong"), rows, extra, values => values.Count));
        var further = author.Where(filtered, parent => parent.Id == extra.Value);
        var moreRows = author.Project(further, parent => new { parent.Id });
        Assert.Throws<ArgumentException>(() => author.BuildQuery(new("extra"), new("Extra"), moreRows, parameter, values => values.Count));
    }
}
