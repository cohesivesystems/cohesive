using System.Collections.Immutable;
using System.Reflection;
using Cohesive.Transitions.Model;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Default compiler for expression-authored transition definitions.
/// </summary>
public sealed class TransitionExpressionCompiler : ITransitionExpressionCompiler
{
    readonly IClrTypeRefMapper typeRefMapper;
    readonly NullabilityInfoContext nullabilityContext = new();

    /// <summary>
    /// Creates a compiler with an optional CLR type mapper override.
    /// </summary>
    public TransitionExpressionCompiler(IClrTypeRefMapper? typeRefMapper = null)
    {
        this.typeRefMapper = typeRefMapper ?? new DefaultClrTypeRefMapper();
    }

    /// <summary>
    /// Compiles typed transition expressions into an immutable transition definition.
    /// </summary>
    public TransitionDefinition Compile<TEntity, TParameters>(
        EntityDefinition entityDefinition,
        string transitionName,
        Action<TransitionExpressionBuilder<TEntity, TParameters>> configure
        ) where TEntity : Entity
    {
        ArgumentNullException.ThrowIfNull(entityDefinition);
        ArgumentNullException.ThrowIfNull(configure);

        var parameters = BuildParameters(typeof(TParameters));
        var parameterNames = parameters.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var builder = new TransitionExpressionBuilder<TEntity, TParameters>(entityDefinition, parameterNames);
        configure(builder);
        var transition = builder.Build(transitionName, parameters);
        return AlignDirectAssignmentParameterTypes(entityDefinition, transition);
    }

    ImmutableArray<TransitionParameterDefinition> BuildParameters(Type parametersType)
    {
        ArgumentNullException.ThrowIfNull(parametersType);

        var properties = parametersType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.GetMethod is not null && !x.GetMethod.IsStatic && x.GetIndexParameters().Length == 0)
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();

        var duplicates = properties
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        
        if (duplicates is not null)
            throw new TransitionExpressionTranslationException(message: $"Transition parameter type '{parametersType.Name}' contains duplicate property '{duplicates.Key}'.");

        List<TransitionParameterDefinition> parameters = [];
        foreach (var property in properties)
        {
            var nullability = nullabilityContext.Create(property);
            var type = typeRefMapper.Map(property.PropertyType, nullability);
            var isRequired = nullability.ReadState != NullabilityState.Nullable;
            parameters.Add(new(property.Name, type, isRequired: isRequired));
        }

        return [.. parameters];
    }

    static TransitionDefinition AlignDirectAssignmentParameterTypes(EntityDefinition entityDefinition, TransitionDefinition transition)
    {
        Dictionary<string, TypeRef>? assignedParameterTypes = null;
        foreach (var update in transition.Updates)
        {
            if (update.ValueExpression is not ParameterExpr parameter)
                continue;

            var field = entityDefinition.Fields.FirstOrDefault(candidate => candidate.Name.Value == update.Field);
            if (field is null)
                continue;

            var fieldParameterType = GetFieldParameterType(field);
            assignedParameterTypes ??= new(StringComparer.Ordinal);
            if (assignedParameterTypes.TryGetValue(parameter.Parameter, out var existingType)
                && existingType != fieldParameterType)
            {
                throw new TransitionExpressionTranslationException(
                    $"Transition '{transition.Name}' assigns parameter '{parameter.Parameter}' to fields with incompatible semantic types.");
            }

            assignedParameterTypes[parameter.Parameter] = fieldParameterType;
        }

        if (assignedParameterTypes is null)
            return transition;

        var updatedInputs = transition.Inputs;
        var changed = false;
        for (var i = 0; i < updatedInputs.Length; i++)
        {
            var input = updatedInputs[i];
            if (!assignedParameterTypes.TryGetValue(input.Name, out var assignedType) || input.Type == assignedType)
                continue;

            updatedInputs = updatedInputs.SetItem(i, input with { Type = assignedType });
            changed = true;
        }

        return changed
            ? transition with { Inputs = updatedInputs }
            : transition;
    }

    static TypeRef GetFieldParameterType(FieldDefinition field) =>
        field.Cardinality == FieldCardinality.Many
            ? DomainTypes.Array(field.Type)
            : field.Type;
}
