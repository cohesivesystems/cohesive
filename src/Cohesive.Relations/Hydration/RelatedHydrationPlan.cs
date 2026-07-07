using Cohesive.Relations.Model;
using Cohesive.Model;

namespace Cohesive.Relations.Hydration;

/// <summary>
/// Related-schema hydration request for <c>relatedField</c> expressions.
/// </summary>
public sealed record RelatedHydrationPlan(
    ShapeId Schema,
    IReadOnlyList<string> Fields,
    IReadOnlyList<Expr> LookupKeyExpressions
);
