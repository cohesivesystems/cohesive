namespace Cohesive.Assurance;

// Shared record-shape and identity boundary. Batch owns claim/order validation; the monitor
// additionally owns continuity against previously accepted completeness certificates.
internal static class TraceObservationValidation
{
    internal static (string Partition, string Occurrence) Key(SemanticObservation observation) =>
        (observation.Partition, observation.Occurrence);
    internal static (ClaimReference Selector, string Partition) Key(ObservationCompleteness frontier) =>
        (frontier.Selector, frontier.Partition);

    internal static TraceEvaluationDiagnostic? Observe(SemanticObservation observation,
        Dictionary<(string Partition, string Occurrence), SemanticObservation> seen, out bool duplicate)
    {
        duplicate = false;
        if (observation is null || observation.Selector is null || observation.Values is null
            || string.IsNullOrWhiteSpace(observation.Occurrence) || string.IsNullOrWhiteSpace(observation.Partition)
            || observation.Step < 0 || observation.Ticks < 0 || !Enum.IsDefined(observation.Kind)
            || observation.Values.Keys.Any(x => !Enum.IsDefined(x))
            || observation.Kind == SemanticObservationKind.Event && observation.Values.Count != 0
            || observation.Kind != SemanticObservationKind.Event && observation.EventPayload is not null)
            return Invalid("/observations", "An observation has invalid identity, role, time, kind or selector.");
        if (seen.TryGetValue(Key(observation), out var previous))
        {
            if (!previous.SameContent(observation))
                return Invalid(observation.Occurrence, "Duplicate identity has contradictory content; append a new correction epoch instead.");
            duplicate = true;
        }
        else seen.Add(Key(observation), observation);
        return null;
    }

    internal static TraceEvaluationDiagnostic? Register(ObservationCompleteness frontier,
        Dictionary<(ClaimReference Selector, string Partition), ObservationCompleteness> seen)
    {
        if (frontier is null || frontier.Selector is null || frontier.Evidence is null
            || string.IsNullOrWhiteSpace(frontier.Partition)
            || frontier.ThroughStep < 0 || frontier.ThroughTicks < 0 || !Enum.IsDefined(frontier.Closure)
            || !seen.TryAdd(Key(frontier), frontier))
            return Invalid("/completeness", "Frontiers require unique source/selector keys, nonnegative bounds and exact evidence.");
        return null;
    }

    static TraceEvaluationDiagnostic Invalid(string location, string message) =>
        new(TraceEvaluationCodes.InvalidObservation, location, message);
}
