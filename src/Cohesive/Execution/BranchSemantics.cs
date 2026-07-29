namespace Cohesive.Execution;

/// <summary>Selection semantics for ordered semantic Choice and Match cases.</summary>
public enum CaseSelection
{
    /// <summary>No case-selection rule was supplied; this value is invalid in canonical IR.</summary>
    Unspecified = 0,

    /// <summary>Evaluate cases in order and select the first predicate or exact pattern that matches.</summary>
    OrderedFirstMatch = 1
}

/// <summary>How a branching construct declares that all possible inputs are covered.</summary>
public enum BranchCompleteness
{
    /// <summary>No completeness contract was supplied; this value is invalid in canonical IR.</summary>
    Unspecified = 0,

    /// <summary>The declared cases are intended to be exhaustive and must be proven by compilation.</summary>
    Exhaustive = 1,

    /// <summary>An explicit fallback provides coverage when no declared case matches.</summary>
    Fallback = 2
}
