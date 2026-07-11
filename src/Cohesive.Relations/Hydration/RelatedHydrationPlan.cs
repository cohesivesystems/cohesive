namespace Cohesive.Relations.Hydration;

/// <summary>
/// Related-schema hydration request for <c>relatedField</c> expressions.
/// </summary>
/// <param name="Schema">Schema of the related observations to hydrate.</param>
/// <param name="Fields">Canonical related field names required by relation evaluation.</param>
/// <param name="LookupKeyExpressions">Expressions that resolve related observation keys from each root observation.</param>
public sealed record RelatedHydrationPlan(
    ShapeId Schema,
    IReadOnlyList<string> Fields,
    IReadOnlyList<Expr> LookupKeyExpressions
);
