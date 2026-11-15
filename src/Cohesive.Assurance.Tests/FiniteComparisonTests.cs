using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Authoring;

namespace Cohesive.Assurance.Tests;

public sealed class FiniteComparisonTests
{
    public sealed record Qualification(bool OriginalResolved, decimal RemainingBlockers);
    static readonly Qualification[] Domain = [new(false, 0), new(true, 0), new(true, 1), new(true, 2)];
    static ClaimReference R(string id) => new(new("comparison-test://" + id), "1", id);
    static FiniteGuardCheckDocument Plan(Expression<Func<Qualification, bool>> guard,
        Expression<Func<Qualification, bool>>? policy = null, Expression<Func<Qualification, bool>>? assumption = null,
        IReadOnlyList<Qualification>? domain = null, string binding = "before", string mapping = "mapping")
    {
        var input = new ExpressionAuthoring().Input<Qualification>(binding);
        var claim = Claim.Define("activation", "1", R("subject"), R(mapping), R("policy"), R("configuration"))
            .ForEachCommitted(R("selection"))
            .Require(input, ClaimInputRole.Before, policy ?? (q => q.RemainingBlockers == 0)).Build();
        var source = FiniteGuardCheck.Define(claim, input, domain ?? Domain, R("domain"), R("config")).When(guard);
        if (assumption is not null) source = source.Assuming(assumption);
        return source.Build();
    }

    [Fact]
    public void FindsBothAdmissionDirectionsAndRechecksPinnedCandidates()
    {
        var old = Plan(q => q.OriginalResolved);
        var proposed = Plan(q => q.RemainingBlockers == 0, binding: "newBefore");
        var comparison = FiniteComparison.Compare(old, proposed, FiniteComparisonTarget.Admission);
        Assert.Equal(0, comparison.NewlyPermittedIndex);
        Assert.Equal(2, comparison.NewlyForbiddenIndex);
        Assert.Equal(ClaimVerdict.Satisfied, comparison.NewlyPermitted);
        Assert.Equal(ClaimVerdict.Satisfied, comparison.NewlyForbidden);
        Assert.False(comparison.EnumerationComplete);
        Assert.Equal(comparison.Serialize(), FiniteComparisonResult.Recheck(comparison.Serialize()).Serialize());
        var swapped = FiniteComparison.Compare(proposed, old, FiniteComparisonTarget.Admission);
        Assert.Equal(comparison.NewlyPermittedIndex, swapped.NewlyForbiddenIndex);
        Assert.Equal(comparison.NewlyForbiddenIndex, swapped.NewlyPermittedIndex);
        var forged = JsonNode.Parse(comparison.Serialize())!.AsObject();
        forged["newlyForbiddenIndex"] = 1;
        Assert.Throws<JsonException>(() => FiniteComparisonResult.Recheck(forged.ToJsonString()));
    }

    [Fact]
    public void RequirementComparisonIncludesPolicyAndApplicabilityWithoutConflatingAdmission()
    {
        var old = Plan(q => true, policy: q => q.OriginalResolved);
        var proposed = Plan(q => true);
        var requirements = FiniteComparison.Compare(old, proposed, FiniteComparisonTarget.Requirement);
        Assert.Equal(0, requirements.NewlyPermittedIndex);
        Assert.Equal(2, requirements.NewlyForbiddenIndex);
        var admission = FiniteComparison.Compare(old, proposed, FiniteComparisonTarget.Admission);
        Assert.True(admission.EnumerationComplete);
        Assert.Equal(ClaimVerdict.Violated, admission.NewlyPermitted);
        Assert.Equal(ClaimVerdict.Violated, admission.NewlyForbidden);

        var restricted = Plan(q => true, assumption: q => q.RemainingBlockers == 0);
        var assumptionChange = FiniteComparison.Compare(proposed, restricted, FiniteComparisonTarget.Requirement);
        Assert.Equal(2, assumptionChange.NewlyPermittedIndex); // Requirement becomes vacuous outside new applicability.
        Assert.Equal(ClaimVerdict.Violated, assumptionChange.NewlyForbidden);
    }

    [Fact]
    public void IncompleteSearchRetainsFoundWitnessButCannotEstablishAbsence()
    {
        var old = Plan(q => q.OriginalResolved);
        var proposed = Plan(q => q.RemainingBlockers == 0);
        var result = FiniteComparison.Compare(old, proposed, FiniteComparisonTarget.Admission, maxMembers: 1);
        Assert.Equal(AssuranceAttemptStatus.BudgetExceeded, result.Status);
        Assert.Equal(ClaimVerdict.Satisfied, result.NewlyPermitted);
        Assert.Equal(ClaimVerdict.Inconclusive, result.NewlyForbidden);
        Assert.Equal(result.Serialize(), FiniteComparisonResult.Recheck(result.Serialize()).Serialize());
        var cancelled = FiniteComparison.Compare(old, proposed, FiniteComparisonTarget.Admission,
            cancellationToken: new CancellationToken(canceled: true));
        Assert.Equal(ClaimVerdict.Inconclusive, cancelled.NewlyPermitted);
        Assert.Equal(ClaimVerdict.Inconclusive, cancelled.NewlyForbidden);
        Assert.Throws<JsonException>(() => FiniteComparisonResult.Recheck(cancelled.Serialize()));
    }

    [Fact]
    public void UnsupportedNewMeaningDoesNotBecomeADifferenceOrEquality()
    {
        var result = FiniteComparison.Compare(Plan(q => true), Plan(q => 1m / q.RemainingBlockers > 0m),
            FiniteComparisonTarget.Admission);
        Assert.Equal(AssuranceAttemptStatus.Unsupported, result.Status);
        Assert.Equal(ClaimVerdict.Inconclusive, result.NewlyPermitted);
        Assert.Equal(ClaimVerdict.Inconclusive, result.NewlyForbidden);
        Assert.Equal(result.Serialize(), FiniteComparisonResult.Recheck(result.Serialize()).Serialize());
    }

    [Fact]
    public void RequiresCommonCatalogAndObservationMapping()
    {
        var plan = Plan(q => true);
        Assert.Throws<ArgumentException>(() => FiniteComparison.Compare(plan,
            Plan(q => true, domain: Domain.Reverse().ToArray()), FiniteComparisonTarget.Admission));
        Assert.Throws<ArgumentException>(() => FiniteComparison.Compare(plan,
            Plan(q => true, mapping: "different-mapping"), FiniteComparisonTarget.Admission));
        Assert.Throws<ArgumentOutOfRangeException>(() => FiniteComparison.Compare(plan, plan, (FiniteComparisonTarget)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => FiniteComparison.Compare(plan, plan, FiniteComparisonTarget.Admission, maxMembers: 0));
        var empty = Plan(q => true, domain: []);
        var result = FiniteComparison.Compare(empty, empty, FiniteComparisonTarget.Requirement);
        Assert.True(result.EnumerationComplete);
        Assert.Equal(ClaimVerdict.Violated, result.NewlyPermitted);
        Assert.Equal(0, result.Visited);
    }
}
