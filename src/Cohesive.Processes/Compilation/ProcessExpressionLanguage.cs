using Cohesive.Model.Expressions;
using Cohesive.Transitions.Compilation;

namespace Cohesive.Processes.Compilation;

/// <summary>
/// Canonical restricted expression-language closure accepted by Process v2 validation and interpretation.
/// </summary>
/// <remarks>
/// Process v2 deliberately shares the pure expression semantics of Transition v1. Keeping one capability profile as
/// the semantic authority prevents the two execution blocks from acquiring parallel operation catalogs. A future
/// Process language that requires a materially different closure should introduce a new versioned profile.
/// </remarks>
public static class ProcessExpressionLanguage
{
    /// <summary>
    /// Gets the exact pure capabilities admitted by Process v2. Ambient identity, source-set, grouping, and join
    /// operations are intentionally excluded.
    /// </summary>
    public static ExprCapabilityProfile Capabilities { get; } = TransitionExpressionLanguage.Capabilities;
}
