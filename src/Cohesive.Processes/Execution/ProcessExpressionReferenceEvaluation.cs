using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Execution;

/// <summary>Evaluates a Process expression against retained coordination-local binding evidence.</summary>
internal static class ProcessExpressionReferenceEvaluation
{
    /// <summary>Evaluates one expression with an optional local binding that overrides retained state.</summary>
    /// <param name="evaluator">Portable expression evaluator configured for the Process language closure.</param>
    /// <param name="expression">Canonical Process expression to evaluate.</param>
    /// <param name="bindings">Retained bindings visible to the owning token.</param>
    /// <param name="localBinding">Optional local binding identity to overlay for this evaluation.</param>
    /// <param name="localValue">Value paired with <paramref name="localBinding"/>.</param>
    /// <returns>The portable expression value produced from the exact retained binding environment.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="evaluator"/>, <paramref name="expression"/>, or a supplied binding value is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Only one of <paramref name="localBinding"/> and <paramref name="localValue"/> is supplied, or
    /// <paramref name="bindings"/> contains a null or duplicate entry.
    /// </exception>
    public static PortableExpressionValue Evaluate(
        PortableExpressionReferenceEvaluator evaluator,
        Expr expression,
        ImmutableArray<ProcessBindingValue> bindings,
        ValueBindingId? localBinding = null,
        PortableValue? localValue = null)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(expression);
        if (localBinding.HasValue != (localValue is not null))
        {
            throw new ArgumentException("A local Process expression binding requires both identity and value.");
        }

        var capacity = checked(bindings.Length + (localBinding.HasValue ? 1 : 0));
        Dictionary<ValueBindingId, PortableValue> values = new(capacity);
        foreach (var binding in bindings)
        {
            if (binding is null)
            {
                throw new ArgumentException(
                    "Retained Process expression bindings cannot contain a null entry.",
                    nameof(bindings));
            }

            ArgumentNullException.ThrowIfNull(binding.Value);
            if (!values.TryAdd(binding.Binding, binding.Value))
            {
                throw new ArgumentException(
                    $"Retained Process expression binding '{binding.Binding.Value}' is duplicated.",
                    nameof(bindings));
            }
        }

        if (localBinding is { } identity && localValue is { } value)
        {
            values[identity] = value;
        }

        return evaluator.Evaluate(expression, new()
        {
            ResolveBinding = binding => values.TryGetValue(binding, out var value)
                ? PortableExpressionValue.FromPortable(value)
                : PortableExpressionValue.Absent,
            ResolveField = (binding, path) =>
            {
                var selected = binding ?? ProcessBindingIds.Input;
                return values.TryGetValue(selected, out var value)
                    ? PortableExpressionValue.FromPortable(value).Project(path)
                    : PortableExpressionValue.Absent;
            },
            ResolveParameter = _ => PortableExpressionValue.Absent
        });
    }
}
