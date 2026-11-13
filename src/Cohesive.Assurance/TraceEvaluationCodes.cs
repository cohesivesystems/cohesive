using Cohesive.Execution;

namespace Cohesive.Assurance;

/// <summary>Stable explanation and diagnostic identities emitted by reference trace evaluation.</summary>
public static class TraceEvaluationCodes
{
    /// <summary>Additional compatible alternatives may exist.</summary>
    public const string EnumerationIncomplete = "assurance.enumeration.incomplete";

    /// <summary>The observation contract does not map every required selector.</summary>
    public const string MappingUnsupported = "assurance.mapping.unsupported";

    /// <summary>The operation may have committed or aborted.</summary>
    public const string CommitOutcomeUnknown = "assurance.commit.unknownOutcome";

    /// <summary>The individual result comes from the state or transition predicate.</summary>
    public const string Predicate = "assurance.predicate";

    /// <summary>Required sources have not closed the enclosing selected scope.</summary>
    public const string ScopeOpen = "assurance.scope.open";

    /// <summary>The deadline is outside the supported clock domain.</summary>
    public const string DeadlineOverflow = "assurance.deadline.overflow";

    /// <summary>A declared batch or comparison limit was exhausted.</summary>
    public const string BudgetExceeded = "assurance.budget.exhausted";

    /// <summary>A qualifying correlated witness was observed.</summary>
    public const string WitnessMatched = "assurance.witness.matched";

    /// <summary>Candidate matching depends on missing observations.</summary>
    public const string MatchMissingEvidence = "assurance.match.missingEvidence";

    /// <summary>Absence is not justified by complete earlier history.</summary>
    public const string HistoryIncomplete = "assurance.history.incomplete";

    /// <summary>No witness is established and the deadline is not complete.</summary>
    public const string ResponsePending = "assurance.response.pending";

    /// <summary>Complete relevant observations establish that no matching witness exists.</summary>
    public const string WitnessAbsent = "assurance.witness.absent";

    /// <summary>An input is incomplete or violates its declared value contract.</summary>
    public const string InputContractGap = "assurance.input.contract";

    /// <summary>Observation identities, order, mapping or completeness are contradictory or malformed.</summary>
    public const string InvalidObservation = "assurance.observation.invalid";

    internal static string Expression(PortableExpressionEvaluationError error) => "assurance.expression." + error;
}
