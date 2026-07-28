namespace Cohesive.Transitions.IR;

/// <summary>Closed classification of terminal Transition decisions.</summary>
/// <remarks>
/// This type defines only the stable semantic categories required by Transition IR v1. The complete
/// interpreter decision, including its typed outcome, sparse patch, emissions, traces, demands,
/// diagnostics, and conflict evidence, is a separate execution artifact.
/// </remarks>
public enum TransitionDecisionKind
{
    /// <summary>No decision category was supplied; this value is invalid in a completed decision.</summary>
    Unspecified = 0,

    /// <summary>The Transition produced an accepted candidate state containing semantic changes.</summary>
    Applied = 1,

    /// <summary>The Transition was accepted but produced no changed aggregate value.</summary>
    NoChange = 2,

    /// <summary>An ordered admission rule rejected the invocation.</summary>
    AdmissionRejected = 3,

    /// <summary>An authored terminal path produced an alternate domain rejection.</summary>
    DomainRejected = 4,

    /// <summary>Required concurrency observations no longer matched authoritative state.</summary>
    Conflict = 5,

    /// <summary>The Transition definition or an accepted-path invariant was invalid.</summary>
    InvalidDefinition = 6,

    /// <summary>Evaluation or realization failed outside authored domain semantics.</summary>
    InfrastructureFailure = 7
}
