namespace Cohesive.Relations.Hydration;

/// <summary>
/// Relation hydration plan with explicit field selection.
/// </summary>
/// <param name="RootSchema">Schema of the root observations to hydrate.</param>
/// <param name="RootFields">Canonical root field names required by relation evaluation.</param>
/// <param name="Related">Hydration plans for related observation schemas required by the relation.</param>
public sealed record RelationHydrationPlan(
    ShapeId RootSchema,
    IReadOnlyList<string> RootFields,
    IReadOnlyList<RelatedHydrationPlan> Related
);
