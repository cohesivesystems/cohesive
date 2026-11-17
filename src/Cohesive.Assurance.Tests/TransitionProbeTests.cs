using Cohesive.Model.Authoring;
using Cohesive.Transitions.Assurance;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;

namespace Cohesive.Assurance.Tests;

public sealed class TransitionProbeTests
{
    public sealed record Stock(string Sku, int Available, int Limit, bool Quarantined);
    public sealed record Request(int Quantity, string Note);

    static Transition<Stock, Request, string> Model(bool subtract = true, bool unsupported = false) =>
        TransitionAuthoring.Create<Stock, Request, string>(ObjectEntityDefinition.For<Stock>(new("stock")).Shape,
            new("reserve"), new("1"), t =>
            {
                t.Requires(new("positive"), (s, r) => r.Quantity > 0, (s, r) => "invalid");
                t.Requires(new("quarantine"), (s, r) => !s.Quarantined, (s, r) => "quarantine");
                t.Requires(new("limit"), (s, r) => r.Quantity <= s.Limit, (s, r) => "limit");
                t.Requires(new("stock"), (s, r) => s.Available >= r.Quantity, (s, r) => "short");
                if (unsupported)
                    t.Requires(new("compound"), (s, r) => r.Quantity > 1 && r.Quantity < 9, (s, r) => "compound");
                if (subtract) t.Set(new("decrement"), s => s.Available, (s, r) => s.Available - r.Quantity);
                t.Return(new("reserved"), TransitionOutcomeDisposition.Applied, "reserved");
            });

    [Fact]
    public void BoundariesComeFromProductionGuardsAndRetainOtherInputFields()
    {
        var model = Model();
        var probe = TransitionAssurance.Prepare(model);
        var cases = probe.GenerateInputs(new("sku", 7, 4, false), new(0, "preserved"));
        Assert.Equal(model.Reference, cases.Reference);
        Assert.Equal(new[] { int.MinValue, -1, 0, 1, 3, 4, 5, 6, 7, 8, int.MaxValue },
            cases.Inputs.Select(x => x.Quantity).Order());
        Assert.All(cases.Inputs, input => Assert.Equal("preserved", input.Note));
        Assert.Equal(new[] { "positive", "quarantine", "limit", "stock" }, cases.InspectedNodes.Select(x => x.Value));
        Assert.True(cases.Inputs.SequenceEqual(probe.GenerateInputs(new("sku", 7, 4, false), new(0, "preserved")).Inputs));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ExtremeThresholdsDoNotWrap(int limit)
    {
        var cases = TransitionAssurance.Prepare(Model()).GenerateInputs(new("sku", limit, limit, false), new(0, ""));
        Assert.Contains(cases.Inputs, x => x.Quantity == limit);
        Assert.Contains(cases.Inputs, x => x.Quantity == (limit == int.MinValue ? limit + 1 : limit - 1));
        Assert.Equal(cases.Inputs.Length, cases.Inputs.Distinct().Count());
    }

    [Theory]
    [InlineData(0, "invalid", 7)]
    [InlineData(5, "limit", 7)]
    [InlineData(3, "reserved", 4)]
    public void TypedExecutionPreservesDomainOutcomes(int quantity, string outcome, int remaining)
    {
        var before = new Stock("sku", 7, 4, false);
        var result = TransitionAssurance.Prepare(Model()).Execute(before, new(quantity, ""), new("case"));
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(before with { Available = remaining }, result.State);
        Assert.Equal(7, before.Available);
    }

    [Fact]
    public void NoChangeIsObservableAndIndependentAssertionCanDetectOmittedEffect()
    {
        var before = new Stock("sku", 7, 4, false);
        var result = TransitionAssurance.Prepare(Model(subtract: false)).Execute(before, new(3, ""), new("case"));
        Assert.Equal(TransitionDecisionKind.NoChange, result.Decision.Kind);
        Assert.Equal("reserved", result.Outcome);
        Assert.NotEqual(before.Available - 3, result.State.Available);
    }

    public sealed record WideRequest(long Amount);

    [Fact]
    public void Int64InputIsNotSilentlyNarrowedWhenItsSeedFitsInt32()
    {
        var model = TransitionAuthoring.Create<Stock, WideRequest, bool>(
            ObjectEntityDefinition.For<Stock>(new("stock")).Shape, new("wide"), new("1"),
            t => t.Requires(new("positive"), (s, r) => r.Amount > 0L, (s, r) => false)
                .Return(new("accepted"), TransitionOutcomeDisposition.Applied, true));
        Assert.Throws<NotSupportedException>(() => TransitionAssurance.Prepare(model)
            .GenerateInputs(new("sku", 7, 4, false), new(0)));
    }

    [Fact]
    public void NoGuardsReturnsOnlySeedWithoutInventingCoverage()
    {
        var model = TransitionAuthoring.Create<Stock, Request, bool>(
            ObjectEntityDefinition.For<Stock>(new("stock")).Shape, new("unguarded"), new("1"),
            t => t.Return(new("accepted"), TransitionOutcomeDisposition.Applied, true));
        var cases = TransitionAssurance.Prepare(model).GenerateInputs(new("sku", 7, 4, false), new(2, "seed"));
        Assert.Equal(new Request(2, "seed"), Assert.Single(cases.Inputs));
        Assert.Empty(cases.InspectedNodes);
    }

    [Fact]
    public void UnsupportedAndBudgetExhaustionNeverReturnPartialSuccess()
    {
        var state = new Stock("sku", 7, 4, true);
        var error = Assert.Throws<NotSupportedException>(() => TransitionAssurance.Prepare(Model(unsupported: true))
            .GenerateInputs(state, new(0, "")));
        Assert.Contains("compound", error.Message);
        var probe = TransitionAssurance.Prepare(Model());
        Assert.Throws<InvalidOperationException>(() => probe.GenerateInputs(state, new(0, ""), maxInputs: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => probe.GenerateInputs(state, new(0, ""), maxInputs: 0));
    }
}
