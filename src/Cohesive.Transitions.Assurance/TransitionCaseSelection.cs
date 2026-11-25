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
    int CandidateCount, ImmutableArray<CaseCoverageGoal> UncoveredGoals)
{
    /// <summary>First witnesses of distinct single-field decision changes, with zero-based indices into Cases.</summary>
    public ImmutableArray<DecisionIsolationPair> DecisionPairs { get; init; } = [];
}

/// <summary>Two selected cases differing in one field that change admission or the accepted movement path.</summary>
public sealed record DecisionIsolationPair(int FirstCase, int SecondCase, string Scope, FieldPath Field,
    string FirstDecision, string SecondDecision);

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
        var accepted = new List<bool>();
        var observations = new List<(ObservationValue State, ObservationValue Input)>();
        foreach (var group in groups)
        {
            var state = ObservationValue.FromObject(group.State);
            foreach (var candidate in group.Commands.Candidates)
            {
                var decision = Execute(group.State, candidate.Input, new($"selection/{pool.Count}")).Decision;
                var command = ObservationValue.FromObject(candidate.Input);
                observations.Add((state, command));
                accepted.Add(decision.IsAccepted);
                pool.Add(new(group.State, candidate.Input, InspectCase(state, command, decision)));
            }
        }

        var axes = GenerationGuards.SelectMany(g => g.Fields.Select(f => new Axis(false, f)))
            .Concat(GenerationGuards.Where(g => g.Input.HasValue).Select(g => new Axis(true, g.Input!.Value))).ToList();
        if (plan.Definition.MachineBinding is { } binding)
            axes.Add(new(false, binding.States.Values.First().Assignments.Single().Path));
        return SelectSuite(pool, observations, accepted, [.. axes.Distinct()], options.MaxCases);
    }

    readonly record struct Axis(bool Input, FieldPath Path);
    readonly record struct Contrast(Axis Axis, string Low, string High);
    sealed record Witness(int First, Axis Axis, Contrast Goal, long Distance);

    static SelectedTransitionCases<TEntity, TInput> SelectSuite(List<SelectedTransitionCase<TEntity, TInput>> pool,
        List<(ObservationValue State, ObservationValue Input)> observations, List<bool> accepted, Axis[] axes, int budget)
    {
        var frequency = pool.SelectMany(c => c.CoveredGoals).GroupBy(g => g).ToDictionary(g => g.Key, g => g.Count());
        var remaining = frequency.Keys.ToHashSet();
        var paths = pool.Select(DecisionPath).ToArray();
        var covered = new HashSet<Contrast>();
        var witnesses = pool.Select(_ => new List<Witness>()).ToArray();
        var chosen = new List<int>();
        var available = Enumerable.Range(0, pool.Count).ToList();
        var pairs = ImmutableArray.CreateBuilder<DecisionIsolationPair>();
        // Equal keys for an axis mean the complete state and command agree outside that field.
        var buckets = axes.ToDictionary(axis => axis, axis => observations.Select((o, i) => (o, i))
            .ToLookup(x => Mask(x.o, axis), x => x.i));
        while (chosen.Count < budget && available.Count > 0)
        {
            var best = available.Select(i =>
            {
                var fresh = witnesses[i].Where(w => !covered.Contains(w.Goal)).ToArray();
                var facts = pool[i].CoveredGoals.Where(remaining.Contains).ToArray();
                return (Index: i, Contrasts: fresh.Select(w => w.Goal).Distinct().Count(), Gain: facts.Length,
                    Distance: fresh.Length == 0 ? 0 : fresh.Min(w => w.Distance), Rarity: facts.Sum(g => 1.0 / frequency[g]));
            }).OrderByDescending(c => c.Contrasts).ThenByDescending(c => c.Gain).ThenBy(c => c.Distance)
                .ThenByDescending(c => c.Rarity).First().Index;
            var position = chosen.Count;
            foreach (var w in witnesses[best])
                if (covered.Add(w.Goal))
                    pairs.Add(new(w.First, position, w.Axis.Input ? "input" : "state", w.Axis.Path,
                        paths[chosen[w.First]], paths[best]));
            chosen.Add(best);
            available.Remove(best);
            remaining.ExceptWith(pool[best].CoveredGoals);
            foreach (var axis in axes)
            {
                var key = Mask(observations[best], axis);
                foreach (var neighbor in buckets[axis][key])
                {
                    if (paths[best] == paths[neighbor]) continue;
                    if (!accepted[best] && !accepted[neighbor]) continue;
                    var (low, high) = string.CompareOrdinal(paths[best], paths[neighbor]) < 0
                        ? (paths[best], paths[neighbor]) : (paths[neighbor], paths[best]);
                    var goal = new Contrast(axis, low, high);
                    if (!covered.Contains(goal))
                        witnesses[neighbor].Add(new(position, axis, goal, Distance(observations[best], observations[neighbor], axis)));
                }
            }
        }
        return new([.. chosen.Select(i => pool[i])], pool.Count, [.. frequency.Keys.Where(remaining.Contains)])
            { DecisionPairs = pairs.ToImmutable() };
    }

    static string DecisionPath(SelectedTransitionCase<TEntity, TInput> c) =>
        string.Join("/", c.CoveredGoals.Where(g => g.Second is null).Select(g => g.First)
            .Where(f => f.Kind is "decision" or "edge" || f is { Kind: "admission", Detail: "false" })
            .Select(f => f.Kind == "decision" ? f.Detail : f.Kind + ":" + Uri.EscapeDataString(f.Node)));

    static (ObservationValue, ObservationValue) Mask((ObservationValue State, ObservationValue Input) o, Axis axis) =>
        axis.Input ? (o.State, o.Input.WithoutField(axis.Path)) : (o.State.WithoutField(axis.Path), o.Input);

    static long Distance((ObservationValue State, ObservationValue Input) a, (ObservationValue State, ObservationValue Input) b, Axis axis)
    {
        var left = axis.Input ? a.Input : a.State;
        var right = axis.Input ? b.Input : b.State;
        return left.TryGetField(axis.Path, out var x) && x.TryGetInt32(out var n)
            && right.TryGetField(axis.Path, out var y) && y.TryGetInt32(out var m) ? Math.Abs((long)n - m) : 1;
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
