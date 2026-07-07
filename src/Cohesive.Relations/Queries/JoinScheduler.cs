namespace Cohesive.Relations.Queries;

/// <summary>
/// Ordered execution plan for projection joins.
/// </summary>
/// <remarks>
/// The scheduler groups joins into stages such that every join in a given stage has all of its
/// dependencies satisfied by earlier stages. Joins within the same stage are independent of one
/// another and can therefore be executed in any order or in parallel if the caller chooses.
/// </remarks>
sealed record JoinSchedule(IReadOnlyList<JoinStage> Stages);

/// <summary>
/// A single layer in a <see cref="JoinSchedule" />.
/// </summary>
/// <param name="Level">
/// Zero-based stage index. Stage <c>0</c> contains joins that depend only on the projection root,
/// stage <c>1</c> contains joins whose prerequisites are satisfied by stage <c>0</c>, and so on.
/// </param>
/// <param name="Joins">Joins that become executable at this level.</param>
sealed record JoinStage(int Level, IReadOnlyList<JoinSpec> Joins);

/// <summary>
/// Builds a dependency-respecting execution order for projection joins.
/// </summary>
/// <remarks>
/// The algorithm is a staged topological sort using Kahn's algorithm:
/// <list type="number">
/// <item>
/// It first indexes joins by alias so that every <see cref="JoinSpec.FromAlias" /> dependency can
/// be validated up front.
/// </item>
/// <item>
/// It then constructs an in-degree table and adjacency list. A join contributes one incoming edge
/// when it depends on another alias, and that dependency is only legal when the parent join has
/// <see cref="JoinCardinality.One" />, because nested joins cannot fan out from a many-valued join.
/// </item>
/// <item>
/// All joins with in-degree zero are emitted as the current stage. Removing that stage from the
/// graph may unlock additional joins whose in-degree then drops to zero, forming the next stage.
/// </item>
/// <item>
/// If the number of emitted joins does not match the number declared by the plan, the remaining
/// joins are part of a cycle, so scheduling fails with an exception.
/// </item>
/// </list>
/// This produces a stable layering of the join graph that the hydration engine can execute stage by
/// stage while preserving alias dependencies.
/// </remarks>
static class JoinScheduler
{
    /// <summary>
    /// Computes a staged topological schedule for the joins in <paramref name="joins" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a join references an unknown alias, when a nested join depends on a many-valued
    /// join, or when the dependency graph contains a cycle.
    /// </exception>
    public static JoinSchedule Schedule(IReadOnlyList<JoinSpec> joins)
    {
        if (joins.Count == 0)
            return new([]);

        var byAlias = joins.ToDictionary(join => join.Alias, StringComparer.Ordinal);

        foreach (var join in joins)
        {
            if (join.FromAlias is not null && !byAlias.ContainsKey(join.FromAlias))
            {
                throw new InvalidOperationException(
                    $"Join '{join.Alias}' depends on unknown alias '{join.FromAlias}'.");
            }
        }

        var indegree = joins.ToDictionary(join => join, static _ => 0);
        var outgoing = joins.ToDictionary(join => join, static _ => new List<JoinSpec>());

        foreach (var join in joins)
        {
            if (join.FromAlias is null)
                continue;

            var parent = byAlias[join.FromAlias];
            if (parent.Cardinality != JoinCardinality.One)
                throw new InvalidOperationException($"Join '{join.Alias}' depends on '{join.FromAlias}', but nested joins can only depend on one-to-one joins.");

            outgoing[parent].Add(join);
            indegree[join]++;
        }

        List<JoinStage> stages = [];
        var current = indegree
            .Where(static pair => pair.Value == 0)
            .Select(static pair => pair.Key)
            .ToList();

        var level = 0;
        var visited = 0;
        while (current.Count > 0)
        {
            stages.Add(new(level, [..current]));

            List<JoinSpec> next = [];
            foreach (var join in current)
            {
                visited++;
                foreach (var child in outgoing[join])
                {
                    indegree[child]--;
                    if (indegree[child] == 0)
                        next.Add(child);
                }
            }

            current = next;
            level++;
        }

        if (visited != joins.Count)
            throw new InvalidOperationException("Projection joins must form an acyclic graph.");

        return new(stages);
    }
}
