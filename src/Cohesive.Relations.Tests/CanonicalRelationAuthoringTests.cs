using Cohesive.Execution;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Tests;

public sealed class CanonicalRelationAuthoringTests
{
    [Fact]
    public void CreateRelation_ProjectsExactTypedCanonicalHandleWithoutRetainingAuthoringState()
    {
        var author = RelationQuery.Expression();
        var input = author.Source<SourceValue>();
        var output = author.Project(input, source => new ResultValue
        {
            Id = source.Id,
            Label = source.Value
        });
        var authored = output.BuildRelation(
            result => result.Id,
            id: new("relation/tests/typed"),
            name: new("Typed relation"));

        var relation = author.CreateRelation(
            authored,
            input,
            output,
            new("7"),
            new(origin: DocumentOrigin.Generated, producer: "tests", producerVersion: "1"));

        Assert.True(relation.IsValid);
        Assert.Same(relation.Document.Definition, relation.Definition);
        Assert.Equal("relation/tests/typed", relation.Reference.DefinitionId.Value);
        Assert.Equal("7", relation.Reference.RevisionId.Value);
        Assert.Equal(relation.Document.DefinitionFingerprint.Value, relation.Reference.Fingerprint.Value);
        var typeMapper = new DefaultClrTypeRefMapper();
        Assert.Equal(typeMapper.Map(typeof(SourceValue), null), relation.InputContract.Type);
        Assert.Equal(typeMapper.Map(typeof(ResultValue), null), relation.ResultContract.Type);
        Assert.Equal(
            author.ShapeDocuments.Select(static document => document.Graph.Id),
            relation.ShapeDocuments.Select(static document => document.Graph.Id));
        Assert.Equal(
            RelationshipCatalogFingerprinter.Compute(author.RelationshipCatalog),
            relation.RelationshipCatalog.CatalogFingerprint);
    }

    [Fact]
    public void CreateRelation_RejectsTypedHandlesThatDoNotOwnCanonicalEndpoints()
    {
        var author = RelationQuery.Expression();
        var input = author.Source<SourceValue>();
        var output = author.Project(input, source => new ResultValue
        {
            Id = source.Id,
            Label = source.Value
        });
        var otherInput = author.Source<OtherSourceValue>();
        var otherOutput = author.Project(input, source => new OtherResultValue { Id = source.Id });
        var authored = output.BuildRelation(
            result => result.Id,
            id: new("relation/tests/endpoints"),
            name: new("Endpoint validation"));

        var inputFailure = Assert.Throws<ArgumentException>(() => author.CreateRelation(
            authored,
            otherInput,
            output,
            new("1")));
        Assert.Contains("does not identify the canonical root", inputFailure.Message, StringComparison.Ordinal);

        var resultFailure = Assert.Throws<ArgumentException>(() => author.CreateRelation(
            authored,
            input,
            otherOutput,
            new("1")));
        Assert.Contains("does not identify the canonical output", resultFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRelation_RejectsNonSingularOutputCardinality()
    {
        var author = RelationQuery.Expression();
        var input = author.Source<SourceValue>();
        var output = author.Project(input, source => new ResultValue
        {
            Id = source.Id,
            Label = source.Value
        });
        var authored = output.BuildRelation(
            result => result.Id,
            mode: RelationOutputMode.ManyPerRoot,
            id: new("relation/tests/many"),
            name: new("Many relation"));

        var failure = Assert.Throws<ArgumentException>(() => author.CreateRelation(
            authored,
            input,
            output,
            new("1")));

        Assert.Contains(nameof(RelationOutputMode.OnePerRoot), failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RelationOutputMode.ManyPerRoot), failure.Message, StringComparison.Ordinal);
    }

    public sealed class SourceValue
    {
        public required string Id { get; init; }

        public required string Value { get; init; }
    }

    public sealed class ResultValue
    {
        public required string Id { get; init; }

        public required string Label { get; init; }
    }

    public sealed class OtherSourceValue
    {
        public required string Id { get; init; }
    }

    public sealed class OtherResultValue
    {
        public required string Id { get; init; }
    }
}
