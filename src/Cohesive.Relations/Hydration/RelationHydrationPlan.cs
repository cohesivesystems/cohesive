using Cohesive.Relations.Model;
using Cohesive.Model;

namespace Cohesive.Relations.Hydration;

/// <summary>
/// Relation hydration plan with explicit field selection.
/// </summary>
public sealed record RelationHydrationPlan(
    ShapeId RootSchema,
    IReadOnlyList<string> RootFields,
    IReadOnlyList<RelatedHydrationPlan> Related
);
