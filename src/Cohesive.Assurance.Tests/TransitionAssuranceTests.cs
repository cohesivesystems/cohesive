using System.Collections.Immutable;
using Cohesive.Authoring;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Assurance.Tests;

public sealed class TransitionAssuranceTests
{
    public sealed record Inventory(decimal OnHand, decimal Reserved);
    public sealed record Reserve(decimal Quantity);

    static ClaimReference Fixture(string id) => new(new("fixture://" + id), "1", "fixture:" + id);

    [Theory]
    [InlineData(3, ClaimVerdict.Satisfied)]
    [InlineData(12, ClaimVerdict.Violated)]
    public void AuthoredTransitionCandidateIsCheckedAgainstIndependentStockClaim(int quantity, ClaimVerdict verdict)
    {
        var entity = ObjectEntityDefinition.For<Inventory>(new("inventory"));
        // Deliberately no stock guard: the independent claim must catch over-reservation.
        var transition = TransitionAuthoring.Create<Inventory, Reserve, bool>(entity.Shape,
            new("inventory/reserve"), new("1"), t => t
                .Set(new("reserve"), state => state.Reserved, (state, input) => state.Reserved + input.Quantity)
                .Return(new("applied"), TransitionOutcomeDisposition.Applied, true));
        var compilation = transition.Compile();
        Assert.True(compilation.IsSuccessful, string.Join(";", compilation.Validation.Diagnostics));
        var plan = compilation.Plan!;
        var before = ObservationValue.FromObject(new Inventory(10m, 0m));
        var command = ObservationValue.FromObject(new Reserve(quantity));
        var decision = TransitionReferenceInterpreter.DecideFullState(plan, new("reserve-1"),
            PortableValue.Concrete(plan.Definition.Input, command), PortableValue.Concrete(plan.Definition.Observation, before));
        var after = TransitionStateProjector.Apply(before, decision);

        var reference = ClaimReference.FromExecution(transition.Reference);
        Assert.Equal(reference, ClaimReference.FromExecution(plan.DefinitionReference));
        var inputs = new ExpressionAuthoring();
        var b = inputs.Input<Inventory>("before");
        var c = inputs.Input<Reserve>("command");
        var a = inputs.Input<Inventory>("after");
        var contract = Fixture("reference-model-observations");
        var claim = Claim.Define("stock.safe", "1", reference, contract, Fixture("source"), Fixture("config"))
            .ForEachCommitted(reference)
            .Require(b, c, a, (old, input, current) => current.Reserved <= current.OnHand).Build();

        // This fixture declares a reference-model commit. DecideFullState itself never commits;
        // a production adapter must wait for an authoritative commit receipt before emitting this.
        var observation = new SemanticObservation("commit-1", "inventory-1", 1, 1, reference,
            SemanticObservationKind.Commit, ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty
                .Add(ClaimInputRole.Before, before).Add(ClaimInputRole.Command, command).Add(ClaimInputRole.After, after));
        var batch = new TraceObservationBatch(new(contract, Fixture("single-aggregate-scope"), [new(reference, ["inventory-1"])]),
            reference, Fixture("epoch"), [new(Fixture("alternative"), [observation],
                [new(reference, "inventory-1", 1, 1, ObservationClosure.Closed, Fixture("closure"))])],
            EnumerationCompleteness.Complete, Fixture("enumeration"));
        var result = TraceClaimEvaluator.Evaluate(claim, batch);
        Assert.Equal(TraceEvaluationStatus.Completed, result.Status);
        Assert.Equal(verdict, result.Verdict);
        Assert.Equal(result.Serialize(), TraceEvaluation.Reevaluate(result.Serialize()).Serialize());
    }

    [Fact]
    public void ExecutionReferenceRetainsFingerprintProfileAndEscapesSeparators()
    {
        ExecutionDefinitionReference Ref(string algorithm, string profile) => new(new("reserve"), new("1"), new(algorithm, profile, "digest"));
        Assert.NotEqual(ClaimReference.FromExecution(Ref("a:b", "c")), ClaimReference.FromExecution(Ref("a", "b:c")));
        Assert.Equal(Ref("sha256", "profile/v1").Fingerprint.ToCanonicalString(),
            ClaimReference.FromExecution(Ref("sha256", "profile/v1")).Fingerprint);
        Assert.NotEqual(ClaimReference.FromExecution(Ref("sha256", "v1")), ClaimReference.FromExecution(Ref("sha256", "v2")));
    }
}
