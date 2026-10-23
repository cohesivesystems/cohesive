using Cohesive.Execution;
using Cohesive.Transitions.Model;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Execution;

/// <summary>Projects a canonical Transition decision's committable patch onto aggregate state.</summary>
public static class TransitionStateProjector
{
    /// <summary>Applies the decision patch in execution order after verifying its before-value evidence.</summary>
    /// <param name="state">Concrete complete aggregate state used to produce the decision.</param>
    /// <param name="decision">Canonical non-committing decision to project.</param>
    /// <returns>The candidate aggregate state after applying every retained patch.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="state"/> is not an object value.</exception>
    /// <exception cref="TransitionStatePreparationException">A patch has mismatched before-values,
    /// a non-committable after-value, or unsupported navigation; code and location identify the failure.</exception>
    public static ObservationValue Apply(ObservationValue state, TransitionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (state.Kind != ObservationValueKind.Object)
        {
            throw new ArgumentException("Transition state projection requires a concrete object value.", nameof(state));
        }

        if (decision.Patch.IsDefaultOrEmpty)
            return state;

        // Assignments are applied in one copy at the end. Before-value evidence is still checked in patch order
        // against the candidate produced by every earlier patch: reads consult pending assignments first.
        var candidate = state;
        var pending = new KeyValuePair<FieldPath, ObservationValue>[decision.Patch.Length];
        var pendingCount = 0;
        for (var index = 0; index < decision.Patch.Length; index++)
        {
            var patch = decision.Patch[index];
            foreach (var segment in patch.Path.Segments)
                if (segment.Kind != SegmentKind.Field)
                    throw new TransitionStatePreparationException("transition.state.pathUnsupported",
                        $"/decision/patch/{index}/path", "State projection does not support collection-element navigation.");
            var observed = ReadCandidate(ref candidate, pending, ref pendingCount, patch.Path);
            if (!MatchesEvidence(patch.Before, observed))
            {
                throw new TransitionStatePreparationException(
                    "transition.state.beforeMismatch", $"/decision/patch/{index}/before",
                    $"Transition patch '{patch.Node.Value}' cannot be projected because state at "
                    + $"'{patch.Path}' does not match its before-value evidence.");
            }

            RequireAssignableAncestors(ref candidate, pending, ref pendingCount, patch.Path);
            pending[pendingCount++] = new(patch.Path, patch.After.State switch
            {
                PortableValueState.Concrete => patch.After.Value!.Value,
                PortableValueState.Null => ObservationValue.Null,
                // WithFields removes the terminal field and empty object ancestors, as WithoutField does.
                PortableValueState.Absent => ObservationValue.Undefined,
                _ => throw new TransitionStatePreparationException(
                    "transition.state.valueNotCommittable", $"/decision/patch/{index}/after",
                    $"Transition patch '{patch.Node.Value}' produced non-committable value state "
                    + $"'{patch.After.State}'.")
            });
        }

        return candidate.WithFields(new ArraySegment<KeyValuePair<FieldPath, ObservationValue>>(pending, 0, pendingCount));
    }

    // Resolves a path against base state plus ordered pending assignments. The latest assignment to the path or an
    // ancestor wins. A later assignment beneath the path cannot be read from one value, so pending work is folded first.
    static ObservationValue ReadCandidate(
        ref ObservationValue candidate,
        KeyValuePair<FieldPath, ObservationValue>[] pending,
        ref int pendingCount,
        FieldPath path)
    {
        for (var index = pendingCount - 1; index >= 0; index--)
        {
            var assigned = pending[index].Key;
            if (assigned.IsPrefixOf(path))
            {
                var value = pending[index].Value;
                if (assigned.Segments.Length == path.Segments.Length)
                    return value;
                return value.TryGetFieldSegments(path.Segments.AsSpan()[assigned.Segments.Length..], out var nested)
                    ? nested
                    : ObservationValue.Undefined;
            }

            if (path.IsPrefixOf(assigned))
            {
                candidate = candidate.WithFields(new ArraySegment<KeyValuePair<FieldPath, ObservationValue>>(pending, 0, pendingCount));
                pendingCount = 0;
                break;
            }
        }

        return candidate.TryGetField(path, out var current) ? current : ObservationValue.Undefined;
    }

    // Sequential WithField failed at the offending patch when an ancestor held a scalar or array. Checking ancestors
    // here keeps that failure ordered before any later patch's evidence mismatch; direct fields have no ancestors.
    static void RequireAssignableAncestors(
        ref ObservationValue candidate,
        KeyValuePair<FieldPath, ObservationValue>[] pending,
        ref int pendingCount,
        FieldPath path)
    {
        for (var length = 1; length < path.Segments.Length; length++)
        {
            var ancestor = ReadCandidate(ref candidate, pending, ref pendingCount, new([.. path.Segments.AsSpan()[..length]]));
            if (ancestor.Kind is ObservationValueKind.Undefined or ObservationValueKind.Null)
                return;
            if (ancestor.Kind != ObservationValueKind.Object)
                throw new InvalidOperationException(
                    $"Field path '{path}' cannot be assigned through value kind '{ancestor.Kind}'.");
        }
    }

    // Equivalent to comparing with the PortableValue that Read would construct under the evidence's own contract.
    static bool MatchesEvidence(PortableValue before, ObservationValue value) => value.Kind switch
    {
        ObservationValueKind.Undefined => before.State == PortableValueState.Absent,
        ObservationValueKind.Null => before.State == PortableValueState.Null,
        _ => before.State == PortableValueState.Concrete && before.Value!.Value.Equals(value)
    };

    /// <summary>Prepares one entity candidate from a canonical decision without committing or delivering effects.</summary>
    /// <param name="entity">Canonical entity authority used to validate the resulting observation.</param>
    /// <param name="entityId">Exact subject identity selected by the invoking boundary.</param>
    /// <param name="decision">Decision made from the supplied current observation, or a successful creation decision.</param>
    /// <param name="current">Existing immutable subject snapshot; its shape identity is checked and the resulting candidate is validated. Null requires retained creation evidence. Absence must still be enforced at commit.</param>
    /// <returns>The validated candidate. Creation starts at version zero; applied changes advance the current version, while no-change and rejected decisions preserve it.</returns>
    /// <exception cref="ArgumentNullException">Entity or decision is null.</exception>
    /// <exception cref="ArgumentException">Subject identity is empty.</exception>
    /// <exception cref="TransitionStatePreparationException">Known decision, subject, evidence, patch,
    /// version, or entity-validation failure; code and location identify the rejected input.</exception>
    /// <remarks>This is state application, not history replay or commit evidence. Callers retain authorization,
    /// concurrency fences, emission ownership, and atomic persistence responsibilities.</remarks>
    public static EntityState ApplyToEntity(EntityDefinition entity, string entityId,
        TransitionDecision decision, EntityObservationSnapshot? current = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        if (decision.Kind is not (TransitionDecisionKind.Applied or TransitionDecisionKind.NoChange
            or TransitionDecisionKind.AdmissionRejected or TransitionDecisionKind.DomainRejected))
            throw new TransitionStatePreparationException("transition.state.decisionUnsupported", "/decision/kind",
                "Only Applied, NoChange, AdmissionRejected, or DomainRejected decisions can prepare entity state.");

        ObservationValue initial;
        long version;
        if (current is null)
        {
            if (decision.Kind != TransitionDecisionKind.Applied
                || decision.Evidence.InitialObservation is not { State: PortableValueState.Concrete, Value: { Fields: not null } value })
                throw new TransitionStatePreparationException("transition.state.creationEvidenceMissing",
                    "/decision/evidence/initialObservation",
                    "An absent subject requires a successful creation decision with complete initial-observation evidence.");
            initial = value;
            version = 0;
        }
        else
        {
            if (current.EntityId.Value != entityId)
                throw new TransitionStatePreparationException("transition.state.subjectMismatch", "/current/entityId",
                    "The current snapshot belongs to a different subject.");
            if (decision.Evidence.InitialObservation is not null)
                throw new TransitionStatePreparationException("transition.state.subjectAlreadyExists",
                    "/decision/evidence/initialObservation", "A creation decision cannot be applied to an existing subject.");
            if (current.Observation.ShapeId != entity.StateShape.QualifiedId)
                throw new TransitionStatePreparationException("transition.state.shapeMismatch", "/current/observation",
                    "The current observation belongs to a different entity shape.");
            // Read immutable snapshot fields directly; only the resulting candidate needs construction.
            initial = ObservationValue.FromObject(current.Observation.Fields);
            if (decision.Kind == TransitionDecisionKind.Applied && current.Version == long.MaxValue)
                throw new TransitionStatePreparationException("transition.state.versionOverflow", "/current/version",
                    "The existing version cannot be incremented.");
            version = decision.Kind == TransitionDecisionKind.Applied ? current.Version + 1 : current.Version;
        }
        var projected = Apply(initial, decision);
        try
        {
            var candidate = entity.CreateState(entityId, projected.Fields!, version);
            if (current is null)
                entity.ValidateState(candidate);
            return candidate;
        }
        catch (SemanticRuleViolationException exception)
        {
            throw new TransitionStatePreparationException("transition.state.observationInvalid",
                current is null ? "/decision/evidence/initialObservation" : "/decision/candidateObservation",
                exception.Message, exception);
        }
    }
}
