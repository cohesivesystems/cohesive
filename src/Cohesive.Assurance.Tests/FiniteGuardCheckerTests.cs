using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Authoring;
using Cohesive.Model;

namespace Cohesive.Assurance.Tests;

public sealed class FiniteGuardCheckerTests
{
    public sealed record Amount(decimal Value);
    public sealed record Qualification(bool OriginalResolved, int RemainingBlockers);
    static ClaimReference R(string name) => new(new("finite-test://" + name), "1", name);
    static readonly Qualification[] Domain =
        [new(false, 0), new(false, 1), new(false, 2), new(true, 0), new(true, 1), new(true, 2)];

    static (ClaimDocument Claim, ExpressionInput<Qualification> Input) Contract()
    {
        var input = new ExpressionAuthoring().Input<Qualification>("qualification");
        var claim = Claim.Define("reactivate.all-gates", "1", R("reactivate"), R("mapping"), R("claim-source"), R("claim-config"))
            .ForEachCommitted(R("reactivate"))
            .Require(input, ClaimInputRole.Before, q => q.RemainingBlockers == 0).Build();
        return (claim, input);
    }
    static FiniteGuardCheck.Source<Qualification> Plan(bool repaired = false, IReadOnlyList<Qualification>? domain = null)
    {
        var (claim, input) = Contract();
        var declaration = FiniteGuardCheck.Define(claim, input, domain ?? Domain, R("guard-source"), R("finite-config"));
        return repaired ? declaration.When(q => q.RemainingBlockers == 0) : declaration.When(q => q.OriginalResolved);
    }
    static ClaimAssessment Assess(ClaimEvidence evidence) => ClaimAssessment.Evaluate(evidence.Claim,
        evidence.Subject, R("policy"), [new(evidence.Kind, evidence.Method, evidence.Configuration)],
        [evidence], DateTimeOffset.UnixEpoch);

    [Fact]
    public void DefectiveGuardFindsAnAdmissibleWitnessAndRepairCompletesOnlyTheDeclaredCatalog()
    {
        var faulty = FiniteGuardChecker.Check(Plan().Build());
        Assert.Equal(AssuranceAttemptStatus.Completed, faulty.Status);
        Assert.Equal(ClaimVerdict.Violated, faulty.Verdict);
        Assert.Equal(ClaimVerdict.Satisfied, faulty.NonVacuity);
        Assert.False(faulty.EnumerationComplete);
        var witness = faulty.Plan.Members[faulty.CounterexampleIndex!.Value].Deserialize<Qualification>()!;
        Assert.True(witness.OriginalResolved);
        Assert.True(witness.RemainingBlockers > 0);
        Assert.Equal(ProofState.Disproven, Assess(faulty.ToEvidence()).State);

        var repaired = FiniteGuardChecker.Check(Plan(repaired: true).Build());
        Assert.Equal(ClaimVerdict.Satisfied, repaired.Verdict);
        Assert.Equal(ClaimVerdict.Satisfied, repaired.NonVacuity);
        Assert.True(repaired.EnumerationComplete);
        Assert.Equal(Domain.Length, repaired.Visited);
        Assert.Null(repaired.CounterexampleIndex);
        var evidence = repaired.ToEvidence();
        Assert.Equal(EvidenceKind.ExhaustiveCheck, evidence.Kind);
        Assert.Equal(EvidenceCoverage.RestrictedScope, evidence.Coverage);
        Assert.Equal(EvidenceTargetKind.Model, evidence.Subject.Kind);
        Assert.Equal(ProofObligation.Model, evidence.Subject.Obligation);
        Assert.Equal(repaired.Plan.Claim.Definition.Selection, evidence.Subject.Scope);
        Assert.Equal(ProofState.Conditional, Assess(evidence).State);
        Assert.Contains(repaired.Plan.Reference(), evidence.Residuals);
        Assert.Equal(faulty.Plan.Claim.Fingerprint, repaired.Plan.Claim.Fingerprint);
        Assert.NotEqual(faulty.Plan.Reference(), repaired.Plan.Reference());
    }

    [Fact]
    public void MemberBudgetNeverTurnsAnIncompleteSearchIntoSuccess()
    {
        var result = FiniteGuardChecker.Check(Plan(repaired: true).Build(), maxMembers: 4);
        Assert.Equal(AssuranceAttemptStatus.BudgetExceeded, result.Status);
        Assert.Equal(ClaimVerdict.Inconclusive, result.Verdict);
        Assert.Equal(ClaimVerdict.Satisfied, result.NonVacuity);
        Assert.False(result.EnumerationComplete);
        Assert.Equal(4, result.Visited);
        Assert.Equal(ProofState.Unknown, Assess(result.ToEvidence()).State);
        Assert.Equal(result.Serialize(), FiniteGuardCheckResult.Recheck(result.Serialize()).Serialize());
        Assert.Throws<ArgumentOutOfRangeException>(() => FiniteGuardChecker.Check(result.Plan, maxMembers: 0));
    }

    [Fact]
    public void AssumptionsFilterMembershipAndRemainAnExplicitResidual()
    {
        var plan = Plan().Assuming(q => q.RemainingBlockers == 0).Build();
        var result = FiniteGuardChecker.Check(plan);
        Assert.True(result.EnumerationComplete);
        Assert.Equal(ClaimVerdict.Satisfied, result.Verdict);
        Assert.Equal(ClaimVerdict.Satisfied, result.NonVacuity);
        Assert.Equal(ProofState.Conditional, Assess(result.ToEvidence()).State);
        Assert.NotEqual(Plan().Build().Reference(), plan.Reference());
        Assert.Contains(plan.SourceMap, x => x.Path == "/assumptions" && x.Source == plan.Source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyDomainOrUnreachableGuardIsVacuousAndCannotEstablishProof(bool empty)
    {
        var plan = empty ? Plan(domain: []).Build() : Plan().Assuming(q => q.RemainingBlockers < 0).Build();
        var result = FiniteGuardChecker.Check(plan);
        Assert.Equal(ClaimVerdict.Satisfied, result.Verdict);
        Assert.Equal(ClaimVerdict.Violated, result.NonVacuity);
        Assert.True(result.EnumerationComplete);
        Assert.Equal(ClaimVerdict.Inconclusive, result.ToEvidence().Verdict);
        Assert.Equal(ProofState.Unknown, Assess(result.ToEvidence()).State);
    }

    [Fact]
    public void CancellationIsRecordedWithoutProofAndCanBeRerunExplicitly()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = FiniteGuardChecker.Check(Plan(repaired: true).Build(), cancellationToken: cancellation.Token);
        Assert.Equal(AssuranceAttemptStatus.Cancelled, result.Status);
        Assert.Equal(ClaimVerdict.Inconclusive, result.NonVacuity);
        Assert.Equal(0, result.Visited);
        Assert.Equal(ProofState.Unknown, Assess(ClaimEvidence.Deserialize(result.ToEvidence().Serialize())).State);
        Assert.Throws<JsonException>(() => FiniteGuardCheckResult.Recheck(result.Serialize()));
        Assert.Equal(ClaimVerdict.Satisfied, FiniteGuardChecker.Check(result.Plan).Verdict);
    }

    [Fact]
    public void UndefinedArithmeticIsUnsupportedAndCannotProduceProof()
    {
        var input = new ExpressionAuthoring().Input<Amount>("amount");
        var claim = Claim.Define("amount", "1", R("amount"), R("mapping"), R("source"), R("config"))
            .AtEveryCommittedState(R("amounts"), InitialStateSelection.Include)
            .Require(input, ClaimInputRole.State, a => a.Value >= 0m).Build();
        var plan = FiniteGuardCheck.Define(claim, input, new Amount[] { new(0m) }, R("source"), R("config"))
            .When(a => 1m / a.Value > 0m).Build();
        var result = FiniteGuardChecker.Check(plan);
        Assert.Equal(AssuranceAttemptStatus.Unsupported, result.Status);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(ClaimVerdict.Inconclusive, result.Verdict);
        Assert.Equal(ProofState.Unknown, Assess(result.ToEvidence()).State);
    }

    [Fact]
    public void SourceCatalogIsCopiedAndCanonicalRestorationPreservesAllMembers()
    {
        var rows = Domain.ToArray();
        var source = Plan(domain: rows);
        rows[4] = new(true, 0);
        var first = source.Build();
        var restored = FiniteGuardCheckDocument.Deserialize(first.Serialize());
        Assert.Equal(first.Reference(), restored.Reference());
        Assert.Equal(first.Serialize(), source.Build().Serialize());
        var result = FiniteGuardChecker.Check(restored);
        Assert.Equal(new Qualification(true, 1), result.Plan.Members[result.CounterexampleIndex!.Value].Deserialize<Qualification>());
        Assert.Equal(result.Serialize(), FiniteGuardCheckResult.Recheck(result.Serialize()).Serialize());
        var missing = JsonNode.Parse(first.Serialize())!;
        missing.AsObject().Remove("members");
        Assert.Throws<JsonException>(() => FiniteGuardCheckDocument.Deserialize(missing.ToJsonString()));
    }

    [Theory]
    [InlineData("verdict")]
    [InlineData("counterexampleIndex")]
    [InlineData("visited")]
    [InlineData("schema")]
    public void RetainedOutcomesAndWitnessesAreIndependentlyChecked(string field)
    {
        var result = FiniteGuardChecker.Check(Plan().Build());
        var json = JsonNode.Parse(result.Serialize())!;
        if (field == "verdict") json[field] = "Satisfied";
        else if (field == "schema") json[field] = "future";
        else json[field] = 0;
        Assert.Throws<JsonException>(() => FiniteGuardCheckResult.Recheck(json.ToJsonString()));
    }

    [Fact]
    public void RestoredClaimsRetainTypedBindingCompatibilityAndInvalidMembersAreRejected()
    {
        var (claim, input) = Contract();
        var restored = ClaimDocument.Deserialize(claim.Serialize());
        var plan = FiniteGuardCheck.Define(restored, input, Domain, R("source"), R("config"))
            .When(q => q.RemainingBlockers == 0).Build();
        Assert.Equal(ClaimVerdict.Satisfied, FiniteGuardChecker.Check(plan).Verdict);
        Assert.Throws<ArgumentException>(() => FiniteGuardCheck.Define(claim, input,
            new Qualification[] { null! }, R("source"), R("config")).When(q => true).Build());
    }

    [Fact]
    public void UnsupportedFormsBindingMismatchAndCapturedHostLogicAreRejected()
    {
        var (claim, input) = Contract();
        var other = new ExpressionAuthoring().Input<Qualification>("other");
        Assert.Throws<ArgumentException>(() => FiniteGuardCheck.Define(claim, other, Domain, R("source"), R("config")));
        var author = Claim.Define("temporal", "1", R("event"), R("mapping"), R("source"), R("config"));
        var temporal = author.RequireWithin(R("trigger"), R("outcome"), TimeSpan.FromSeconds(1))
            .Require(Expr.Const(true), [new(input.Id, ClaimInputRole.Trigger, new(input.Type)),
                new(new("witness"), ClaimInputRole.Witness, new(input.Type))]).Build();
        Assert.Throws<NotSupportedException>(() => FiniteGuardCheck.Define(temporal, input, Domain, R("source"), R("config")));
        var limit = new { Value = 2 };
        Assert.Throws<ExpressionAuthoringException>(() => FiniteGuardCheck.Define(claim, input, Domain, R("source"), R("config"))
            .When(q => q.RemainingBlockers < limit.Value));
    }
}
