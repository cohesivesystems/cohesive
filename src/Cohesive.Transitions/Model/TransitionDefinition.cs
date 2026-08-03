using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Transitions.Model;

/// <summary>
/// Declarative state transition definition for an entity.
/// </summary>
/// <remarks>
/// This flat definition is a temporary compatibility surface for existing builders and
/// <c>DeclarativeEntityRuntime</c>. Canonical persisted execution-kernel semantics are represented by
/// <c>Cohesive.Transitions.IR.TransitionDefinition</c>; this type is not persisted kernel authority. The remaining
/// compatibility surface is owned by ARI-218 and must not acquire new production consumers.
/// </remarks>
public sealed record TransitionDefinition
{
    /// <summary>
    /// Creates a transition definition.
    /// </summary>
    [JsonConstructor]
    public TransitionDefinition(
        string name,
        ImmutableArray<TransitionParameterDefinition> inputs = default,
        ImmutableArray<TransitionPreconditionDefinition> preconditions = default,
        ImmutableArray<FieldUpdateDefinition> updates = default,
        ImmutableArray<EffectDefinition> effects = default,
        ImmutableArray<EntityTypeName> readEntities = default,
        ImmutableArray<EntityTypeName> writeEntities = default,
        ImmutableArray<string> readSet = default,
        ImmutableArray<string> writeSet = default,
        string? description = null,
        ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null
        )
    {
        Name = Guard.RequireNotNullOrWhiteSpace(value: name);
        Inputs = inputs.IsDefault ? [] : inputs;
        Preconditions = preconditions.IsDefault ? [] : preconditions;
        Updates = updates.IsDefault ? [] : updates;
        Effects = effects.IsDefault ? [] : effects;
        WriteEntities = NormalizeEntitySet(writeEntities.IsDefault ? [] : writeEntities);
        ReadEntities = NormalizeEntitySet((readEntities.IsDefault ? [] : readEntities).Concat(WriteEntities));
        WriteSet = NormalizeFieldSet((writeSet.IsDefault ? [] : writeSet).Concat(InferWriteSet(Updates)));
        ReadSet = NormalizeFieldSet((readSet.IsDefault ? [] : readSet).Concat(InferReadSet(Preconditions, Updates, Effects)).Concat(WriteSet));
        Description = description;
        Annotations = AnnotationMap.Normalize(annotations);

        var duplicateInputs = Inputs
            .GroupBy(keySelector: x => x.Name, comparer: StringComparer.Ordinal)
            .FirstOrDefault(predicate: g => g.Count() > 1);

        if (duplicateInputs is not null)
        {
            throw new ArgumentException(
                message: $"Transition '{Name}' contains duplicate input parameter '{duplicateInputs.Key}'.",
                paramName: nameof(inputs)
                );
        }

        var duplicateUpdates = Updates
            .GroupBy(keySelector: x => x.Field, comparer: StringComparer.Ordinal)
            .FirstOrDefault(predicate: g => g.Count() > 1);

        if (duplicateUpdates is not null)
        {
            throw new ArgumentException(
                message: $"Transition '{Name}' contains duplicate update for field '{duplicateUpdates.Key}'.",
                paramName: nameof(updates)
                );
        }
    }

    /// <summary>
    /// Transition name.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Declared transition input parameters.
    /// </summary>
    public ImmutableArray<TransitionParameterDefinition> Inputs { get; init; }

    /// <summary>
    /// Preconditions checked before transition execution.
    /// </summary>
    public ImmutableArray<TransitionPreconditionDefinition> Preconditions { get; init; }

    /// <summary>
    /// Field update expressions produced by the transition.
    /// </summary>
    public ImmutableArray<FieldUpdateDefinition> Updates { get; init; }

    /// <summary>
    /// Effects emitted by the transition.
    /// </summary>
    public ImmutableArray<EffectDefinition> Effects { get; init; }

    /// <summary>
    /// Entity types required to execute this transition.
    /// </summary>
    public ImmutableArray<EntityTypeName> ReadEntities { get; init; }

    /// <summary>
    /// Entity types that may be mutated by this transition.
    /// </summary>
    public ImmutableArray<EntityTypeName> WriteEntities { get; init; }

    /// <summary>
    /// Field names required to execute this transition.
    /// </summary>
    public ImmutableArray<string> ReadSet { get; init; }

    /// <summary>
    /// Field names that may be mutated by this transition.
    /// </summary>
    public ImmutableArray<string> WriteSet { get; init; }

    /// <summary>
    /// Optional descriptive text.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Optional metadata extensions.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }

    internal TransitionDefinition WithOwningEntity(EntityTypeName entityType)
    {
        var writeEntities = NormalizeEntitySet(WriteEntities.Append(entityType));
        var readEntities = NormalizeEntitySet(ReadEntities.Concat(writeEntities));
        return this with
        {
            ReadEntities = readEntities,
            WriteEntities = writeEntities
        };
    }

    static ImmutableArray<EntityTypeName> NormalizeEntitySet(IEnumerable<EntityTypeName> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        return [.. entities
            .Distinct()
            .OrderBy(x => x.Value, StringComparer.Ordinal)];
    }

    static ImmutableArray<string> NormalizeFieldSet(IEnumerable<string> fields)
    {
        return [.. fields
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)];
    }

    static IEnumerable<string> InferWriteSet(IEnumerable<FieldUpdateDefinition> updates)
    {
        foreach (var update in updates)
            yield return update.Field;
    }

    static IEnumerable<string> InferReadSet(
        IEnumerable<TransitionPreconditionDefinition> preconditions,
        IEnumerable<FieldUpdateDefinition> updates,
        IEnumerable<EffectDefinition> effects
        )
    {
        foreach (var precondition in preconditions)
        {
            foreach (var field in EnumerateFieldReferences(precondition.Expression))
                yield return field;
        }

        foreach (var update in updates)
        {
            foreach (var field in EnumerateFieldReferences(update.ValueExpression))
                yield return field;
        }

        foreach (var effect in effects)
        {
            if (effect.Payload is null)
                continue;

            foreach (var field in EnumerateFieldReferences(effect.Payload))
                yield return field;
        }
    }

    static IEnumerable<string> EnumerateFieldReferences(Expr expression)
    {
        switch (expression)
        {
            case FieldExpr field:
                if (field.Path.TryGetTerminalFieldIdentity(out var fieldIdentity))
                    yield return fieldIdentity;
                yield break;

            case ParameterExpr:
            case ConstantExpr:
                yield break;

            case UnaryExpr unary:
                foreach (var fieldRef in EnumerateFieldReferences(unary.Operand))
                    yield return fieldRef;
                yield break;

            case BinaryExpr binary:
                foreach (var fieldRef in EnumerateFieldReferences(binary.Left))
                    yield return fieldRef;

                foreach (var fieldRef in EnumerateFieldReferences(binary.Right))
                    yield return fieldRef;
                yield break;

            case ConditionalExpr conditional:
                foreach (var fieldRef in EnumerateFieldReferences(conditional.Test))
                    yield return fieldRef;

                foreach (var fieldRef in EnumerateFieldReferences(conditional.IfTrue))
                    yield return fieldRef;

                foreach (var fieldRef in EnumerateFieldReferences(conditional.IfFalse))
                    yield return fieldRef;
                yield break;

            case CallExpr function:
                foreach (var argument in function.Arguments)
                {
                    foreach (var fieldRef in EnumerateFieldReferences(argument))
                        yield return fieldRef;
                }
                yield break;
        }

        throw new InvalidOperationException(
            $"Unsupported expression node '{expression.GetType().Name}' while inferring transition dependencies.");
    }
}
