using System.Collections.Immutable;

namespace Cohesive.Transitions.Assurance;

/// <summary>Common coverage evidence for a bounded suite. Goal types retain their domain semantics.</summary>
public interface ICoverageSelection<TGoal>
{
    /// <summary>Number of candidates inspected, including required path steps for sequences.</summary>
    int CandidateCount { get; }
    /// <summary>Observed goals left uncovered by the suite.</summary>
    ImmutableArray<TGoal> UncoveredGoals { get; }
}

internal static class CoverageSelection
{
    // Index goal membership once. Covering a goal decrements the marginal gain of its candidates.
    internal static List<int> Pick<TGoal, TRank>(int count, int budget,
        Func<int, IEnumerable<TGoal>> goals, HashSet<TGoal> remaining,
        Func<int, int, TRank?> rank, Action<int, int>? picked = null)
        where TGoal : notnull where TRank : struct, IComparable<TRank>
    {
        var memberships = new Dictionary<TGoal, List<int>>();
        var gains = new int[count];
        for (var i = 0; i < count; i++)
            foreach (var goal in goals(i))
            {
                if (!remaining.Contains(goal)) continue;
                if (!memberships.TryGetValue(goal, out var members))
                    memberships.Add(goal, members = []);
                members.Add(i);
                gains[i]++;
            }
        var selected = new List<int>();
        var used = new bool[count];
        while (selected.Count < budget)
        {
            TRank? highest = null;
            var best = -1;
            for (var i = 0; i < count; i++)
            {
                if (used[i] || rank(i, gains[i]) is not { } score) continue;
                if (highest.HasValue && score.CompareTo(highest.Value) <= 0) continue;
                highest = score;
                best = i;
            }
            if (best < 0) break;
            used[best] = true;
            picked?.Invoke(best, selected.Count);
            selected.Add(best);
            foreach (var goal in goals(best))
                if (remaining.Remove(goal))
                    foreach (var member in memberships[goal]) gains[member]--;
        }
        return selected;
    }
}
