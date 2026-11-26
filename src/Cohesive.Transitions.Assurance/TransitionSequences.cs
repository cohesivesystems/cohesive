using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Assurance;

public static partial class TransitionAssurance
{
    /// <summary>Register the finite public command vocabulary for a bounded offline sequence search.</summary>
    public static SequenceProbe<TEntity> ForSequences<TEntity>() where TEntity : notnull => new();
}

/// <summary>Limits on returned suites and bounded reference execution.</summary>
public sealed record SequenceSelectionOptions
{
    /// <summary>Maximum distinct sequences, including required scenarios.</summary>
    public required int MaxSequences { get; init; }
    /// <summary>Maximum commands per sequence from the seed.</summary>
    public required int MaxStepsPerSequence { get; init; }
    /// <summary>Maximum offline step executions, including required commands.</summary>
    public int MaxCandidates { get; init; } = 4096;
    /// <summary>Maximum histories expanded at each exploration depth.</summary>
    public int BeamWidth { get; init; } = 32;
}

/// <summary>A command's observed decision and whether its full modeled state changed.</summary>
public sealed record SequenceDecision(string Command, TransitionDecisionKind Decision, bool Changed);
/// <summary>An observed decision alone or immediately after another decision.</summary>
public sealed record SequenceGoal(SequenceDecision? Previous, SequenceDecision Current);
/// <summary>One replayable command path and its observed coverage.</summary>
public sealed record SelectedSequence(ImmutableArray<string> Commands, ImmutableArray<SequenceGoal> CoveredGoals);
/// <summary>A bounded suite and the observed coverage remaining after selection.</summary>
public sealed record SequenceSelection(ImmutableArray<SelectedSequence> Sequences, int CandidateCount,
    bool SearchTruncated, ImmutableArray<SequenceGoal> UncoveredGoals) : ICoverageSelection<SequenceGoal>;

/// <summary>Explores sequences from one seed using the production reference interpreter for every step.</summary>
public sealed class SequenceProbe<TEntity> where TEntity : notnull
{
    delegate (TEntity State, TransitionDecisionKind Decision, bool IsAccepted) Run(TEntity state, int step);
    readonly Dictionary<string, Run> commands = new(StringComparer.Ordinal);

    readonly List<ImmutableArray<string>> required = [];

    /// <summary>Validate and snapshot a mandatory command path against this vocabulary at setup.</summary>
    public SequenceProbe<TEntity> RequireScenario(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Length == 0 || required.Any(p => p.SequenceEqual(names)))
            throw new ArgumentException("Empty or duplicate required scenario.", nameof(names));
        foreach (var name in names)
            if (!commands.ContainsKey(name))
                throw new ArgumentException($"Unknown required command '{name}'.", nameof(names));
        required.Add([.. names]);
        return this;
    }

    /// <summary>Execute the registered commands in order, carrying the interpreter state into each step.</summary>
    public IEnumerable<(TEntity State, TransitionDecisionKind Decision, bool IsAccepted)> ExecuteSequence(
        TEntity seed, IEnumerable<string> names)
    {
        var state = seed;
        var index = 0;
        foreach (var name in names)
        {
            if (!commands.TryGetValue(name, out var run))
                throw new ArgumentException($"Unknown sequence command '{name}'.", nameof(names));
            var step = run(state, index++);
            state = step.State;
            yield return step;
        }
    }

    /// <summary>Register a parameterless public command.</summary>
    public SequenceProbe<TEntity> Command<TOutcome>(string name, Transition<TEntity, Unit, TOutcome> transition)
        => Command(name, transition, default(Unit));

    /// <summary>Register a public command with a fixed typed argument.</summary>
    public SequenceProbe<TEntity> Command<TInput, TOutcome>(string name,
        Transition<TEntity, TInput, TOutcome> transition, TInput input)
        => Commands(transition, new[] { input }, _ => name);

    /// <summary>Prepare a typed transition once and register a finite set of concrete named inputs.</summary>
    public SequenceProbe<TEntity> Commands<TInput, TOutcome>(Transition<TEntity, TInput, TOutcome> transition,
        IEnumerable<TInput> inputs, Func<TInput, string> name)
    {
        var runner = OfflineTransition.Prepare(transition);
        var entries = inputs.Select(input => (Name: name(input), Input: input)).ToArray();
        var names = new HashSet<string>(commands.Keys, StringComparer.Ordinal);
        foreach (var entry in entries)
            if (string.IsNullOrWhiteSpace(entry.Name) || !names.Add(entry.Name))
                throw new ArgumentException($"Empty or duplicate command name '{entry.Name}'.", nameof(name));
        foreach (var entry in entries)
            commands.Add(entry.Name, (state, step) =>
            {
                var result = runner.Execute(state, entry.Input, new($"sequence-{step}"));
                return (result.State, result.Decision.Kind, result.Decision.IsAccepted);
            });
        return this;
    }

    sealed record Path(TEntity State, ObservationValue Observation, ImmutableArray<string> Commands,
        SequenceDecision? Last, ImmutableHashSet<SequenceGoal> Goals)
    {
        public Path Append(string name, Run run)
        {
            var (state, kind, _) = run(State, Commands.Length);
            var observation = ObservationValue.FromObject(state);
            var current = new SequenceDecision(name, kind, !Observation.Equals(observation));
            var goals = Goals.Add(new(null, current));
            if (Last is not null) goals = goals.Add(new(Last, current));
            return new(state, observation, Commands.Add(name), current, goals);
        }
    }

    /// <summary>Keep required paths, then greedily cover observed decisions and adjacent pairs.
    /// Beam search retains high-coverage histories per full observation and last decision at each depth.
    /// Ties follow ordinal command exploration order. Uncovered goals describe explored paths only.</summary>
    public SequenceSelection SelectSequences(TEntity seed, SequenceSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxStepsPerSequence <= 0 || options.MaxSequences <= 0 ||
            options.BeamWidth <= 0 || options.MaxCandidates <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Selection limits must be positive.");
        if (commands.Count == 0) throw new InvalidOperationException("Register at least one command.");
        var root = new Path(seed, ObservationValue.FromObject(seed), [], null, []);
        var selected = new List<Path>();
        var explored = 0;
        foreach (var scenario in required)
        {
            if (scenario.Length > options.MaxStepsPerSequence ||
                selected.Count >= options.MaxSequences || scenario.Length > options.MaxCandidates - explored)
                throw new ArgumentException("Invalid, duplicate or over-budget required scenario.", nameof(options));
            var path = root;
            foreach (var name in scenario)
            {
                path = path.Append(name, commands[name]);
                explored++;
            }
            selected.Add(path);
        }

        var candidates = new List<Path>();
        var frontier = new List<Path> { root };
        var vocabulary = commands.OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();
        bool truncated = false;
        for (var depth = 0; depth < options.MaxStepsPerSequence; depth++)
        {
            var next = new List<Path>();
            foreach (var path in frontier)
            {
                foreach (var (name, run) in vocabulary)
                {
                    if (explored == options.MaxCandidates) { truncated = true; break; }
                    next.Add(path.Append(name, run));
                    explored++;
                }
                if (explored == options.MaxCandidates) break;
            }
            candidates.AddRange(next);
            if (explored == options.MaxCandidates)
            {
                truncated |= depth + 1 < options.MaxStepsPerSequence ||
                    next.Count < (long)frontier.Count * vocabulary.Length;
                break;
            }
            if (depth + 1 == options.MaxStepsPerSequence) break;
            var ranked = next.OrderByDescending(p => p.Goals.Count)
                .DistinctBy(p => (p.Observation, p.Last)).Take(options.BeamWidth).ToList();
            truncated |= ranked.Count < next.Count;
            frontier = ranked;
        }

        var covered = selected.SelectMany(p => p.Goals).ToHashSet();
        var remaining = candidates.SelectMany(p => p.Goals).ToHashSet();
        remaining.ExceptWith(covered);
        var extra = CoverageSelection.Pick<SequenceGoal, (int Gain, int Length)>(
            candidates.Count, options.MaxSequences - selected.Count, i => candidates[i].Goals, remaining,
            (i, gain) => gain > 0 ? (gain, -candidates[i].Commands.Length) : null);
        selected.AddRange(extra.Select(i => candidates[i]));
        return new([.. selected.Select(p => new SelectedSequence(p.Commands, Ordered(p.Goals)))],
            explored, truncated, Ordered(remaining));
    }

    static ImmutableArray<SequenceGoal> Ordered(IEnumerable<SequenceGoal> goals) =>
        [.. goals.OrderBy(g => g.Previous?.Command, StringComparer.Ordinal)
            .ThenBy(g => g.Previous?.Decision).ThenBy(g => g.Previous?.Changed)
            .ThenBy(g => g.Current.Command, StringComparer.Ordinal)
            .ThenBy(g => g.Current.Decision).ThenBy(g => g.Current.Changed)];
}
