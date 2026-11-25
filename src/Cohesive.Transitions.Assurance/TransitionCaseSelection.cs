using System.Collections.Immutable;
using System.Linq.Expressions;
using Cohesive.Model;
using Cohesive.Transitions.Execution;

namespace Cohesive.Transitions.Assurance;

/// <summary>Separate bounds for candidate exploration and the returned suite.</summary>
public sealed class CaseSelectionOptions<TEntity, TInput>
{
    internal StateCaseOptions<TEntity> States { get; } = new();
    internal StateCaseOptions<TInput> Inputs { get; } = new();
    /// <summary>Maximum number of returned pairs.</summary>
    public int MaxCases { get; init; } = 8;
    /// <summary>Maximum states explored and distinct candidate pairs generated.</summary>
    public int MaxCandidates { get; init; } = 1024;

    /// <summary>Set the inclusive integer exploration range for a state field.</summary>
    public CaseSelectionOptions<TEntity, TInput> StateRange(Expression<Func<TEntity, int>> field, int minimum, int maximum)
    {
        States.Range(field, minimum, maximum);
        return this;
    }

    /// <summary>Set the inclusive integer exploration range for a command field.</summary>
    public CaseSelectionOptions<TEntity, TInput> InputRange(Expression<Func<TInput, int>> field, int minimum, int maximum)
    {
        Inputs.Range(field, minimum, maximum);
        return this;
    }
}

/// <summary>An observation used to guide selection on the clean reference model.</summary>
public sealed record CaseCoverageFact(string Kind, string Node, string Detail);
/// <summary>One observed fact, or the co-occurrence of two boundaries sharing a field.</summary>
public sealed record CaseCoverageGoal(CaseCoverageFact First, CaseCoverageFact? Second = null);
/// <summary>One flat candidate pair and the goals it witnesses, without expected results.</summary>
public sealed record SelectedTransitionCase<TEntity, TInput>(TEntity State, TInput Input, ImmutableArray<CaseCoverageGoal> CoveredGoals);
/// <summary>Deterministic suite and observed goals left uncovered within the suite budget.</summary>
public sealed record SelectedTransitionCases<TEntity, TInput>(ImmutableArray<SelectedTransitionCase<TEntity, TInput>> Cases,
    int CandidateCount, ImmutableArray<CaseCoverageGoal> UncoveredGoals);

public sealed partial class TransitionProbe<TEntity, TInput, TOutcome>
{
    /// <summary>Select bounded state/input pairs using actual admission and movement evidence.
    /// Only observed goals from the generated candidate pool participate in selection.</summary>
    public SelectedTransitionCases<TEntity, TInput> SelectCases(TEntity template, TInput seed, CaseSelectionOptions<TEntity, TInput> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxCases);
        var stateSeed = ObservationValue.FromObject(template);
        foreach (var (field, range) in options.States.Ranges)
        {
            int value = ReadInteger(stateSeed, field);
            if (value < range.Minimum || value > range.Maximum)
                throw new ArgumentOutOfRangeException(nameof(template), $"State field {field} is outside its range.");
        }
        var input = ObservationValue.FromObject(seed);
        foreach (var (field, range) in options.Inputs.Ranges)
        {
            var value = ReadInteger(input, field);
            if (value < range.Minimum || value > range.Maximum)
                throw new ArgumentOutOfRangeException(nameof(seed), $"Input field {field} is outside its range.");
        }
        var groups = GenerateCases(template, seed, options.States, options.MaxCandidates, options.Inputs.Ranges);
        var pool = new List<SelectedTransitionCase<TEntity, TInput>>();
        foreach (var group in groups)
        {
            var state = ObservationValue.FromObject(group.State);
            foreach (var candidate in group.Commands.Candidates)
            {
                var decision = Execute(group.State, candidate.Input, new($"selection/{pool.Count}")).Decision;
                pool.Add(new(group.State, candidate.Input, InspectCase(state, ObservationValue.FromObject(candidate.Input), decision)));
            }
        }

        // Greedy set cover. For equal gains prefer rarer goals, then stable generation order.
        var frequency = pool.SelectMany(c => c.CoveredGoals).GroupBy(g => g).ToDictionary(g => g.Key, g => g.Count());
        var remaining = frequency.Keys.ToHashSet();
        var selected = ImmutableArray.CreateBuilder<SelectedTransitionCase<TEntity, TInput>>();
        while (selected.Count < options.MaxCases && remaining.Count > 0)
        {
            var best = pool.Select(c =>
            {
                var goals = c.CoveredGoals.Where(remaining.Contains).ToArray();
                return (Case: c, Gain: goals.Length, Rarity: goals.Sum(g => 1.0 / frequency[g]));
            }).OrderByDescending(c => c.Gain).ThenByDescending(c => c.Rarity).First().Case;
            selected.Add(best);
            remaining.ExceptWith(best.CoveredGoals);
        }
        return new(selected.ToImmutable(), pool.Count, [.. frequency.Keys.Where(remaining.Contains)]);
    }

    ImmutableArray<CaseCoverageGoal> InspectCase(ObservationValue state, ObservationValue input, TransitionDecision decision)
    {
        var goals = new List<CaseCoverageGoal>();
        var boundaries = new List<(Guard Rule, CaseCoverageFact Fact)>();
        void Fact(string kind, string node, string detail) => goals.Add(new(new(kind, node, detail)));
        Fact("decision", plan.Definition.Body.Id.Value, decision.Kind.ToString());
        if (plan.Definition.MachineBinding is { } machine)
        {
            var path = machine.States.Values.First().Assignments.Single().Path;
            state.TryGetField(path, out var value);
            var name = machine.States.First(pair => pair.Value.Assignments[0].Value.Value == value).Key;
            Fact("state", path.ToString(), name);
        }
        var reached = decision.Evidence.Trace.Where(e => e.Kind == TransitionTraceEventKind.AdmissionEvaluated).ToArray();
        foreach (var entry in reached) Fact("admission", entry.Node.Value, entry.Detail!);
        foreach (var movement in decision.MachineMovements) Fact("edge", movement.Edge.Value, "taken");
        foreach (var rule in GenerationGuards)
        {
            if (rule.MachineGuard
                ? !rule.SelectedGuard || !decision.MachineMovements.Any(m => m.Edge == rule.Id)
                : !reached.Any(e => e.Node == rule.Id)) continue;

            var detail = Boundary(rule, state, input);
            if (detail is null) continue;
            var fact = new CaseCoverageFact(rule.MachineGuard ? "edge-boundary" : "guard-boundary", rule.Id.Value, detail);
            goals.Add(new(fact));
            foreach (var (other, previous) in boundaries)
                if ((other.Input is not null && other.Input == rule.Input) || other.Fields.Intersect(rule.Fields).Any())
                    goals.Add(new(previous, fact));
            boundaries.Add((rule, fact));
        }
        return [.. goals.Distinct()];
    }

    // Signed distances are normalized to command minus threshold (or first state field minus its peer).
    static string? Boundary(Guard rule, ObservationValue state, ObservationValue input)
    {
        var left = rule.Input ?? rule.Fields[0];
        var source = rule.Input.HasValue ? input : state;
        if (rule.Boolean)
        {
            source.TryGetField(left, out var value);
            return $"{left}={value.GetBoolean()}";
        }
        var peer = rule.Constant.HasValue ? default(FieldPath?) : rule.Fields[rule.Input.HasValue ? 0 : 1];
        long delta = (long)ReadInteger(source, left) - (rule.Constant ?? ReadInteger(state, peer!.Value));
        if (delta is < -1 or > 1) return null;
        return $"{left} relative to {rule.Constant?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? peer.ToString()}: {delta}";
    }
}
