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
        Assert.Equal(cases.Candidates.Length, cases.Inputs.Distinct().Count());
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
        Assert.ThrowsAny<NotSupportedException>(() => TransitionAssurance.Prepare(model)
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
        var error = Assert.ThrowsAny<NotSupportedException>(() => TransitionAssurance.Prepare(Model(unsupported: true))
            .GenerateInputs(state, new(0, "")));
        Assert.Contains("compound", error.Message);
        var probe = TransitionAssurance.Prepare(Model());
        Assert.Throws<InvalidOperationException>(() => probe.GenerateInputs(state, new(0, ""), maxInputs: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => probe.GenerateInputs(state, new(0, ""), maxInputs: 0));
    }
    [Fact]
    public void OfflineExecutionDoesNotRequireAnAssuranceRunner()
    {
        var model = Model();
        var direct = Cohesive.Transitions.Execution.OfflineTransition.Prepare(model);
        var probe = TransitionAssurance.Prepare(model);
        foreach (var quantity in new[] { 0, 3, 8 })
        {
            var state = new Stock("sku", 7, 4, false);
            var a = direct.Execute(state, new(quantity, ""), new("case"));
            var b = probe.Execute(state, new(quantity, ""), new("case"));
            Assert.Equal(a.State, b.State);
            Assert.Equal(a.Outcome, b.Outcome);
            Assert.Equal(a.Decision.Kind, b.Decision.Kind);
        }
    }

    [Fact]
    public void BoundsAreExplicitAndDeduplicationPreservesAllMotivatingNodes()
    {
        var cases = TransitionAssurance.Prepare(Model()).GenerateInputs(new("sku", 4, 4, false),
            new(1, "seed"), minimum: 1, maximum: 5);
        Assert.All(cases.Inputs, input => Assert.InRange(input.Quantity, 1, 5));
        var exact = Assert.Single(cases.Candidates.Where(x => x.Input.Quantity == 4));
        Assert.Contains(exact.MotivatingNodes, x => x.Value == "limit");
        Assert.Contains(exact.MotivatingNodes, x => x.Value == "stock");
        Assert.NotEmpty(cases.CoverageLimits);
        Assert.Throws<ArgumentOutOfRangeException>(() => TransitionAssurance.Prepare(Model())
            .GenerateInputs(new("sku", 4, 4, false), new(0, ""), minimum: 1));
    }

    public sealed record BooleanRequest(bool Enabled, int Other);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BooleanInputGuardsProposeBothValues(bool negate)
    {
        var model = TransitionAuthoring.Create<Stock, BooleanRequest, bool>(
            ObjectEntityDefinition.For<Stock>(new("stock")).Shape, new("boolean"), new("1"), t =>
            {
                if (negate) t.Requires(new("flag"), (s, r) => !r.Enabled, (s, r) => false);
                else t.Requires(new("flag"), (s, r) => r.Enabled, (s, r) => false);
                t.Return(new("accepted"), TransitionOutcomeDisposition.Applied, true);
            });
        var result = TransitionAssurance.Prepare(model).GenerateInputs(new("sku", 4, 4, false), new(false, 17));
        Assert.Equal(new[] { false, true }, result.Inputs.Select(x => x.Enabled));
        Assert.All(result.Candidates, c =>
        {
            Assert.Equal(17, c.Input.Other);
            Assert.Equal("flag", Assert.Single(c.MotivatingNodes).Value);
        });
    }

    [Fact]
    public void UnsupportedDiagnosticsAreStructuredAndAttributed()
    {
        var error = Assert.Throws<TransitionInputGenerationException>(() =>
            TransitionAssurance.Prepare(Model(unsupported: true)).GenerateInputs(new("sku", 4, 4, false), new(0, "")));
        Assert.Equal("assurance.inputs.unsupported", error.Code);
        Assert.Equal("compound", error.Node.Value);
    }

    [Theory]
    [InlineData(TransitionOutcomeDisposition.NoChange, TransitionDecisionKind.NoChange)]
    [InlineData(TransitionOutcomeDisposition.DomainRejected, TransitionDecisionKind.DomainRejected)]
    public void TypedOfflineExecutionRetainsExplicitNoChangeAndRejection(TransitionOutcomeDisposition disposition, TransitionDecisionKind kind)
    {
        var model = TransitionAuthoring.Create<Stock, Request, string>(
            ObjectEntityDefinition.For<Stock>(new("stock")).Shape, new("decision"), new("1"),
            t => t.Return(new("outcome"), disposition, "authored"));
        var before = new Stock("sku", 7, 4, true);
        var result = Cohesive.Transitions.Execution.OfflineTransition.Prepare(model).Execute(before, new(3, ""), new("case"));
        Assert.Equal(kind, result.Decision.Kind);
        Assert.Equal(before, result.State);
        Assert.Equal("authored", result.Outcome);
    }

}
