using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Assurance;

/// <summary>Exhaustive reference interpretation of an explicit finite guard-check catalog.</summary>
public static class FiniteGuardChecker
{
    /// <summary>Versioned interpreter identity, distinct from the trace evaluator.</summary>
    public static ClaimReference Method { get; } = new(new("assurance-method://finite-guard"), "1", "finite-guard/v1");

    /// <summary>Checks implication and non-vacuity by visiting the declared catalog in source order.</summary>
    /// <param name="plan">Validated derived catalog, guard, assumptions and claim predicate.</param>
    /// <param name="maxMembers">Positive maximum catalog rows visited; exhaustion cannot establish success.</param>
    /// <param name="cancellationToken">Cancellation stops checking and returns an inconclusive attempt.</param>
    /// <returns>Retained evidence including the first counterexample and enabling witness when found.</returns>
    /// <exception cref="ArgumentNullException">Plan is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The member budget is nonpositive.</exception>
    public static FiniteGuardCheckResult Check(FiniteGuardCheckDocument plan, int maxMembers = 100_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (maxMembers <= 0) throw new ArgumentOutOfRangeException(nameof(maxMembers));
        PortableExpressionReferenceEvaluator evaluator = new(AssurancePredicates.Capabilities, "finite guard checker");
        var id = plan.Claim.Definition.Inputs[0].Id;
        PortableExpressionValue missing = new(PortableValueState.Missing, default);
        var current = missing;
        PortableExpressionValue Resolve(ValueBindingId binding) => binding == id ? current : missing;
        PortableExpressionEvaluationContext context = new()
        {
            ResolveBinding = Resolve,
            ResolveField = (binding, path) => binding is { } value ? Resolve(value).Project(path, missingFieldState: PortableValueState.Missing) : missing,
            ResolveParameter = _ => missing,
            MissingFieldState = PortableValueState.Missing
        };
        bool Evaluate(Expr expression) => PortableExpressionReferenceEvaluator.RequireBoolean(evaluator.Evaluate(expression, context), "finite guard predicate");
        int visited = 0;
        int? enabled = null;
        FiniteGuardCheckResult Result(AssuranceAttemptStatus status, ClaimVerdict verdict, int? counterexample = null, string? diagnostic = null) =>
            new(plan, maxMembers, status, verdict, visited, enabled, counterexample, diagnostic);
        for (var index = 0; index < plan.Members.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested)
                return Result(AssuranceAttemptStatus.Cancelled, ClaimVerdict.Inconclusive);
            if (visited == maxMembers)
                return Result(AssuranceAttemptStatus.BudgetExceeded, ClaimVerdict.Inconclusive);
            current = PortableExpressionValue.FromObservation(plan.Members[index]);
            visited++;
            try
            {
                if (!Evaluate(plan.Assumptions) || !Evaluate(plan.Guard)) continue;
                enabled ??= index;
                if (!Evaluate(plan.Claim.Definition.Predicate))
                    return Result(AssuranceAttemptStatus.Completed, ClaimVerdict.Violated, index);
            }
            catch (PortableExpressionEvaluationException error)
            {
                return Result(AssuranceAttemptStatus.Unsupported, ClaimVerdict.Inconclusive,
                    diagnostic: error.Error + ": " + error.Message);
            }
        }
        if (cancellationToken.IsCancellationRequested)
            return Result(AssuranceAttemptStatus.Cancelled, ClaimVerdict.Inconclusive);
        return Result(AssuranceAttemptStatus.Completed, ClaimVerdict.Satisfied);
    }
}

/// <summary>Immutable exhaustive-check attempt; success and non-vacuity are separate facts.</summary>
public sealed class FiniteGuardCheckResult
{
    internal FiniteGuardCheckResult(FiniteGuardCheckDocument plan, int maxMembers, AssuranceAttemptStatus status,
        ClaimVerdict verdict, int visited, int? enabledWitnessIndex, int? counterexampleIndex, string? diagnostic)
    {
        Plan = plan; MaxMembers = maxMembers; Status = status; Verdict = verdict; Visited = visited;
        EnabledWitnessIndex = enabledWitnessIndex; CounterexampleIndex = counterexampleIndex; Diagnostic = diagnostic;
    }
    /// <summary>Exact retained plan and full declared catalog.</summary>
    public FiniteGuardCheckDocument Plan { get; }
    /// <summary>Effective row-visit budget.</summary>
    public int MaxMembers { get; }
    /// <summary>Attempt status; a counterexample can finish the query before enumeration completes.</summary>
    public AssuranceAttemptStatus Status { get; }
    /// <summary>Implication verdict; a completed empty antecedent satisfies implication vacuously.</summary>
    public ClaimVerdict Verdict { get; }
    /// <summary>Rows actually visited, including rows excluded by assumptions or guard.</summary>
    public int Visited { get; }
    /// <summary>Index of the first row satisfying assumptions and guard, or null if none was found.</summary>
    public int? EnabledWitnessIndex { get; }
    /// <summary>Index of a validated row satisfying assumptions and guard but violating the claim.</summary>
    public int? CounterexampleIndex { get; }
    /// <summary>Unsupported-expression diagnostic, when applicable.</summary>
    public string? Diagnostic { get; }
    /// <summary>Whether every catalog row was visited successfully.</summary>
    public bool EnumerationComplete => Status == AssuranceAttemptStatus.Completed && Visited == Plan.Members.Length;
    /// <summary>Existence of an enabled row: satisfied, definitively absent, or still unknown.</summary>
    public ClaimVerdict NonVacuity => EnabledWitnessIndex is not null ? ClaimVerdict.Satisfied
        : EnumerationComplete ? ClaimVerdict.Violated : ClaimVerdict.Inconclusive;

    /// <summary>Projects checked results to bounded model evidence; never implementation refinement evidence.</summary>
    /// <returns>Restricted-scope evidence retaining the exact plan and result references; vacuity cannot establish proof.</returns>
    /// <exception cref="JsonException">A retained portable value cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">A retained representation is unsupported.</exception>
    public ClaimEvidence ToEvidence()
    {
        var planReference = Plan.Reference();
        var resultReference = new ClaimReference(new("assurance-result://finite-guard"), "1",
            Convert.ToHexString(StrictDocumentJson.ComputeCanonicalSha256(Payload(), FiniteGuardCheckDocument.Options)));
        return new(Plan.Claim, new(EvidenceTargetKind.Model, planReference, Plan.Configuration, Plan.Claim.Definition.Selection, ProofObligation.Model),
            EvidenceKind.ExhaustiveCheck, FiniteGuardChecker.Method, Plan.Configuration,
            Status == AssuranceAttemptStatus.Unsupported ? InterpretationSupport.Unsupported : InterpretationSupport.Supported,
            Status, Verdict == ClaimVerdict.Satisfied && NonVacuity != ClaimVerdict.Satisfied ? ClaimVerdict.Inconclusive : Verdict,
            EvidenceCoverage.RestrictedScope, dependenciesComplete: true,
            dependencies: [Plan.Source], residuals: [planReference], artifacts: [planReference, resultReference]);
    }

    sealed record Retained(string Schema, ClaimReference Method, FiniteGuardCheckDocument Plan, int MaxMembers, AssuranceAttemptStatus Status,
        ClaimVerdict Verdict, int Visited, int? EnabledWitnessIndex, int? CounterexampleIndex, string? Diagnostic);
    Retained Payload() => new("cohesive-assurance-finite-result/v1", FiniteGuardChecker.Method, Plan, MaxMembers, Status, Verdict, Visited, EnabledWitnessIndex, CounterexampleIndex, Diagnostic);

    /// <summary>Retains exact inputs, bounds, witnesses and outcome for independent checking.</summary>
    /// <returns>Canonical result JSON.</returns>
    /// <exception cref="JsonException">A retained portable value cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">A retained representation is unsupported.</exception>
    public string Serialize() => Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(Payload(), FiniteGuardCheckDocument.Options));

    /// <summary>Independently reruns a retained attempt; cancellation is a recorded stopping event, not a successful check.</summary>
    /// <param name="json">Retained result JSON.</param>
    /// <returns>Recomputed result with exact agreement; cancelled attempts cannot be reproduced by this deterministic replay API.</returns>
    /// <exception cref="JsonException">The result is malformed, cancelled, or disagrees with independent checking.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The retained row budget is nonpositive.</exception>
    public static FiniteGuardCheckResult Recheck(string json)
    {
        if (!StrictDocumentJson.TryReadCanonicalObject(json, FiniteGuardCheckDocument.Options, "finite check result", out Retained? retained, out var error)
            || retained is null || retained.Plan is null)
            throw new JsonException($"{error.Failure} at {error.Location}: {error.Message}");
        if (retained.Status == AssuranceAttemptStatus.Cancelled)
            throw new JsonException("A cancelled attempt has no deterministic cancellation schedule; rerun its retained plan explicitly.");
        var result = FiniteGuardChecker.Check(retained.Plan, retained.MaxMembers);
        if (!string.Equals(json, result.Serialize(), StringComparison.Ordinal))
            throw new JsonException("Retained finite-check evidence disagrees with independent enumeration.");
        return result;
    }
}
