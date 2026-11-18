using System.Collections.Immutable;
using System.Linq.Expressions;
using Cohesive.Model;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Assurance;

/// <summary>Inclusive valid domain for an integer pre-state field.</summary>
public readonly record struct StateIntegerRange(int Minimum, int Maximum);

/// <summary>Typed state domains and exploration limits for joint generation.</summary>
public sealed class StateCaseOptions<TEntity>
{
    readonly Dictionary<FieldPath, StateIntegerRange> ranges = [];
    internal IReadOnlyDictionary<FieldPath, StateIntegerRange> Ranges => ranges;

    /// <summary>Maximum states explored and maximum state/command pairs returned.</summary>
    public int MaxCases { get; init; } = 1024;
    /// <summary>Optional cross-field invariant applied before proposing commands.</summary>
    public Func<TEntity, bool>? ValidState { get; init; }

    /// <summary>Declare the inclusive domain of a typed integer property.</summary>
    public StateCaseOptions<TEntity> Range(Expression<Func<TEntity, int>> field, int minimum, int maximum)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (minimum > maximum) throw new ArgumentOutOfRangeException(nameof(minimum));
        var boxed = Expression.Lambda<Func<TEntity, object?>>(Expression.Convert(field.Body, typeof(object)), field.Parameters);
        ranges.Add(FieldPath.Capture(boxed), new(minimum, maximum));
        return this;
    }
}

/// <summary>One synthetic pre-state and the guard-derived commands for it. Contains no expected outcomes.</summary>
public sealed record TransitionStateCases<TEntity, TInput>(TEntity State, TransitionInputCases<TInput> Commands);

public sealed partial class TransitionProbe<TEntity, TInput, TOutcome>
{
    /// <summary>Generate joint cases from typed ranges, exploration budget and optional invariant.</summary>
    public ImmutableArray<TransitionStateCases<TEntity, TInput>> GenerateCases(TEntity template, TInput input, StateCaseOptions<TEntity> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return GenerateCases(template, input, options.Ranges, options.MaxCases, options.ValidState);
    }

    /// <summary>Varies inspected Boolean fields and bounded integer thresholds, then proposes commands at each state.</summary>
    /// <param name="template">Valid initial example. Fields outside the inspected guards retain their template values.</param>
    /// <param name="input">Command template for boundary generation.</param>
    /// <param name="ranges">Inclusive domains for integer state fields referenced by supported guards.</param>
    /// <param name="maxCases">Positive bound on generated state/command pairs. Overflow fails without partial results.</param>
    /// <param name="validState">Optional independent predicate for joint state invariants.</param>
    /// <returns>Deterministic synthetic state groups with exact model and input-node provenance.</returns>
    public ImmutableArray<TransitionStateCases<TEntity, TInput>> GenerateCases(
        TEntity template, TInput input, IReadOnlyDictionary<FieldPath, StateIntegerRange> ranges,
        int maxCases = 1024, Func<TEntity, bool>? validState = null)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCases);
        var guards = ResolveGuards();
        var root = ObservationValue.FromObject(template);
        if (!plan.Definition.Observation.IsSatisfiedByConstant(root))
            throw new ArgumentException("Template violates the observation contract.", nameof(template));
        var states = new List<ObservationValue> { root };
        var fields = guards.SelectMany(rule => rule.Fields.Select(path => (path, rule))).GroupBy(pair => pair.path);
        foreach (var group in fields)
        {
            var path = group.Key;
            ObservationValue[] values;
            if (group.First().rule.Boolean)
                values = [ObservationValue.FromBool(false), ObservationValue.FromBool(true)];
            else
            {
                var seed = ReadInteger(root, path);
                if (!ranges.TryGetValue(path, out var range) || seed < range.Minimum || seed > range.Maximum)
                    throw new ArgumentException($"Provide a valid integer range containing the template at {path}.", nameof(ranges));
                SortedSet<long> boundaries = [range.Minimum, (long)range.Minimum + 1, seed, (long)range.Maximum - 1, range.Maximum];
                foreach (var (_, guard) in group)
                {
                    if (guard.Constant is int threshold) AddNeighbors(threshold);
                    foreach (var peer in guard.Fields.Where(peer => peer != path))
                        AddNeighbors(ReadInteger(root, peer));
                }
                void AddNeighbors(int threshold)
                {
                    boundaries.Add((long)threshold - 1);
                    boundaries.Add(threshold);
                    boundaries.Add((long)threshold + 1);
                }
                values = [.. boundaries.Where(n => n >= range.Minimum && n <= range.Maximum).Select(ObservationValue.FromInt64)];
            }
            if ((long)states.Count * values.Length > maxCases)
                throw new InvalidOperationException($"State exploration budget {maxCases} exceeded at {path}.");
            states = [.. states.SelectMany(state => values.Select(value => state.WithField(path, value)))];
        }
        var remaining = maxCases;
        var result = ImmutableArray.CreateBuilder<TransitionStateCases<TEntity, TInput>>();
        foreach (var observation in states)
        {
            if (!plan.Definition.Observation.IsSatisfiedByConstant(observation)) continue;
            var state = observation.Deserialize<TEntity>() ?? throw new InvalidOperationException("No typed generated state.");
            if (validState is not null && !validState(state)) continue;
            var commands = GenerateInputs(state, input, maxCases, int.MinValue, int.MaxValue, guards);
            if (commands.Candidates.Length > remaining)
                throw new InvalidOperationException($"Joint case budget {maxCases} exceeded.");
            remaining -= commands.Candidates.Length;
            result.Add(new(state, commands));
        }
        if (result.Count == 0) throw new InvalidOperationException("No candidate state satisfies the supplied domain.");
        return result.ToImmutable();
    }
}
