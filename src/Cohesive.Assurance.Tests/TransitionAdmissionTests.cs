using Cohesive.Authoring;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Assurance;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Assurance.Tests;

public sealed class TransitionAdmissionTests
{
    public sealed record Driver(bool Active, bool OriginalResolved, int RemainingBlockers);
    static readonly Cohesive.Transitions.Model.EntityDefinition Entity = ObjectEntityDefinition.For<Driver>(new("driver"));
    static ClaimReference R(string name) => new(new("admission-test://" + name), "1", name);
    static readonly Driver[] Catalog =
    [
        new(false, false, 1), new(false, true, 0),
        new(false, true, 1), new(false, true, 2)
    ];

    static ExpressionInput<Driver> Before()
    {
        var author = new ExpressionAuthoring();
        var graph = ShapeGraphDocument.FromGraph(new ShapeGraph(new("driver-model"), [Entity.Shape]));
        return author.Input(author.Clr.Shape<Driver>(graph, new(graph.Graph.Id, Entity.Shape.Id)), "before");
    }

    static Transition<Driver, bool, bool> Candidate(bool repaired) =>
        TransitionAuthoring.Create<Driver, bool, bool>(
            Entity.Shape,
            new("driver/reactivate"), new(repaired ? "2" : "1"), t =>
            {
                if (repaired)
                    t.Requires(new("activation-eligibility"), (driver, command) => driver.RemainingBlockers == 0,
                        (driver, command) => false);
                else
                    t.Requires(new("activation-eligibility"), (driver, command) => driver.OriginalResolved,
                        (driver, command) => false);
                t.Set(new("activate"), driver => driver.Active, true)
                    .Return(new("activated"), TransitionOutcomeDisposition.Applied, true);
            });

    static ClaimDocument Contract(Transition<Driver, bool, bool> transition, ExpressionInput<Driver> before)
    {
        var reference = ClaimReference.FromExecution(transition.Reference);
        return Claim.Define("activation.requires-no-blockers", "1", reference,
                R("observations"), R("policy"), R("claim-config"))
            .ForEachCommitted(reference)
            .Require(before, ClaimInputRole.Before, d => d.RemainingBlockers == 0)
            .Build();
    }

    static FiniteGuardCheckDocument Bind(Transition<Driver, bool, bool> transition, IReadOnlyList<Driver>? catalog = null)
    {
        var before = Before();
        return TransitionAssurance.CheckAdmission(transition, Contract(transition, before), before,
            catalog ?? Catalog, R("catalog"), R("transition-admission/v1")).Build();
    }

    static TransitionDecision Execute(Transition<Driver, bool, bool> transition, Driver state)
    {
        var compilation = transition.Compile();
        Assert.True(compilation.IsSuccessful, string.Join("; ", compilation.Validation.Diagnostics));
        var plan = compilation.Plan!;
        return TransitionReferenceInterpreter.DecideFullState(plan, new("activate-1"),
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromBool(true)),
            PortableValue.Concrete(plan.Definition.Observation, ObservationValue.FromObject(state)));
    }

    [Fact]
    public void AdmissionDifferenceWitnessExecutesDifferentlyUnderTheActualCandidates()
    {
        var faulty = Candidate(repaired: false);
        var repaired = Candidate(repaired: true);
        var comparison = FiniteComparison.Compare(Bind(faulty), Bind(repaired), FiniteComparisonTarget.Admission);
        Assert.Equal(ClaimVerdict.Satisfied, comparison.NewlyForbidden);
        Assert.Equal(ClaimVerdict.Violated, comparison.NewlyPermitted);
        var restored = FiniteComparisonResult.Recheck(comparison.Serialize());
        var witness = restored.OldCandidate.Members[restored.NewlyForbiddenIndex!.Value].Deserialize<Driver>()!;
        Assert.Equal(new Driver(false, true, 1), witness);
        Assert.Equal(TransitionDecisionKind.Applied, Execute(faulty, witness).Kind);
        Assert.Equal(TransitionDecisionKind.AdmissionRejected, Execute(repaired, witness).Kind);
    }

    [Fact]
    public void ActualTransitionAdmissionFindsFaultAndRepairChangesBothCheckingAndExecution()
    {
        var faulty = Candidate(repaired: false);
        var repaired = Candidate(repaired: true);
        var failure = FiniteGuardChecker.Check(Bind(faulty));
        var success = FiniteGuardChecker.Check(Bind(repaired));
        Assert.Equal(ClaimVerdict.Violated, failure.Verdict);
        var witness = failure.Plan.Members[failure.CounterexampleIndex!.Value].Deserialize<Driver>()!;
        Assert.Equal(new Driver(false, true, 1), witness);
        Assert.Equal(ClaimVerdict.Satisfied, success.Verdict);
        Assert.True(success.EnumerationComplete);
        Assert.Equal(ClaimVerdict.Satisfied, success.NonVacuity);

        var badDecision = Execute(faulty, witness);
        var repairedDecision = Execute(repaired, witness);
        Assert.Equal(TransitionDecisionKind.Applied, badDecision.Kind);
        Assert.Equal(TransitionDecisionKind.AdmissionRejected, repairedDecision.Kind);
        var actual = TransitionStateProjector.Apply(ObservationValue.FromObject(witness), badDecision).Deserialize<Driver>()!;
        Assert.True(actual.Active);
        Assert.Equal(1, actual.RemainingBlockers);
        Assert.Equal(TransitionDecisionKind.Applied, Execute(repaired, new(false, true, 0)).Kind);

        // Same policy expression; the targeted transition revision and derived guard change.
        Assert.Equal(failure.Plan.Claim.Definition.Predicate, success.Plan.Claim.Definition.Predicate);
        Assert.NotEqual(failure.Plan.Guard, success.Plan.Guard);
        Assert.NotEqual(failure.Plan.Reference(), success.Plan.Reference());
        foreach (var result in new[] { failure, success })
        {
            var restored = FiniteGuardCheckResult.Recheck(result.Serialize());
            Assert.Equal(result.Serialize(), restored.Serialize());
            var report = restored.ToEvidence();
            var assessment = ClaimAssessment.Evaluate(report, R("acceptance"),
                [EvidenceAcceptance.Of(report)], DateTimeOffset.UnixEpoch);
            Assert.Equal(result == failure ? ProofState.Disproven : ProofState.Conditional, assessment.State);
            var transition = result == failure ? faulty : repaired;
            Assert.Contains(TransitionAssurance.Profile, report.Dependencies);
            Assert.Contains(ClaimReference.FromExecution(transition.Reference), report.Dependencies);
            Assert.Contains(ClaimReference.FromExecution(transition.Reference, new("activation-eligibility")), report.Dependencies);
            Assert.Equal(ProofState.Unknown, ClaimAssessment.Evaluate(report, R("acceptance"),
                [EvidenceAcceptance.Of(report)], DateTimeOffset.UnixEpoch,
                unavailableDependencies: [ClaimReference.FromExecution(transition.Reference)]).State);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FiniteAdmissionMatchesTheInterpreterForEveryCatalogMember(bool repaired)
    {
        var transition = Candidate(repaired);
        foreach (var state in Catalog)
        {
            var check = FiniteGuardChecker.Check(Bind(transition, [state]));
            var decision = Execute(transition, state);
            Assert.Equal(decision.Kind == TransitionDecisionKind.Applied, check.EnabledWitnessIndex is not null);
        }
    }

    [Fact]
    public void AllAdmissionRulesAreCombinedInOrderAndMappedToTheirOriginalNodes()
    {
        var transition = TransitionAuthoring.Create<Driver, bool, bool>(
            Entity.Shape,
            new("driver/reactivate"), new("3"), t => t
                .Requires(new("original"), (d, command) => d.OriginalResolved, (d, command) => false)
                .Requires(new("all-gates"), (d, command) => d.RemainingBlockers == 0, (d, command) => false)
                .Set(new("activate"), d => d.Active, true)
                .Return(new("activated"), TransitionOutcomeDisposition.Applied, true));
        var plan = Bind(transition);
        Assert.Equal(ClaimVerdict.Satisfied, FiniteGuardChecker.Check(plan).Verdict);
        Assert.Contains(plan.SourceMap, x => x.Path == "/guard/left"
            && x.Source == ClaimReference.FromExecution(transition.Reference, new("original")));
        Assert.Contains(plan.SourceMap, x => x.Path == "/guard/right"
            && x.Source == ClaimReference.FromExecution(transition.Reference, new("all-gates")));
        foreach (var state in Catalog)
            Assert.Equal(Execute(transition, state).Kind == TransitionDecisionKind.Applied,
                FiniteGuardChecker.Check(Bind(transition, [state])).EnabledWitnessIndex is not null);
    }

    [Fact]
    public void OrderedAdmissionShortCircuitsBeforeAnUndefinedLaterPredicate()
    {
        var transition = TransitionAuthoring.Create<Driver, bool, bool>(Entity.Shape,
            new("driver/ordered"), new("1"), t => t
                .Requires(new("nonzero"), (d, command) => d.RemainingBlockers != 0, (d, command) => false)
                .Requires(new("division"), (d, command) => 1m / d.RemainingBlockers > 0m, (d, command) => false)
                .Return(new("accepted"), TransitionOutcomeDisposition.Applied, true));
        var state = new Driver(false, true, 0);
        var result = FiniteGuardChecker.Check(Bind(transition, [state]));
        Assert.Equal(AssuranceAttemptStatus.Completed, result.Status);
        Assert.Equal(ClaimVerdict.Violated, result.NonVacuity);
        Assert.Equal(TransitionDecisionKind.AdmissionRejected, Execute(transition, state).Kind);
    }

    [Fact]
    public void NoAdmissionRulesMeanTrueRatherThanVacuousOrMissingGuard()
    {
        var transition = TransitionAuthoring.Create<Driver, bool, bool>(
            Entity.Shape,
            new("driver/reactivate"), new("unguarded"), t => t
                .Set(new("activate"), d => d.Active, true)
                .Return(new("activated"), TransitionOutcomeDisposition.Applied, true));
        var result = FiniteGuardChecker.Check(Bind(transition));
        Assert.Equal(Expr.Const(true), result.Plan.Guard);
        Assert.Equal(ClaimVerdict.Violated, result.Verdict);
        Assert.Equal(ClaimVerdict.Satisfied, result.NonVacuity);
    }

    [Fact]
    public void CommandDependentGuardIsRejectedEvenInAShortCircuitedBranch()
    {
        var transition = TransitionAuthoring.Create<Driver, bool, bool>(
            Entity.Shape,
            new("driver/reactivate"), new("command"), t => t
                .Requires(new("command-dependent"), (d, command) => d.OriginalResolved || command, (d, command) => false)
                .Return(new("activated"), TransitionOutcomeDisposition.Applied, true));
        var exception = Assert.Throws<NotSupportedException>(() => Bind(transition));
        Assert.Contains("command-dependent", exception.Message);
        Assert.Contains("transition.input", exception.Message);
    }

    [Fact]
    public void ClaimCannotSilentlyBindToAnotherTransitionRevision()
    {
        var before = Before();
        var claim = Contract(Candidate(repaired: false), before);
        Assert.Throws<ArgumentException>(() => TransitionAssurance.CheckAdmission(Candidate(repaired: true), claim, before,
            Catalog, R("catalog"), R("config")));
    }

    [Fact]
    public void DefaultClrNamedInputCannotBeAssumedEquivalentToTheInlineObservation()
    {
        var transition = Candidate(repaired: true);
        var before = new ExpressionAuthoring().Input<Driver>("before");
        Assert.Throws<ArgumentException>(() => TransitionAssurance.CheckAdmission(transition, Contract(transition, before),
            before, Catalog, R("catalog"), R("config")));
    }

    [Fact]
    public void SubjectCreationCannotBeMisreadAsAnExistingEntityPreState()
    {
        var transition = TransitionAuthoring.Create<Driver, bool, bool>(Entity.Shape,
            new("driver/create"), new("1"), t => t
                .CreatesFrom(new("initialize"), command => new Driver(false, false, 1))
                .Return(new("created"), TransitionOutcomeDisposition.Applied, true));
        Assert.Throws<NotSupportedException>(() => Bind(transition));
    }

    [Fact]
    public void CorePackagesDoNotDependOnTheOptionalBridge()
    {
        Assert.DoesNotContain(typeof(Claim).Assembly.GetReferencedAssemblies(), x => x.Name!.Contains("Transitions"));
        Assert.DoesNotContain(typeof(Transition<,,>).Assembly.GetReferencedAssemblies(), x => x.Name!.Contains("Assurance"));
    }
}
