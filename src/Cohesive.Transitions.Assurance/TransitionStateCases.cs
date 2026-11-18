using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Assurance;

/// <summary>Inclusive valid domain for an integer pre-state field.</summary>
public readonly record struct StateIntegerRange(int Minimum, int Maximum);

/// <summary>One synthetic pre-state and the guard-derived commands for it. Contains no expected outcomes.</summary>
public sealed record TransitionStateCases<TEntity, TInput>(TEntity State, TransitionInputCases<TInput> Commands);

public sealed partial class TransitionProbe<TEntity, TInput, TOutcome>
{
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
        // Validate the supported guard profile using the same authority as command generation.
        var initial = GenerateInputs(template, input, maxCases);
        var root = ObservationValue.FromObject(template);
        if (!plan.Definition.Observation.IsSatisfiedByConstant(root))
            throw new ArgumentException("Template violates the observation contract.", nameof(template));
        var states = new List<ObservationValue> { root };
        var visited = new HashSet<FieldPath>();
        foreach (var rule in plan.Definition.Preconditions)
        {
            var field = rule.Predicate switch
            {
                FieldExpr direct => direct,
                UnaryExpr { Operand: FieldExpr flag } => flag,
                BinaryExpr { Right: FieldExpr right } => right,
                BinaryExpr { Left: FieldExpr left } => left,
                _ => null
            };
            if (field is null || !visited.Add(field.Path)) continue;
            if (!root.TryGetField(field.Path, out var original))
                throw Unsupported(rule.Id, "missing template state field");
            ObservationValue[] values;
            if (original.TryGetBoolean(out _))
                values = [ObservationValue.FromBool(false), ObservationValue.FromBool(true)];
            else
            {
                if (!original.TryGetInt32(out var seed) || !ranges.TryGetValue(field.Path, out var range)
                    || seed < range.Minimum || seed > range.Maximum)
                    throw new ArgumentException($"Provide a valid integer range containing the template at {field.Path}.", nameof(ranges));
                long[] boundaries = [range.Minimum, (long)range.Minimum + 1, seed, (long)range.Maximum - 1, range.Maximum];
                values = boundaries.Where(n => n >= range.Minimum && n <= range.Maximum)
                    .Distinct().Order().Select(ObservationValue.FromInt64).ToArray();
            }
            if ((long)states.Count * values.Length > maxCases)
                throw new InvalidOperationException($"State exploration budget {maxCases} exceeded at {rule.Id.Value}.");
            states = [.. states.SelectMany(state => values.Select(value => state.WithField(field.Path, value)))];
        }
        var remaining = maxCases;
        var result = ImmutableArray.CreateBuilder<TransitionStateCases<TEntity, TInput>>();
        foreach (var observation in states)
        {
            if (!plan.Definition.Observation.IsSatisfiedByConstant(observation)) continue;
            var state = observation.Deserialize<TEntity>() ?? throw new InvalidOperationException("No typed generated state.");
            if (validState is not null && !validState(state)) continue;
            if (remaining == 0) throw new InvalidOperationException($"Joint case budget {maxCases} exceeded.");
            var commands = observation.Equals(root) ? initial : GenerateInputs(state, input, remaining);
            remaining -= commands.Candidates.Length;
            if (remaining < 0) throw new InvalidOperationException($"Joint case budget {maxCases} exceeded.");
            result.Add(new(state, commands));
        }
        if (result.Count == 0) throw new InvalidOperationException("No candidate state satisfies the supplied domain.");
        return result.ToImmutable();
    }
}
