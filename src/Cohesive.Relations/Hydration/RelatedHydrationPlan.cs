namespace Cohesive.Relations.Hydration;

/// <summary>
/// Related-schema hydration request for <c>relatedField</c> expressions.
/// </summary>
/// <remarks>
/// This type supports the prototype <c>relatedField</c> hydration path and is not a relationship
/// declaration. New relationship semantics belong in a
/// <see cref="Cohesive.Relations.Model.RelationshipCatalog"/> and are traversed through canonical
/// relation/query IR. A future executor migration can derive equivalent hydration work from that
/// catalog-bound IR without treating this compatibility plan as another semantic authority.
/// </remarks>
/// <param name="Schema">Schema of the related observations to hydrate.</param>
/// <param name="Fields">Canonical related field names required by relation evaluation.</param>
/// <param name="LookupKeyExpressions">Expressions that resolve related observation keys from each root observation.</param>
public sealed record RelatedHydrationPlan(
    ShapeId Schema,
    IReadOnlyList<string> Fields,
    IReadOnlyList<Expr> LookupKeyExpressions
);
