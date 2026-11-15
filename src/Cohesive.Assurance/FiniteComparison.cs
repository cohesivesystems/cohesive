using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Assurance;

/// <summary>The meaning compared over a shared explicit catalog.</summary>
public enum FiniteComparisonTarget
{
    /// <summary>Compare admission, A ∧ G; the accepted claim predicate is not an admission rule.</summary>
    Admission,
    /// <summary>Compare the complete requirement, (A ∧ G) ⇒ P, including vacuous satisfaction outside applicability.</summary>
    Requirement
}

/// <summary>Finds directional distinguishing rows between exact finite candidates.</summary>
public static class FiniteComparison
{
    /// <summary>Versioned comparison interpretation, independent of proof and execution methods.</summary>
    public static ClaimReference Method { get; } = new(new("assurance-method://finite-comparison"), "1", "finite-comparison/v1");

    /// <summary>Searches ¬old ∧ new and old ∧ ¬new over the same ordered catalog.</summary>
    /// <param name="oldCandidate">Exact earlier plan, including domain, assumptions, guard, policy and provenance.</param>
    /// <param name="newCandidate">Exact proposed plan with the same catalog, input contract and observation mapping.</param>
    /// <param name="target">Whether to compare admission or the full implication requirement.</param>
    /// <param name="maxMembers">Positive maximum number of shared rows visited.</param>
    /// <param name="cancellationToken">Stops search without claiming absence of a witness.</param>
    /// <returns>Both directional witnesses when found; absence is definitive only after complete enumeration.</returns>
    /// <exception cref="ArgumentNullException">A candidate is null.</exception>
    /// <exception cref="ArgumentException">Candidates lack a common catalog, input contract, form, mapping or external assumptions.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Target is unknown or the row budget is nonpositive.</exception>
    public static FiniteComparisonResult Compare(FiniteGuardCheckDocument oldCandidate,
        FiniteGuardCheckDocument newCandidate, FiniteComparisonTarget target,
        int maxMembers = 100_000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldCandidate);
        ArgumentNullException.ThrowIfNull(newCandidate);
        if (!Enum.IsDefined(target)) throw new ArgumentOutOfRangeException(nameof(target));
        if (maxMembers <= 0) throw new ArgumentOutOfRangeException(nameof(maxMembers));
        var oldClaim = oldCandidate.Claim.Definition;
        var newClaim = newCandidate.Claim.Definition;
        if (!oldCandidate.Members.SequenceEqual(newCandidate.Members)
            || oldClaim.Form != newClaim.Form || oldClaim.Inputs[0].Role != newClaim.Inputs[0].Role
            || !oldClaim.Inputs[0].Contract.Equals(newClaim.Inputs[0].Contract)
            || oldClaim.Observation != newClaim.Observation
            || !oldClaim.Assumptions.SequenceEqual(newClaim.Assumptions))
            throw new ArgumentException("Comparison requires the same ordered catalog, input contract, form, observation mapping and external assumption references.", nameof(newCandidate));
        FinitePredicateEvaluator oldEvaluator = new(oldCandidate), newEvaluator = new(newCandidate);
        int visited = 0;
        int? permitted = null, forbidden = null;
        FiniteComparisonResult Result(AssuranceAttemptStatus status, string? diagnostic = null) =>
            new(oldCandidate, newCandidate, target, maxMembers, status, visited, permitted, forbidden, diagnostic);
        bool Meaning(FiniteGuardCheckDocument plan, FinitePredicateEvaluator evaluator)
        {
            var enabled = evaluator.Evaluate(plan.Assumptions) && evaluator.Evaluate(plan.Guard);
            return target == FiniteComparisonTarget.Admission ? enabled
                : !enabled || evaluator.Evaluate(plan.Claim.Definition.Predicate);
        }
        for (var index = 0; index < oldCandidate.Members.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested) return Result(AssuranceAttemptStatus.Cancelled);
            if (visited == maxMembers) return Result(AssuranceAttemptStatus.BudgetExceeded);
            oldEvaluator.SetMember(oldCandidate.Members[index]);
            newEvaluator.SetMember(oldCandidate.Members[index]);
            visited++;
            try
            {
                // Evaluate both independently, including when one already determines a query's truth.
                var oldValue = Meaning(oldCandidate, oldEvaluator);
                var newValue = Meaning(newCandidate, newEvaluator);
                if (!oldValue && newValue) permitted ??= index;
                if (oldValue && !newValue) forbidden ??= index;
                if (permitted is not null && forbidden is not null) return Result(AssuranceAttemptStatus.Completed);
            }
            catch (PortableExpressionEvaluationException error)
            {
                return Result(AssuranceAttemptStatus.Unsupported, error.Error + ": " + error.Message);
            }
        }
        return Result(cancellationToken.IsCancellationRequested ? AssuranceAttemptStatus.Cancelled : AssuranceAttemptStatus.Completed);
    }
}

/// <summary>Retained model comparison; a witness is not evidence of reachable implementation behavior.</summary>
public sealed class FiniteComparisonResult
{
    internal FiniteComparisonResult(FiniteGuardCheckDocument oldCandidate, FiniteGuardCheckDocument newCandidate,
        FiniteComparisonTarget target, int maxMembers, AssuranceAttemptStatus status, int visited,
        int? newlyPermittedIndex, int? newlyForbiddenIndex, string? diagnostic)
    {
        OldCandidate = oldCandidate; NewCandidate = newCandidate; Target = target; MaxMembers = maxMembers;
        Status = status; Visited = visited; NewlyPermittedIndex = newlyPermittedIndex;
        NewlyForbiddenIndex = newlyForbiddenIndex; Diagnostic = diagnostic;
    }
    /// <summary>Exact old candidate, including all dependencies and the shared catalog.</summary>
    public FiniteGuardCheckDocument OldCandidate { get; }
    /// <summary>Exact new candidate, including all dependencies and the shared catalog.</summary>
    public FiniteGuardCheckDocument NewCandidate { get; }
    /// <summary>Meaning selected for both directional queries.</summary>
    public FiniteComparisonTarget Target { get; }
    /// <summary>Effective row-visit budget.</summary>
    public int MaxMembers { get; }
    /// <summary>Search completion or interruption; finding both directions may stop enumeration early.</summary>
    public AssuranceAttemptStatus Status { get; }
    /// <summary>Number of rows attempted, including any unsupported row.</summary>
    public int Visited { get; }
    /// <summary>First shared row where old is false and new is true, or null if none was found.</summary>
    public int? NewlyPermittedIndex { get; }
    /// <summary>First shared row where old is true and new is false, or null if none was found.</summary>
    public int? NewlyForbiddenIndex { get; }
    /// <summary>Diagnostic for an unsupported evaluation, otherwise null.</summary>
    public string? Diagnostic { get; }
    /// <summary>Whether all declared rows were successfully evaluated.</summary>
    public bool EnumerationComplete => Status == AssuranceAttemptStatus.Completed && Visited == OldCandidate.Members.Length;
    /// <summary>Existence of a newly permitted row; absence is bounded to this catalog.</summary>
    public ClaimVerdict NewlyPermitted => Existence(NewlyPermittedIndex);
    /// <summary>Existence of a newly forbidden row; absence is bounded to this catalog.</summary>
    public ClaimVerdict NewlyForbidden => Existence(NewlyForbiddenIndex);
    ClaimVerdict Existence(int? index) => index is not null ? ClaimVerdict.Satisfied
        : EnumerationComplete ? ClaimVerdict.Violated : ClaimVerdict.Inconclusive;

    sealed record Retained(string Schema, ClaimReference Method, FiniteGuardCheckDocument OldCandidate,
        FiniteGuardCheckDocument NewCandidate, FiniteComparisonTarget Target, int MaxMembers,
        AssuranceAttemptStatus Status, int Visited, int? NewlyPermittedIndex, int? NewlyForbiddenIndex, string? Diagnostic);
    Retained Payload() => new("cohesive-assurance-finite-comparison/v1", FiniteComparison.Method,
        OldCandidate, NewCandidate, Target, MaxMembers, Status, Visited, NewlyPermittedIndex, NewlyForbiddenIndex, Diagnostic);

    /// <summary>Retains both exact candidates and all search outcomes in canonical form.</summary>
    /// <returns>Canonical JSON suitable for independent rechecking.</returns>
    /// <exception cref="JsonException">A retained value cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">A retained representation is unsupported.</exception>
    public string Serialize() => Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(Payload(), FiniteGuardCheckDocument.Options));

    /// <summary>Recomputes both directional queries and checks exact agreement with a retained result.</summary>
    /// <param name="json">Canonical comparison result.</param>
    /// <returns>Independently recomputed comparison; no execution candidate is invoked.</returns>
    /// <exception cref="JsonException">Artifact is incompatible, cancelled, malformed, or disagrees with recomputation.</exception>
    /// <exception cref="ArgumentException">Retained candidates or search bounds are incompatible.</exception>
    public static FiniteComparisonResult Recheck(string json)
    {
        if (!StrictDocumentJson.TryReadCanonicalObject(json, FiniteGuardCheckDocument.Options, "finite comparison",
                out Retained? retained, out var error) || retained is null
            || retained.OldCandidate is null || retained.NewCandidate is null)
            throw new JsonException($"{error.Failure} at {error.Location}: {error.Message}");
        if (retained.Status == AssuranceAttemptStatus.Cancelled)
            throw new JsonException("Cancelled comparisons have no retained deterministic cancellation schedule; rerun the candidates.");
        var result = FiniteComparison.Compare(retained.OldCandidate, retained.NewCandidate, retained.Target, retained.MaxMembers);
        if (!string.Equals(json, result.Serialize(), StringComparison.Ordinal))
            throw new JsonException("Retained comparison disagrees with independent enumeration.");
        return result;
    }
}
