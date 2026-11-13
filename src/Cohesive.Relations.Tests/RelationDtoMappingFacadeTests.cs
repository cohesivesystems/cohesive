using Cohesive.Authoring;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Tests;

public sealed class RelationDtoMappingFacadeTests
{
    [Fact]
    public async Task MapsPocoToRecordAndSharesTheCanonicalPreparedPlan()
    {
        var mapping = RelationDtoMapping.Create((Load x) => new Summary(x.Id, x.Status, x.Amount));
        var source = new Load("load-42", "Ready", 125m, "internal");
        Assert.Equal(new Summary("load-42", "Ready", 125m), await mapping.MapAsync(source));
        var outcome = await mapping.EvaluateAsync([source], new("facade/inspect"));
        Assert.True(outcome.IsSuccessful);
        Assert.NotEmpty(mapping.AuthoringProvenance.Sources);
        Assert.Same(mapping.CompilationRequest, outcome.Evaluation.Compilation);
        Assert.Same(mapping.Compilation.Plan, outcome.Compilation.Plan);
        var detailed = mapping.Mapper.Map(outcome.PhysicalExecution!);
        var row = Assert.Single(detailed.Rows);
        Assert.NotNull(row.Source.Root);
        Assert.Single(row.Source.InputOccurrences);
        Assert.Equal(new Summary("load-42", "Ready", 125m), row.Value);
        var json = RelationQueryJsonSerializer.Serialize(mapping.CompilationRequest.DefinitionDocument);
        Assert.Equal(json, RelationQueryJsonSerializer.Serialize(RelationQueryJsonSerializer.Deserialize(json)));
        var repeated = RelationDtoMapping.Create((Load x) => new Summary(x.Id, x.Status, x.Amount));
        Assert.Equal(mapping.Compilation.Plan!.Provenance.DefinitionFingerprint,
            repeated.Compilation.Plan!.Provenance.DefinitionFingerprint);
    }

    [Fact]
    public async Task PreservesBatchOrderDuplicatesAndIdentityFreeDtos()
    {
        var mapping = RelationDtoMapping.Create((Load x) => new Label(x.Status + "!"));
        var inputs = Enumerable.Range(0, 15).Select(index => new Load("same-id", "row-" + index, index, "unused")).ToArray();
        var batch = inputs.Concat([inputs[0]]).ToArray();
        var seen = 0;
        IEnumerable<Load> Once()
        {
            foreach (var value in batch) { seen++; yield return value; }
        }
        var output = await mapping.MapManyAsync(Once());
        Assert.Equal(batch.Length, seen);
        Assert.Equal(batch.Select(x => new Label(x.Status + "!")), output);
        Assert.Empty(await mapping.MapManyAsync([]));
    }

    [Fact]
    public async Task MatchesTheExpandedCanonicalPipeline()
    {
        var mapping = RelationDtoMapping.Create((Load x) => new Summary(x.Id, x.Status, x.Amount));
        var inputs = new[] { new Load("b", "Ready", 42m, "unused"), new Load("a", "Open", 20m, "unused") };
        var evaluation = new RelationQueryEvaluationBuilder(mapping.CompilationRequest, new("expanded"))
            .Supply(inputs, x => x.Id).Build();
        var outcome = await RelationQueryEvaluator.CreateSuppliedOnly().EvaluateAsync(evaluation);
        var detailed = mapping.Mapper.Map(outcome.PhysicalExecution!);
        Assert.True(detailed.IsSuccessful);
        Assert.Equal(detailed.Rows.Select(x => x.Value).OrderBy(x => x.Id),
            (await mapping.MapManyAsync(inputs)).OrderBy(x => x.Id));
    }

    [Fact]
    public async Task PreservesNullsAndNestedSourceReads()
    {
        var mapping = RelationDtoMapping.Create((Detail x) => new Label(x.Header.Text));
        Assert.Null((await mapping.MapAsync(new Detail(new Label(null)))).Text);
        Assert.Equal("Open", (await mapping.MapAsync(new Detail(new Label("Open")))).Text);
    }

    [Fact]
    public void RetainsExistingNestedDtoLimitationsAndStructuredDiagnostics()
    {
        var failure = Assert.Throws<RelationQueryPreparationException>(() =>
            RelationDtoMapping.Create((Container x) => new ContainerDto(
                new Label(x.Name), x.Items.Select(item => new Label(item.Status)).ToArray())));
        Assert.Equal("relationQuery.preparation.dto", failure.Code);
        Assert.True(failure.Compilation.IsSuccessful);
        Assert.NotEmpty(failure.DtoDiagnostics);
        Assert.All(failure.DtoDiagnostics, diagnostic => Assert.Equal("REL3308", diagnostic.Code));
    }

    [Fact]
    public async Task BoundsEnumerationAndCancelsBeforeReadingInputs()
    {
        var mapping = RelationDtoMapping.Create((Load x) => new Label(x.Status), maximumRows: 2);
        var seen = 0;
        IEnumerable<Load> Inputs()
        {
            while (true) { seen++; yield return new("a", "Ready", 1m, "unused"); }
        }
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.MapManyAsync(Inputs()));
        Assert.Equal(3, seen);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mapping.MapManyAsync(Inputs(), cancellation.Token));
        Assert.Equal(0, seen);
        await Assert.ThrowsAsync<ArgumentNullException>(() => mapping.MapAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => mapping.MapManyAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => mapping.MapManyAsync([null!]));
    }

    [Fact]
    public async Task RejectsInvalidDataAndDoesNotPublishPartialBatches()
    {
        var mapping = RelationDtoMapping.Create((Load x) => new Summary(x.Id, x.Status, x.Amount));
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.MapManyAsync([
            new("a", "Ready", 1m, "unused"), new("b", null!, 2m, "unused")]));
        Assert.Equal("Ready", (await mapping.MapAsync(new("c", "Ready", 3m, "unused"))).Status);
    }

    [Fact]
    public async Task SupportsConcurrentCallsWithoutSharingRuntimeInputs()
    {
        var mapping = RelationDtoMapping.Create((Load x) => new Label(x.Status));
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            mapping.MapAsync(new Load("same-id", index.ToString(), index, "unused"))));
        Assert.Equal(Enumerable.Range(0, 12).Select(index => new Label(index.ToString())), results);
    }

    [Fact]
    public void RejectsExecutableCallbacksDuringAuthoring()
    {
        Assert.Throws<ExpressionAuthoringException>(() =>
            RelationDtoMapping.Create((Load x) => new Label(Unsupported(x.Status))));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RelationDtoMapping.Create((Load x) => new Label(x.Status), maximumRows: 0));
    }

    static string Unsupported(string value) => throw new InvalidOperationException("Must never execute.");
    public sealed record Load(string Id, string Status, decimal Amount, string InternalNotes);
    public sealed record Summary(string Id, string Status, decimal Amount);
    public sealed record Label(string? Text);
    public sealed record Detail(Label Header);
    public sealed record Container(string? Name, Load[] Items);
    public sealed record ContainerDto(Label Header, Label[] Items);
}
