using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Assurance;

/// <summary>Batch evidence from the v1 reference interpretation; it does not assign global proof state or readiness.</summary>
public sealed class EffectUniquenessEvidence
{
    internal EffectUniquenessEvidence(EffectUniquenessCheck input, AssuranceVerdict verdict,
        int operations, int effects, int missing, int? first, int? second, DocumentValidationResult validation)
    {
        Input = input;
        Verdict = verdict;
        ObservedOperations = operations;
        UniqueEffects = effects;
        MissingIdentities = missing;
        FirstWitnessIndex = first;
        SecondWitnessIndex = second;
        Validation = validation;
    }

    /// <summary>Exact immutable input; witness indices refer to this observation array.</summary>
    public EffectUniquenessCheck Input { get; }
    /// <summary>Scoped reference verdict, independent of job success or claim acceptance.</summary>
    public AssuranceVerdict Verdict { get; }
    /// <summary>Number of operations with fully identified effect evidence.</summary>
    public int ObservedOperations { get; }
    /// <summary>Number of distinct fully identified effects; duplicate telemetry is excluded.</summary>
    public int UniqueEffects { get; }
    /// <summary>Number of observations with at least one missing identity.</summary>
    public int MissingIdentities { get; }
    /// <summary>First counterexample observation index, or null when no valid witness is retained.</summary>
    public int? FirstWitnessIndex { get; }
    /// <summary>Second distinct effect for the witness operation, or null.</summary>
    public int? SecondWitnessIndex { get; }
    /// <summary>Structured observation-contract errors; invalid evidence always yields inconclusive.</summary>
    public DocumentValidationResult Validation { get; }
    /// <summary>Whether consistent evidence contains no observed violation; this is not proof.</summary>
    public bool NoViolationObserved => Validation.IsValid && Verdict != AssuranceVerdict.Violated;
    /// <summary>Whether a complete satisfied scope contains no observed operations.</summary>
    public bool IsVacuous => Verdict == AssuranceVerdict.Satisfied && ObservedOperations == 0;

    /// <summary>Projects a deterministic, payload-free explanation for review and expect tests.</summary>
    /// <returns>Stable text including scope, coverage, and the limits of the result.</returns>
    public string Explain()
    {
        var reason = !Validation.IsValid ? Validation.Diagnostics[0].Code
            : Verdict == AssuranceVerdict.Violated ? "Distinct committed effects share one operation occurrence."
            : MissingIdentities > 0 ? "Required identity evidence is missing."
            : Input.Completion == ObservationScopeCompletion.Open ? "The scope is open; later effects remain possible."
            : Input.Completion == ObservationScopeCompletion.ClosedIncomplete ? "The closed scope has incomplete observations."
            : "The explicitly closed complete scope has no duplicate effects.";
        return string.Create(CultureInfo.InvariantCulture,
        $"Claim: {Input.Claim.Id}@{Input.Claim.Revision}\nProfile: {EffectUniquenessEvaluator.Profile}\nVerdict: {Verdict}\nScope: {Input.Completion}\nObserved operations: {ObservedOperations}\nDistinct effects: {UniqueEffects}\nMissing identities: {MissingIdentities}\nVacuous: {IsVacuous}\nReason: {reason}\nGeneral proof state: not established by this trace check");
    }
}

/// <summary>Reference evaluation of finite effect uniqueness under the A0 observation and closure contract.</summary>
public static class EffectUniquenessEvaluator
{
    /// <summary>Exact supported semantic and evaluator profile; changes require evidence reassessment.</summary>
    public const string Profile = "cohesive-assurance/effect-uniqueness/v1";

    /// <summary>Exact reference implementation version retained with evidence.</summary>
    public const string Identity = "cohesive-assurance/reference-effect-uniqueness/v1";

    /// <summary>Evaluates the complete supplied projection without inferring closure from its length.</summary>
    /// <param name="input">Pinned claim, target, observation mapping, and finite observations.</param>
    /// <returns>Scoped evidence; contradictory or foreign observations produce diagnostics and inconclusive.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    public static EffectUniquenessEvidence Evaluate(EffectUniquenessCheck input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        if (input.ObservedTransition != input.Claim.Transition)
            diagnostics.Add(new("assurance.observation.transition.mismatch", DiagnosticSeverity.Error,
                "Observed transition does not match the claim's exact reference.", "/observedTransition"));

        var effects = new Dictionary<string, string>(StringComparer.Ordinal);
        var operations = new Dictionary<string, (string Effect, int Index)>(StringComparer.Ordinal);
        var missing = 0;
        int? first = null;
        int? second = null;
        for (var index = 0; index < input.Observations.Length; index++)
        {
            var observation = input.Observations[index];
            if (observation.OperationIdentity is not { } operation || observation.EffectIdentity is not { } effect)
            {
                missing++;
                continue;
            }
            if (effects.TryGetValue(effect, out var previousOperation))
            {
                if (previousOperation != operation)
                    diagnostics.Add(new("assurance.observation.effect.conflict", DiagnosticSeverity.Error,
                        "One effect is attributed to different operation occurrences.", $"/observations/{index}"));
                continue;
            }
            effects.Add(effect, operation);
            if (!operations.TryGetValue(operation, out var previous))
                operations.Add(operation, (effect, index));
            else if (second is null)
            {
                first = previous.Index;
                second = index;
            }
        }
        var validation = DocumentValidationResult.FromDiagnostics(diagnostics.ToImmutable());
        var verdict = !validation.IsValid ? AssuranceVerdict.Inconclusive
            : second is not null ? AssuranceVerdict.Violated
            : missing == 0 && input.Completion == ObservationScopeCompletion.ClosedComplete
                ? AssuranceVerdict.Satisfied : AssuranceVerdict.Inconclusive;
        return new(input, verdict, operations.Count, effects.Count, missing,
            validation.IsValid ? first : null, validation.IsValid ? second : null, validation);
    }
}
