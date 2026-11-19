using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Assurance;

/// <summary>Reference batch interpretation of the four claim forms over declared semantic observations.</summary>
/// <remarks>
/// Joint finite alternatives are evaluated independently before lifting the whole claim verdict.
/// The interpreter never infers serialization order, source completeness or a provider guarantee.
/// </remarks>
public static class TraceClaimEvaluator
{
    /// <summary>Evaluates a derived claim, retaining obligations, witnesses, coverage and exact evidence inputs.</summary>
    /// <param name="claim">Validated derived claim document.</param>
    /// <param name="batch">Finite adapter input with scoped completeness and joint mapping alternatives.</param>
    /// <param name="limits">Explicit limits, or conservative reference defaults.</param>
    /// <param name="cancellationToken">Cancellation signal; cancellation throws rather than returning partial success.</param>
    /// <returns>A retained evaluation attempt; trace satisfaction is not universal proof.</returns>
    /// <exception cref="ArgumentNullException">Claim or batch is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit is nonpositive.</exception>
    /// <exception cref="OperationCanceledException">Evaluation is cancelled.</exception>
    public static TraceEvaluation Evaluate(ClaimDocument claim, TraceObservationBatch batch,
        TraceEvaluationLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(batch);
        limits ??= new();
        if (limits.MaxAlternatives <= 0 || limits.MaxObservations <= 0 || limits.MaxPredicateEvaluations <= 0 || limits.MaxEventCandidateVisits <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "Evaluation bounds must be positive.");
        return new Evaluation(claim, batch, limits, cancellationToken).Run();
    }

    // Advance constructs the batch by appending to this exact accepted prefix.
    internal static TraceEvaluation EvaluateAppend(TraceEvaluation prefix, TraceObservationBatch batch,
        CancellationToken cancellation) => new Evaluation(prefix.Claim, batch, prefix.Limits, cancellation,
            CanReuse(prefix) ? prefix : null).Run();

    static bool CanReuse(TraceEvaluation prefix) => prefix.Claim.Definition.Form switch
    {
        ClaimForm.StateInvariant or ClaimForm.TransitionContract => true,
        // Predicate diagnostics can depend on shared input preparation across triggers.
        // Replay those prefixes in full to preserve exact diagnostic ordering.
        ClaimForm.BoundedResponse => prefix.Alternatives[0].DuplicateDeliveries == 0
            && prefix.Diagnostics.All(d => d.Code is TraceEvaluationCodes.ScopeOpen or TraceEvaluationCodes.ResponsePending),
        _ => false
    };

    sealed class Evaluation(ClaimDocument document, TraceObservationBatch batch,
        TraceEvaluationLimits limits, CancellationToken cancellation, TraceEvaluation? prefix = null)
    {
        static readonly PortableExpressionValue Missing = new(PortableValueState.Missing, default);
        readonly ClaimDefinition claim = document.Definition;
        readonly List<TraceEvaluationDiagnostic> diagnostics = [];
        readonly List<TraceAlternativeResult> results = [];
        readonly PortableExpressionReferenceEvaluator expressions = new(AssurancePredicates.Capabilities, "Assurance reference evaluator");
        AssuranceAttemptStatus status = AssuranceAttemptStatus.Completed;
        long predicateEvaluations;
        long candidateVisits;
        Dictionary<(string Partition, string Occurrence), int>? priorWitnessIndices;
        readonly Dictionary<ValueBindingId, int> inputIndices = document.Definition.Inputs
            .Select((input, index) => (input.Id, index)).ToDictionary(x => x.Id, x => x.index);
        // Invocation-owned, bounded by observations × declared inputs; cleared between alternatives.
        readonly Dictionary<SemanticObservation, PortableExpressionValue?[]> preparedInputs = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<ClaimReference, ObservationRequirement> requirements = [];
        readonly Dictionary<(ClaimReference, string), ObservationCompleteness> frontiers = [];

        public TraceEvaluation Run()
        {
            cancellation.ThrowIfCancellationRequested();
            if (!ValidateBatch()) return Finish(ClaimVerdict.Inconclusive);
            foreach (var alternative in batch.Alternatives)
            {
                cancellation.ThrowIfCancellationRequested();
                var observations = ValidateAlternative(alternative, out var duplicates);
                if (status == AssuranceAttemptStatus.InvalidObservations) return Finish(ClaimVerdict.Inconclusive);
                results.Add(EvaluateAlternative(alternative, observations, duplicates));
                if (status == AssuranceAttemptStatus.BudgetExceeded) return Finish(ClaimVerdict.Inconclusive);
            }
            if (status != AssuranceAttemptStatus.Completed) return Finish(ClaimVerdict.Inconclusive);
            if (batch.Enumeration != EnumerationCompleteness.Complete)
            {
                Gap(TraceEvaluationCodes.EnumerationIncomplete, "/alternatives", "Additional compatible joint assignments or trace mappings may exist.");
                return Finish(ClaimVerdict.Inconclusive);
            }
            var first = results[0].Verdict;
            return Finish(results.All(x => x.Verdict == first) ? first : ClaimVerdict.Inconclusive);
        }

        TraceEvaluation Finish(ClaimVerdict verdict) => new(document, batch, limits, status, verdict,
            [.. results], [.. diagnostics]);

        bool ValidateBatch()
        {
            if (batch.Contract is null || batch.Contract.Reference is null || batch.Contract.Scope is null
                || batch.Target is null || batch.Epoch is null || batch.EnumerationEvidence is null
                || !Enum.IsDefined(batch.Enumeration))
                return Invalid("/batch", "Exact contract, scope, target, epoch and enumeration evidence are required.");
            if (batch.Contract.Reference != claim.Observation)
                return Invalid("/contract", "The observation contract does not match the claim's exact revision and fingerprint.");
            if (batch.Alternatives.IsDefaultOrEmpty)
                return Invalid("/alternatives", "Zero compatible mappings is not vacuous satisfaction; a consistency checker must explain the empty domain.");
            if (batch.Alternatives.Any(x => x is null || x.Reference is null || x.Observations.IsDefault || x.Completeness.IsDefault)
                || batch.Alternatives.Select(x => x.Reference).Distinct().Count() != batch.Alternatives.Length)
                return Invalid("/alternatives", "Alternatives require unique exact references and initialized observation/frontier arrays.");
            if (batch.Alternatives.Length > limits.MaxAlternatives
                || batch.Alternatives.Sum(x => (long)x.Observations.Length + x.Completeness.Length) > limits.MaxObservations)
                return Exhausted("/alternatives", "The finite observation input exceeds the declared batch bounds.");
            var declaredRequirements = batch.Contract.Requirements;
            if (declaredRequirements.IsDefaultOrEmpty || declaredRequirements.Any(x => x is null || x.Selector is null
                || x.Partitions.IsDefaultOrEmpty || x.Partitions.Any(string.IsNullOrWhiteSpace)
                || x.Partitions.Distinct(StringComparer.Ordinal).Count() != x.Partitions.Length)
                || declaredRequirements.Select(x => x.Selector).Distinct().Count() != declaredRequirements.Length)
                return Invalid("/contract/requirements", "Each exact selector needs unique, nonempty required source partitions.");
            if (!declaredRequirements.Any(x => x.Selector == claim.Selection)
                || claim.Witness is not null && !declaredRequirements.Any(x => x.Selector == claim.Witness))
            {
                status = AssuranceAttemptStatus.Unsupported;
                Gap(TraceEvaluationCodes.MappingUnsupported, "/contract/requirements", "The contract does not map every exact selector needed by this claim.");
                return false;
            }
            foreach (var requirement in declaredRequirements) requirements.Add(requirement.Selector, requirement);
            return true;
        }

        List<SemanticObservation> ValidateAlternative(TraceObservationAlternative alternative, out int duplicates)
        {
            duplicates = 0;
            List<SemanticObservation> observations = [];
            Dictionary<(string Partition, string Occurrence), SemanticObservation> seen = [];
            long previousStep = -1, previousTime = -1;
            foreach (var observation in alternative.Observations)
            {
                cancellation.ThrowIfCancellationRequested();
                if (TraceObservationValidation.Observe(observation, seen, out var duplicate) is { } invalid)
                {
                    Invalid(invalid.Location, invalid.Message);
                    return observations;
                }
                if (!CompatibleKind(observation) || observation.Kind == SemanticObservationKind.InitialState && observation.Step != 0)
                {
                    Invalid(observation.Occurrence, "Observation kind does not match the selected claim form or initial boundary.");
                    return observations;
                }
                if (duplicate)
                {
                    duplicates++;
                    continue;
                }
                if (observation.Step < previousStep || observation.Ticks < previousTime
                    || observation.Step == previousStep && observation.Ticks != previousTime)
                {
                    Invalid(observation.Occurrence, "Observations are not in the declared monotone semantic order; arrival order is not a substitute.");
                    return observations;
                }
                previousStep = observation.Step;
                previousTime = observation.Ticks;
                var requirement = requirements.GetValueOrDefault(observation.Selector);
                if (requirement is null || !requirement.Partitions.Contains(observation.Partition, StringComparer.Ordinal))
                {
                    Invalid(observation.Occurrence, "The observation's exact selector or source partition is not declared by the contract.");
                    return observations;
                }
                observations.Add(observation);
            }
            frontiers.Clear();
            foreach (var frontier in alternative.Completeness)
            {
                if (TraceObservationValidation.Register(frontier, frontiers) is { } invalid)
                {
                    Invalid(invalid.Location, invalid.Message);
                    return observations;
                }
                var requirement = requirements.GetValueOrDefault(frontier.Selector);
                if (requirement is null || !requirement.Partitions.Contains(frontier.Partition, StringComparer.Ordinal))
                {
                    Invalid("/completeness", "Frontier source or selector is not declared by the observation contract.");
                    return observations;
                }
                if (frontier.Closure == ObservationClosure.Closed && observations.Any(x => x.Selector == frontier.Selector
                    && x.Partition == frontier.Partition && (x.Step > frontier.ThroughStep || x.Ticks > frontier.ThroughTicks)))
                {
                    Invalid("/completeness", "A closed frontier excludes retained observations in its own scope.");
                    return observations;
                }
            }
            return observations;
        }

        bool CompatibleKind(SemanticObservation observation)
        {
            if (observation.Selector == claim.Witness) return observation.Kind == SemanticObservationKind.Event;
            if (observation.Selector != claim.Selection) return true;
            return claim.Form switch
            {
                ClaimForm.StateInvariant => observation.Kind is SemanticObservationKind.InitialState or SemanticObservationKind.CommittedState,
                ClaimForm.TransitionContract => observation.Kind is SemanticObservationKind.Commit or SemanticObservationKind.AbortedAttempt or SemanticObservationKind.UnknownOutcome,
                _ => observation.Kind == SemanticObservationKind.Event
            };
        }

        TraceAlternativeResult EvaluateAlternative(TraceObservationAlternative alternative,
            List<SemanticObservation> observations, int duplicates)
        {
            preparedInputs.Clear();
            List<ClaimObligationResult> obligations = [];
            if (prefix is not null && claim.Form == ClaimForm.BoundedResponse)
            {
                priorWitnessIndices = [];
                foreach (var row in prefix.Batch.Alternatives[0].Observations)
                    if (row.Selector == claim.Witness)
                        priorWitnessIndices.Add(TraceObservationValidation.Key(row), priorWitnessIndices.Count);
            }
            else if (prefix is not null)
                foreach (var diagnostic in prefix.Diagnostics)
                    if (diagnostic.Code != TraceEvaluationCodes.ScopeOpen) diagnostics.Add(diagnostic);
            var candidates = observations.Where(x => x.Kind == SemanticObservationKind.Event && x.Selector == claim.Witness).ToList();
            foreach (var observation in observations)
            {
                if (observation.Selector != claim.Selection || !Selects(observation.Kind)) continue;
                cancellation.ThrowIfCancellationRequested();
                // A selected occurrence in the retained prefix has the same inputs and position.
                // Charge its original logical work so limits agree with a fresh batch run.
                if (priorWitnessIndices is null && prefix is not null && obligations.Count < prefix.Alternatives[0].Obligations.Length)
                {
                    obligations.Add(prefix.Alternatives[0].Obligations[obligations.Count]);
                    if (observation.Kind != SemanticObservationKind.UnknownOutcome) predicateEvaluations++;
                    continue;
                }
                var result = observation.Kind == SemanticObservationKind.UnknownOutcome
                    ? Obligation(observation, ClaimVerdict.Inconclusive, TraceEvaluationCodes.CommitOutcomeUnknown)
                    : claim.Form is ClaimForm.StateInvariant or ClaimForm.TransitionContract
                        ? Obligation(observation, Predicate(observation, null), TraceEvaluationCodes.Predicate)
                        : Match(observation, candidates, obligations.Count);
                obligations.Add(result);
                if (status == AssuranceAttemptStatus.BudgetExceeded) break;
            }
            var closed = Covers(claim.Selection, frontier => frontier.Closure == ObservationClosure.Closed);
            var verdict = obligations.Any(x => x.Verdict == ClaimVerdict.Violated) ? ClaimVerdict.Violated
                : closed && obligations.All(x => x.Verdict == ClaimVerdict.Satisfied) ? ClaimVerdict.Satisfied
                : ClaimVerdict.Inconclusive;
            if (!closed) Gap(TraceEvaluationCodes.ScopeOpen, alternative.Reference.Source.Value, "The enclosing selected scope is not closed by all required source partitions.");
            return new(alternative.Reference, verdict, [.. obligations], duplicates, closed, closed && obligations.Count == 0);
        }

        bool Selects(SemanticObservationKind kind) => claim.Form switch
        {
            ClaimForm.StateInvariant => kind == SemanticObservationKind.CommittedState
                || kind == SemanticObservationKind.InitialState && claim.InitialState == InitialStateSelection.Include,
            ClaimForm.TransitionContract => kind is SemanticObservationKind.Commit or SemanticObservationKind.UnknownOutcome,
            _ => kind == SemanticObservationKind.Event
        };

        ClaimObligationResult Match(SemanticObservation trigger, List<SemanticObservation> observations, int obligationIndex)
        {
            long deadline = 0;
            if (claim.Form == ClaimForm.BoundedResponse)
            {
                try { deadline = checked(trigger.Ticks + claim.DeadlineTicks!.Value); }
                catch (OverflowException)
                {
                    status = AssuranceAttemptStatus.Unsupported;
                    Gap(TraceEvaluationCodes.DeadlineOverflow, trigger.Occurrence, "The inclusive deadline exceeds the supported clock domain.");
                    return Obligation(trigger, ClaimVerdict.Inconclusive, TraceEvaluationCodes.DeadlineOverflow);
                }
            }
            var unknown = false;
            // Validation establishes monotone steps and ticks. Search the eligible half-open
            // range before charging the comparison budget; same-step outcomes remain inclusive.
            var start = claim.Form == ClaimForm.HistoricalRequirement ? 0 : Bound(observations, trigger.Step, byTime: false, inclusive: false);
            var end = claim.Form == ClaimForm.HistoricalRequirement
                ? Bound(observations, trigger.Step, byTime: false, inclusive: false)
                : Bound(observations, deadline, byTime: true, inclusive: true);
            if (priorWitnessIndices is not null && obligationIndex < prefix!.Alternatives[0].Obligations.Length)
            {
                var previous = prefix.Alternatives[0].Obligations[obligationIndex];
                var resume = previous.WitnessOccurrence is { } occurrence
                    ? priorWitnessIndices[(previous.WitnessPartition!, occurrence)] + 1
                    : Math.Clamp(priorWitnessIndices.Count, start, end);
                var visits = resume - start;
                // New work on an earlier trigger may have consumed the remaining budget.
                // In that case replay this range to report the same first failure as batch.
                if (visits <= limits.MaxEventCandidateVisits - candidateVisits
                    && visits <= limits.MaxPredicateEvaluations - predicateEvaluations)
                {
                    candidateVisits += visits;
                    predicateEvaluations += visits;
                    if (previous.WitnessOccurrence is not null) return previous;
                    start = resume;
                }
            }
            for (var index = start; index < end; index++)
            {
                var candidate = observations[index];
                cancellation.ThrowIfCancellationRequested();
                if (++candidateVisits > limits.MaxEventCandidateVisits)
                {
                    Exhausted(trigger.Occurrence, "The event candidate scan budget is exhausted.");
                    return Obligation(trigger, ClaimVerdict.Inconclusive, TraceEvaluationCodes.BudgetExceeded);
                }
                var verdict = Predicate(trigger, candidate);
                if (verdict == ClaimVerdict.Satisfied)
                    return new(trigger.Occurrence, trigger.Partition, trigger.Step, verdict,
                        candidate.Occurrence, candidate.Partition, TraceEvaluationCodes.WitnessMatched);
                unknown |= verdict == ClaimVerdict.Inconclusive;
                if (status == AssuranceAttemptStatus.BudgetExceeded) break;
            }
            var complete = claim.Form == ClaimForm.HistoricalRequirement
                ? Covers(claim.Witness!, frontier => frontier.ThroughStep >= Math.Max(0, trigger.Step - 1))
                : Covers(claim.Witness!, frontier => frontier.ThroughTicks >= deadline);
            var reason = unknown ? TraceEvaluationCodes.MatchMissingEvidence : !complete
                ? claim.Form == ClaimForm.HistoricalRequirement ? TraceEvaluationCodes.HistoryIncomplete : TraceEvaluationCodes.ResponsePending
                : TraceEvaluationCodes.WitnessAbsent;
            if (unknown || !complete) Gap(reason, trigger.Occurrence, "No established witness; predicate evidence or source completeness is insufficient.");
            return Obligation(trigger, unknown || !complete ? ClaimVerdict.Inconclusive : ClaimVerdict.Violated, reason);
        }

        static int Bound(List<SemanticObservation> observations, long value, bool byTime, bool inclusive)
        {
            var low = 0;
            var high = observations.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                var key = byTime ? observations[middle].Ticks : observations[middle].Step;
                if (key < value || inclusive && key == value) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        bool Covers(ClaimReference selector, Func<ObservationCompleteness, bool> sufficient)
        {
            var requirement = requirements[selector];
            return requirement.Partitions.All(partition => frontiers.TryGetValue((selector, partition), out var frontier) && sufficient(frontier));
        }

        static ClaimObligationResult Obligation(SemanticObservation observation, ClaimVerdict verdict, string reason) =>
            new(observation.Occurrence, observation.Partition, observation.Step, verdict, null, null, reason);

        ClaimVerdict Predicate(SemanticObservation trigger, SemanticObservation? witness)
        {
            if (++predicateEvaluations > limits.MaxPredicateEvaluations)
            {
                Exhausted(trigger.Occurrence, "The predicate/candidate comparison budget is exhausted; no obligations were silently discarded.");
                return ClaimVerdict.Inconclusive;
            }
            cancellation.ThrowIfCancellationRequested();
            PortableExpressionValue Resolve(ValueBindingId id)
            {
                if (!inputIndices.TryGetValue(id, out var index)) return Missing;
                var input = claim.Inputs[index];
                var observation = input.Role == ClaimInputRole.Witness ? witness : trigger;
                if (observation is null) return Missing;
                if (!preparedInputs.TryGetValue(observation, out var values))
                    preparedInputs.Add(observation, values = new PortableExpressionValue?[claim.Inputs.Length]);
                if (values[index] is { } prepared) return prepared;
                var value = input.Role is ClaimInputRole.Trigger or ClaimInputRole.Witness
                    ? observation.EventPayload
                    : observation.Values.TryGetValue(input.Role, out var observed) ? observed : (ObservationValue?)null;
                if (value is null) return (values[index] = Missing).Value;
                if (!input.Contract.IsSatisfiedByConstant(value.Value))
                {
                    Gap(TraceEvaluationCodes.InputContractGap, observation.Occurrence, "The observed input is incomplete or does not satisfy its declared value contract.");
                    return (values[index] = Missing).Value;
                }
                return (values[index] = PortableExpressionValue.FromObservation(value.Value)).Value;
            }
            try
            {
                var result = expressions.Evaluate(claim.Predicate, new()
                {
                    ResolveBinding = Resolve,
                    ResolveField = (id, path) => id is { } binding ? Resolve(binding).Project(path, missingFieldState: PortableValueState.Missing) : Missing,
                    ResolveParameter = _ => Missing,
                    MissingFieldState = PortableValueState.Missing
                });
                return PortableExpressionReferenceEvaluator.RequireBoolean(result, "claim predicate")
                    ? ClaimVerdict.Satisfied : ClaimVerdict.Violated;
            }
            catch (PortableExpressionEvaluationException error)
            {
                if (error.Error != PortableExpressionEvaluationError.RuntimeInputUnavailable)
                    status = AssuranceAttemptStatus.Unsupported;
                Gap(TraceEvaluationCodes.Expression(error.Error), trigger.Occurrence + "/predicate", error.Message);
                return ClaimVerdict.Inconclusive;
            }
        }

        void Gap(string code, string location, string message) => diagnostics.Add(new(code, location, message));
        bool Invalid(string location, string message)
        {
            status = AssuranceAttemptStatus.InvalidObservations;
            Gap(TraceEvaluationCodes.InvalidObservation, location, message);
            return false;
        }
        bool Exhausted(string location, string message)
        {
            status = AssuranceAttemptStatus.BudgetExceeded;
            Gap(TraceEvaluationCodes.BudgetExceeded, location, message);
            return false;
        }
    }
}
