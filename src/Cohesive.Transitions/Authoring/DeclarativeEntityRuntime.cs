using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Executes declarative transition definitions against immutable entity snapshots.
/// </summary>
/// <remarks>
/// Compatibility interpreter retained for legacy characterization and the Transportation example through ARI-218.
/// New runtime integrations must compile and interpret canonical Transition IR.
/// </remarks>
public sealed class DeclarativeEntityRuntime
{
    static readonly ConditionalWeakTable<EntityDefinition, CompiledEntityPlan> PlanByEntityDefinition = [];

    internal static IReadOnlySet<string> SupportedExpressionFunctions { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ExprFunctionNames.EntityId,
            ExprFunctionNames.Count,
            ExprFunctionNames.Object,
            ExprFunctionNames.InsertAt,
            ExprFunctionNames.InsertRangeAt,
            ExprFunctionNames.Append,
            ExprFunctionNames.AppendRange,
            ExprFunctionNames.Concat
        };

    readonly EntityDefinition entityDefinition;
    readonly Dictionary<string, FieldDefinition> fieldByIdentity;
    readonly IReadOnlyList<FieldDefinition> fieldsByOrdinal;
    readonly IReadOnlyDictionary<string, int> ordinalByFieldName;
    readonly IReadOnlyList<FieldDefinition> requiredFields;
    readonly IReadOnlyList<FieldDefinition> computedFields;
    readonly Dictionary<string, TransitionPlan> transitionByName;

    /// <summary>
    /// Creates a runtime interpreter for the supplied entity definition.
    /// </summary>
    public DeclarativeEntityRuntime(EntityDefinition entityDefinition)
    {
        this.entityDefinition = Guard.RequireNotNull(entityDefinition);
        var compiledPlan = PlanByEntityDefinition.GetValue(this.entityDefinition, static x => CompiledEntityPlan.Build(x));
        fieldByIdentity = compiledPlan.FieldByIdentity;
        fieldsByOrdinal = compiledPlan.FieldsByOrdinal;
        ordinalByFieldName = compiledPlan.OrdinalByFieldName;
        requiredFields = compiledPlan.RequiredFields;
        computedFields = compiledPlan.ComputedFields;
        transitionByName = compiledPlan.TransitionByName;
    }

    /// <summary>
    /// Validates an immutable entity snapshot against declarative field constraints and invariants.
    /// </summary>
    /// <exception cref="SemanticRuleViolationException"></exception>
    /// <exception cref="InvariantViolationException"></exception>
    public void ValidateState(string entityId, EntityState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(state);

        var mutableState = CreateMutableState(entityId: entityId, state: state);
        var context = new EvaluationContext(
            EntityId: entityId,
            FieldByIdentity: fieldByIdentity,
            State: mutableState,
            TransitionInputValues: new Dictionary<string, ObservationValue>(StringComparer.Ordinal));
        EnsureComputedFieldsMatch(entityId: entityId, context: context);
        EnsureRequiredFieldPresence(entityId: entityId, stateByFieldName: mutableState);
        EnsureFieldConstraints(entityId: entityId, context: context);
        EnsureEntityInvariants(entityId: entityId, context: context);
    }

    internal EntityState NormalizeState(string entityId, EntityState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(state);

        if (computedFields.Count == 0)
            return state;

        var mutableState = CreateMutableState(entityId: entityId, state: state);
        var context = new EvaluationContext(
            EntityId: entityId,
            FieldByIdentity: fieldByIdentity,
            State: mutableState,
            TransitionInputValues: new Dictionary<string, ObservationValue>(StringComparer.Ordinal));
        ApplyComputedFields(entityId: entityId, context: context);
        return mutableState.ToEntityState(state.Version);
    }

    /// <summary>
    /// Applies a declarative transition to the supplied state snapshot.
    /// </summary>
    /// <exception cref="SemanticRuleViolationException">The given transition name was not found</exception>
    /// <exception cref="SemanticRuleViolationException">The given transition references an unknown field</exception>
    /// <exception cref="TransitionPreconditionException"></exception>
    /// <exception cref="InvariantViolationException"></exception>
    public TransitionResult Apply(
        string entityId,
        EntityState state,
        long version,
        string transitionName,
        ObservationValue input = default
        )
    {
        var observedInput = input.Kind switch
        {
            ObservationValueKind.Undefined or ObservationValueKind.Null => null,
            ObservationValueKind.Object when input.Fields is not null => input.Fields,
            ObservationValueKind.Object => new Dictionary<string, ObservationValue>(StringComparer.Ordinal),
            _ => throw new SemanticRuleViolationException($"Transition '{transitionName}' on entity '{entityId}' expects input to be an object value.")
        };

        return Apply(
            entityId: entityId,
            state: state,
            version: version,
            transitionName: transitionName,
            input: observedInput
            );
    }

    /// <summary>
    /// Applies a declarative transition to the supplied state snapshot.
    /// </summary>
    /// <exception cref="SemanticRuleViolationException">The given transition name was not found</exception>
    /// <exception cref="SemanticRuleViolationException">The given transition references an unknown field</exception>
    /// <exception cref="TransitionPreconditionException"></exception>
    /// <exception cref="InvariantViolationException"></exception>
    TransitionResult Apply(
        string entityId,
        EntityState state,
        long version,
        string transitionName,
        IReadOnlyDictionary<string, ObservationValue>? input
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionName);

        if (!transitionByName.TryGetValue(transitionName, out var transitionPlan))
            throw new SemanticRuleViolationException($"Entity '{entityId}' does not declare transition '{transitionName}' on entity type '{entityDefinition.Name.Value}'.");

        var inputValues = input is null
            ? new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            : new(input, StringComparer.Ordinal);

        EnsureTransitionInputs(entityId: entityId, transition: transitionPlan, inputValues: inputValues);
        var transition = transitionPlan.Transition;

        var oldState = state;
        var mutableState = CreateMutableState(entityId: entityId, state: state);
        var context = new EvaluationContext(
            EntityId: entityId,
            FieldByIdentity: fieldByIdentity,
            State: mutableState,
            TransitionInputValues: inputValues
            );
        
        foreach (var precondition in transition.Preconditions)
        {
            var result = Evaluate(precondition.Expression, context);
            if (!AsBoolean(result, context: $"precondition '{precondition.Name}' on transition '{transition.Name}'"))
                throw new TransitionPreconditionException(transitionName: transition.Name, entityId: entityId);
        }

        foreach (var update in transition.Updates)
        {
            if (!fieldByIdentity.TryGetValue(update.Field, out var field))
                throw new SemanticRuleViolationException($"Transition '{transition.Name}' on entity '{entityId}' references unknown field '{update.Field}'.");

            EnsureFieldCanBeUpdated(entityId: entityId, transition: transition, field: field, currentState: mutableState);

            var value = Evaluate(expression: update.ValueExpression, context: context);
            EnsureFieldValueMatchesType(entityId: entityId, field: field, value: value, context: $"transition '{transition.Name}' update");
            mutableState.Set(field.Name.Value, value);
        }

        ApplyComputedFields(entityId: entityId, context: context);

        EnsureRequiredFieldPresence(entityId: entityId, stateByFieldName: mutableState);
        EnsureFieldConstraints(entityId: entityId, context: context);
        EnsureEntityInvariants(entityId: entityId, context: context);

        var snapshotFieldNames = transitionPlan.SnapshotFieldNames;
        var snapshotState = mutableState.ToObservationValueDictionary(snapshotFieldNames);
        var snapshot = snapshotFieldNames.Count == 0
            ? null
            : new EffectSnapshot(
                SnapshotTokenProjector.Compute(snapshotState, snapshotFieldNames),
                snapshotFieldNames
                );

        List<EffectRequest> effects = [];
        foreach (var effect in transition.Effects)
        {
            var payloadValue = effect.Payload is null
                ? ObservationValue.Null
                : NormalizeEffectPayload(Evaluate(expression: effect.Payload, context: context));
            effects.Add(EffectRequest.Named(
                name: effect.Name,
                payload: payloadValue,
                continuation: effect.Continuation is null
                    ? null
                    : new EffectContinuation(effect.Continuation.TransitionName),
                snapshot: effect.Continuation is null ? null : snapshot));
        }

        var newState = mutableState.ToEntityState(version + 1);
        var readFieldNames = transitionPlan.ReadFieldNames;
        var writeFieldNames = transitionPlan.WriteFieldNames;
        var changedFieldNames = ResolveChangedFieldNames(oldState, newState);

        return new(
            TransitionName: transition.Name,
            OldState: oldState,
            NewState: newState,
            Effects: effects,
            NewVersion: version + 1,
            ReadFields: readFieldNames,
            WriteFields: writeFieldNames,
            ChangedFields: changedFieldNames
            );
    }

    MutableStateBuffer CreateMutableState(string entityId, EntityState state)
    {
        return new(
            entityId: entityId,
            entityDefinition: entityDefinition,
            state: state,
            fieldsByOrdinal: fieldsByOrdinal,
            ordinalByFieldName: ordinalByFieldName
            );
    }

    void EnsureTransitionInputs(
        string entityId,
        TransitionPlan transition,
        Dictionary<string, ObservationValue> inputValues
        )
    {
        foreach (var (name, value) in inputValues)
        {
            if (!transition.ParameterByName.TryGetValue(name, out var parameter))
                throw new SemanticRuleViolationException($"Transition '{transition.Transition.Name}' on entity '{entityId}' received unknown parameter '{name}'.");

            if (value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined && parameter.IsRequired)
                throw new SemanticRuleViolationException($"Transition '{transition.Transition.Name}' on entity '{entityId}' requires parameter '{name}'.");

            if (value.Kind is not ObservationValueKind.Null and not ObservationValueKind.Undefined)
            {
                EnsureValueMatchesType(
                    entityId: entityId,
                    type: parameter.Type,
                    value: value,
                    context: $"transition parameter '{name}'"
                    );
            }
        }

        foreach (var parameterName in transition.RequiredParameterNames)
        {
            if (!inputValues.TryGetValue(parameterName, out var value)
                || value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
            {
                throw new SemanticRuleViolationException(
                    $"Transition '{transition.Transition.Name}' on entity '{entityId}' is missing required parameter '{parameterName}'.");
            }
        }
    }

    static void EnsureFieldCanBeUpdated(
        string entityId,
        TransitionDefinition transition,
        FieldDefinition field,
        MutableStateBuffer currentState
        )
    {
        var mutability = field.Mutability;
        if (mutability is FieldMutability.Computed)
            throw new SemanticRuleViolationException($"Transition '{transition.Name}' on entity '{entityId}' cannot directly update computed field '{field.Name.Value}'.");

        if (mutability != FieldMutability.WriteOnce)
            return;

        if (!currentState.TryGetValue(field.Name.Value, out var existingValue))
            return;

        if (existingValue.Kind is not ObservationValueKind.Null and not ObservationValueKind.Undefined)
        {
            throw new SemanticRuleViolationException($"Transition '{transition.Name}' on entity '{entityId}' cannot overwrite write-once field '{field.Name.Value}'.");
        }
    }

    void EnsureRequiredFieldPresence(string entityId, MutableStateBuffer stateByFieldName)
    {
        foreach (var field in requiredFields)
        {
            if (!stateByFieldName.TryGetValue(field.Name.Value, out var value)
                || value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
            {
                throw new SemanticRuleViolationException($"Entity '{entityId}' is missing required field '{field.Name.Value}'.");
            }
        }
    }

    void EnsureFieldConstraints(string entityId, EvaluationContext context)
    {
        foreach (var field in entityDefinition.Fields)
        {
            foreach (var constraint in field.GetTransitionConstraints())
            {
                var result = Evaluate(expression: constraint.Expression, context: context);
                if (!AsBoolean(result, context: $"constraint '{constraint.Name}' on field '{field.Name.Value}'"))
                {
                    var message = string.IsNullOrWhiteSpace(constraint.Message)
                        ? $"Constraint '{constraint.Name}' on field '{field.Name.Value}' failed for entity '{entityId}'."
                        : constraint.Message;
                    throw new SemanticRuleViolationException(message);
                }
            }
        }
    }

    void ApplyComputedFields(string entityId, EvaluationContext context)
    {
        foreach (var field in computedFields)
        {
            var compute = field.Compute!;
            var computedValue = Evaluate(expression: compute.Expression, context: context);
            EnsureFieldValueMatchesType(entityId: entityId, field: field, value: computedValue, context: $"computed field '{field.Name.Value}'");
            context.State.Set(field.Name.Value, computedValue);
        }
    }

    void EnsureComputedFieldsMatch(string entityId, EvaluationContext context)
    {
        foreach (var field in computedFields)
        {
            var compute = field.Compute!;
            var expectedValue = Evaluate(expression: compute.Expression, context: context);
            EnsureFieldValueMatchesType(entityId: entityId, field: field, value: expectedValue, context: $"computed field '{field.Name.Value}'");

            if (!context.State.TryGetValue(field.Name.Value, out var actualValue)
                || !ObservationValue.DeepEquals(actualValue, expectedValue))
            {
                throw new SemanticRuleViolationException(
                    $"Computed field '{field.Name.Value}' on entity '{entityId}' does not match its compute expression.");
            }

            context.State.Set(field.Name.Value, expectedValue);
        }
    }
    
    void EnsureEntityInvariants(string entityId, EvaluationContext context)
    {
        foreach (var invariant in entityDefinition.Invariants)
        {
            var result = Evaluate(expression: invariant.Expression, context: context);
            if (!AsBoolean(result, context: $"invariant '{invariant.Name}'"))
            {
                throw new InvariantViolationException(invariantName: invariant.Name, entityId: entityId);
            }
        }
    }

    void EnsureFieldValueMatchesType(string entityId, FieldDefinition field, ObservationValue value, string context)
    {
        if (value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
        {
            if (field.Presence == FieldPresence.Required)
            {
                throw new SemanticRuleViolationException($"{context} produced null for required field '{field.Name.Value}' on entity '{entityId}'.");
            }

            return;
        }

        if (field.Cardinality == FieldCardinality.Single)
        {
            EnsureValueMatchesType(entityId: entityId, type: field.Type, value: value, context: $"{context} field '{field.Name.Value}'");
            return;
        }

        if (value.Kind != ObservationValueKind.Array)
        {
            throw new SemanticRuleViolationException(
                $"{context} produced a non-array value for collection field '{field.Name.Value}' on entity '{entityId}'.");
        }

        foreach (var item in value.EnumerateArray())
        {
            EnsureValueMatchesType(entityId: entityId, type: field.Type, value: item, context: $"{context} field '{field.Name.Value}' element");
        }
    }

    static void EnsureValueMatchesType(string entityId, TypeRef type, ObservationValue value, string context)
    {
        if (!JsonTypeSemantics.MatchesType(type: type, value: value))
        {
            throw new SemanticRuleViolationException($"{context} on entity '{entityId}' does not satisfy expected type '{JsonTypeSemantics.DescribeType(type)}'.");
        }
    }

    ObservationValue Evaluate(Expr expression, EvaluationContext context)
    {
        switch (expression)
        {
            case FieldExpr field:
                if (!field.Path.TryGetTerminalFieldIdentity(out var fieldIdentity))
                    throw new SemanticRuleViolationException($"Expression on entity '{context.EntityId}' references a non-terminal field path '{field.Path}'.");

                if (!context.FieldByIdentity.TryGetValue(fieldIdentity, out var fieldDef))
                    throw new SemanticRuleViolationException($"Expression on entity '{context.EntityId}' references unknown field '{fieldIdentity}'.");

                if (!context.State.TryGetValue(fieldDef.Name.Value, out var fieldValue))
                    throw new SemanticRuleViolationException($"Expression on entity '{context.EntityId}' references unknown field '{fieldDef.Name.Value}'.");
                return fieldValue;

            case ParameterExpr parameter:
                if (!context.TransitionInputValues.TryGetValue(parameter.Parameter, out var parameterValue))
                    throw new SemanticRuleViolationException($"Expression on entity '{context.EntityId}' references unknown transition parameter '{parameter.Parameter}'.");

                return parameterValue;

            case ConstantExpr constant:
                return constant.Value;

            case UnaryExpr unary:
                return unary.Operator switch
                {
                    UnaryOperator.Not => ObservationValue.FromBool(!AsBoolean(
                        Evaluate(expression: unary.Operand, context: context),
                        context: "logical not"
                        )),
                    
                    _ => throw new SemanticRuleViolationException($"Unsupported unary operator '{unary.Operator}' on entity '{context.EntityId}'.")
                };

            case BinaryExpr binary:
            {
                if (binary.Operator == BinaryOperator.And)
                {
                    var left = Evaluate(expression: binary.Left, context: context);
                    if (!AsBoolean(value: left, context: "logical and left"))
                        return ObservationValue.FromBool(false);

                    var right = Evaluate(expression: binary.Right, context: context);
                    return ObservationValue.FromBool(AsBoolean(value: right, context: "logical and right"));
                }

                if (binary.Operator is BinaryOperator.Or)
                {
                    var left = Evaluate(expression: binary.Left, context: context);
                    if (AsBoolean(value: left, context: "logical or left"))
                        return ObservationValue.FromBool(true);

                    var right = Evaluate(expression: binary.Right, context: context);
                    return ObservationValue.FromBool(AsBoolean(value: right, context: "logical or right"));
                }

                var leftValue = Evaluate(expression: binary.Left, context: context);
                var rightValue = Evaluate(expression: binary.Right, context: context);
                return EvaluateBinary(op: binary.Operator, left: leftValue, right: rightValue, entityId: context.EntityId);
            }

            case ConditionalExpr conditional:
            {
                var testValue = Evaluate(expression: conditional.Test, context: context);
                return AsBoolean(value: testValue, context: "conditional test")
                    ? Evaluate(expression: conditional.IfTrue, context: context)
                    : Evaluate(expression: conditional.IfFalse, context: context);
            }

            case CallExpr function:
                return EvaluateFunction(function: function, context: context);
        }

        throw new SemanticRuleViolationException($"Unsupported expression node '{expression.GetType().Name}' on entity '{context.EntityId}'.");
    }

    ObservationValue EvaluateFunction(CallExpr function, EvaluationContext context)
    {
        var arguments = function.Arguments
            .Select(x => Evaluate(expression: x, context: context))
            .ToList();

        if (TryEvaluateBuiltInFunction(function: function.Function, arguments: arguments, entityId: context.EntityId, result: out var builtIn))
            return builtIn;

        throw new SemanticRuleViolationException($"Unsupported function '{function.Function}' on entity '{context.EntityId}'.");
    }

    static bool TryEvaluateBuiltInFunction(
        string function,
        IReadOnlyList<ObservationValue> arguments,
        string entityId,
        out ObservationValue result
        )
    {
        switch (function)
        {
            case ExprFunctionNames.EntityId:
                if (arguments.Count != 0)
                    throw new SemanticRuleViolationException($"Function '{ExprFunctionNames.EntityId}' expects zero arguments on entity '{entityId}'.");
                result = ObservationValue.FromString(entityId);
                return true;

            case ExprFunctionNames.Count:
                if (arguments.Count != 1)
                    throw new SemanticRuleViolationException($"Function '{ExprFunctionNames.Count}' expects exactly one argument on entity '{entityId}'.");
                result = ObservationValue.FromInt64(Count(value: arguments[0]));
                return true;

            case ExprFunctionNames.Object:
                if (arguments.Count % 2 != 0)
                    throw new SemanticRuleViolationException($"Function '{ExprFunctionNames.Object}' expects an even number of arguments on entity '{entityId}'.");

                Dictionary<string, ObservationValue> obj = new(StringComparer.Ordinal);
                for (var i = 0; i < arguments.Count; i += 2)
                {
                    var key = AsString(arguments[i], context: "object key");
                    obj[key] = arguments[i + 1];
                }

                result = ObservationValue.FromObject(obj);
                return true;

            case ExprFunctionNames.InsertAt:
                if (arguments.Count != 3)
                    throw new SemanticRuleViolationException($"Function '{ExprFunctionNames.InsertAt}' expects exactly three arguments on entity '{entityId}'.");
                result = InsertAt(arguments[0], arguments[1], arguments[2], entityId);
                return true;

            case ExprFunctionNames.InsertRangeAt:
                if (arguments.Count != 3)
                    throw new SemanticRuleViolationException($"Function '{ExprFunctionNames.InsertRangeAt}' expects exactly three arguments on entity '{entityId}'.");
                result = InsertRangeAt(arguments[0], arguments[1], arguments[2], entityId);
                return true;

            case ExprFunctionNames.Append:
                if (arguments.Count != 2)
                    throw new SemanticRuleViolationException($"Function '{ExprFunctionNames.Append}' expects exactly two arguments on entity '{entityId}'.");
                result = Append(arguments[0], arguments[1], entityId);
                return true;

            case ExprFunctionNames.AppendRange:
                if (arguments.Count != 2)
                    throw new SemanticRuleViolationException($"Function '{ExprFunctionNames.AppendRange}' expects exactly two arguments on entity '{entityId}'.");
                result = AppendRange(arguments[0], arguments[1], entityId);
                return true;

            case ExprFunctionNames.Concat:
                if (arguments.Count == 0)
                    throw new SemanticRuleViolationException($"Function '{ExprFunctionNames.Concat}' expects at least one argument on entity '{entityId}'.");
                result = ObservationValue.FromString(string.Concat(arguments.Select(x => AsString(x, "concat argument"))));
                return true;

            default:
                result = ObservationValue.Null;
                return false;
        }
    }

    static ObservationValue EvaluateBinary(BinaryOperator op, ObservationValue left, ObservationValue right, string entityId)
    {
        return op switch
        {
            BinaryOperator.Eq => ObservationValue.FromBool(ObservationValue.DeepEquals(left, right)),
            BinaryOperator.Ne => ObservationValue.FromBool(!ObservationValue.DeepEquals(left, right)),
            BinaryOperator.Gt => ObservationValue.FromBool(CompareOperands(left: left, right: right, entityId: entityId) > 0),
            BinaryOperator.Ge => ObservationValue.FromBool(CompareOperands(left: left, right: right, entityId: entityId) >= 0),
            BinaryOperator.Lt => ObservationValue.FromBool(CompareOperands(left: left, right: right, entityId: entityId) < 0),
            BinaryOperator.Le => ObservationValue.FromBool(CompareOperands(left: left, right: right, entityId: entityId) <= 0),
            BinaryOperator.Add => ObservationValue.FromDecimal(AsDecimal(left, "addition left") + AsDecimal(right, "addition right")),
            BinaryOperator.Sub => ObservationValue.FromDecimal(AsDecimal(left, "subtraction left") - AsDecimal(right, "subtraction right")),
            BinaryOperator.Mul => ObservationValue.FromDecimal(AsDecimal(left, "multiplication left") * AsDecimal(right, "multiplication right")),
            BinaryOperator.Div => ObservationValue.FromDecimal(AsDecimal(left, "division left") / AsDecimal(right, "division right")),
            _ => throw new SemanticRuleViolationException($"Unsupported binary operator '{op}' on entity '{entityId}'.")
        };
    }

    static int CompareOperands(ObservationValue left, ObservationValue right, string entityId)
    {
        if (left.TryGetDecimal(out var leftDecimal) && right.TryGetDecimal(out var rightDecimal))
            return leftDecimal.CompareTo(rightDecimal);

        if (left.Kind == ObservationValueKind.String && right.Kind == ObservationValueKind.String)
        {
            var leftString = left.GetString();
            var rightString = right.GetString();
            if (DateTimeOffset.TryParse(leftString, out var leftDate)
                && DateTimeOffset.TryParse(rightString, out var rightDate))
            {
                return leftDate.CompareTo(rightDate);
            }

            return string.Compare(leftString, rightString, StringComparison.Ordinal);
        }

        throw new SemanticRuleViolationException($"Unable to compare expression operands on entity '{entityId}'.");
    }

    static int Count(ObservationValue value)
    {
        return value switch
        {
            { Kind: ObservationValueKind.Null or ObservationValueKind.Undefined } => 0,
            { Kind: ObservationValueKind.Array } => value.GetArrayLength(),
            { Kind: ObservationValueKind.Object } when value.Fields is not null => value.Fields.Count,
            _ => throw new SemanticRuleViolationException($"Function '{ExprFunctionNames.Count}' expects an array or object value.")
        };
    }

    static bool AsBoolean(ObservationValue value, string context)
    {
        if (!value.TryGetBoolean(out var result))
            throw new SemanticRuleViolationException($"Expression value for '{context}' is not a boolean.");

        return result;
    }

    static string AsString(ObservationValue value, string context)
    {
        if (value.Kind == ObservationValueKind.String)
            return value.GetString() ?? string.Empty;
        throw new SemanticRuleViolationException($"Expression value for '{context}' is not a string.");
    }

    static decimal AsDecimal(ObservationValue value, string context)
    {
        if (!value.TryGetDecimal(out var result))
            throw new SemanticRuleViolationException($"Expression value for '{context}' is not numeric.");

        return result;
    }

    static ObservationValue InsertAt(ObservationValue source, ObservationValue indexValue, ObservationValue item, string entityId)
    {
        if (source.Kind != ObservationValueKind.Array || source.Array.IsDefault)
            throw new SemanticRuleViolationException($"Function 'insertAt' expects an array as first argument on entity '{entityId}'.");

        var index = AsInt32(indexValue, "insertAt index");
        if (index < 0 || index > source.Array.Length)
        {
            throw new SemanticRuleViolationException(
                $"Function 'insertAt' received out-of-range index '{index}' on entity '{entityId}'.");
        }

        var result = new ObservationValue[source.Array.Length + 1];
        source.Array.AsSpan()[..index].CopyTo(result);
        result[index] = item;
        source.Array.AsSpan()[index..].CopyTo(result.AsSpan(index + 1));
        return FromOwnedArray(result);
    }

    static ObservationValue Append(ObservationValue source, ObservationValue item, string entityId)
    {
        if (source.Kind != ObservationValueKind.Array || source.Array.IsDefault)
            throw new SemanticRuleViolationException($"Function 'append' expects an array as first argument on entity '{entityId}'.");

        var result = new ObservationValue[source.Array.Length + 1];
        source.Array.AsSpan().CopyTo(result);
        result[^1] = item;
        return FromOwnedArray(result);
    }

    static ObservationValue InsertRangeAt(ObservationValue source, ObservationValue indexValue, ObservationValue items, string entityId)
    {
        if (source.Kind != ObservationValueKind.Array || source.Array.IsDefault)
            throw new SemanticRuleViolationException($"Function 'insertRangeAt' expects an array as first argument on entity '{entityId}'.");

        if (items.Kind != ObservationValueKind.Array || items.Array.IsDefault)
            throw new SemanticRuleViolationException($"Function 'insertRangeAt' expects an array as third argument on entity '{entityId}'.");

        var index = AsInt32(indexValue, "insertRangeAt index");
        if (index < 0 || index > source.Array.Length)
            throw new SemanticRuleViolationException($"Function 'insertRangeAt' received out-of-range index '{index}' on entity '{entityId}'.");

        var result = new ObservationValue[source.Array.Length + items.Array.Length];
        source.Array.AsSpan()[..index].CopyTo(result);
        items.Array.AsSpan().CopyTo(result.AsSpan(index));
        source.Array.AsSpan()[index..].CopyTo(result.AsSpan(index + items.Array.Length));
        return FromOwnedArray(result);
    }

    static ObservationValue AppendRange(ObservationValue source, ObservationValue items, string entityId)
    {
        if (source.Kind != ObservationValueKind.Array || source.Array.IsDefault)
            throw new SemanticRuleViolationException($"Function 'appendRange' expects an array as first argument on entity '{entityId}'.");

        if (items.Kind != ObservationValueKind.Array || items.Array.IsDefault)
            throw new SemanticRuleViolationException($"Function 'appendRange' expects an array as second argument on entity '{entityId}'.");

        var result = new ObservationValue[source.Array.Length + items.Array.Length];
        source.Array.AsSpan().CopyTo(result);
        items.Array.AsSpan().CopyTo(result.AsSpan(source.Array.Length));
        return FromOwnedArray(result);
    }

    static ObservationValue FromOwnedArray(ObservationValue[] values) =>
        ObservationValue.FromImmutableArray(ImmutableCollectionsMarshal.AsImmutableArray(values));

    static int AsInt32(ObservationValue value, string context)
    {
        if (!value.TryGetInt32(out var result))
            throw new SemanticRuleViolationException($"Expression value for '{context}' is not an int32.");

        return result;
    }

    static IReadOnlyList<string> ResolveFieldNames(
        TransitionDefinition transition,
        IReadOnlyList<string> fieldNames,
        IReadOnlyDictionary<string, FieldDefinition> fieldByIdentity,
        string context
        )
    {
        if (fieldNames.Count == 0)
            return [];

        List<string> names = [];
        foreach (var fieldName in fieldNames)
        {
            if (!fieldByIdentity.TryGetValue(fieldName, out var field))
                throw new SemanticRuleViolationException($"Transition '{transition.Name}' references unknown field '{fieldName}' while resolving {context}.");

            names.Add(field.Name.Value);
        }

        return [..names.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)];
    }

    static IReadOnlyList<string> ResolveSnapshotFieldNames(
        TransitionDefinition transition,
        IReadOnlyDictionary<string, FieldDefinition> fieldByIdentity
        )
    {
        var fieldNames = transition.WriteSet.Length > 0
            ? transition.WriteSet
            : transition.ReadSet;

        return ResolveFieldNames(
            transition: transition,
            fieldNames: fieldNames,
            fieldByIdentity: fieldByIdentity,
            context: "snapshot projection"
            );
    }

    static IReadOnlyList<string> ResolveChangedFieldNames(EntityState oldState, EntityState newState)
    {
        HashSet<string> names = new(oldState.Fields.Keys, StringComparer.Ordinal);
        names.UnionWith(newState.Fields.Keys);

        List<string> changed = [];
        foreach (var name in names)
        {
            var hasOld = oldState.Fields.TryGetValue(name, out var oldValue);
            var hasNew = newState.Fields.TryGetValue(name, out var newValue);
            if (!hasOld || !hasNew || !ObservationValue.DeepEquals(oldValue, newValue))
                changed.Add(name);
        }

        return [.. changed.OrderBy(x => x, StringComparer.Ordinal)];
    }

    static ObservationValue NormalizeEffectPayload(ObservationValue value)
    {
        if (value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
            return ObservationValue.Null;

        if (value.Kind == ObservationValueKind.Object)
            return value;

        return ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["value"] = value
        });
    }

    sealed class CompiledEntityPlan
    {
        CompiledEntityPlan(
            Dictionary<string, FieldDefinition> fieldByIdentity,
            Dictionary<string, FieldDefinition> fieldByName,
            FieldDefinition[] fieldsByOrdinal,
            Dictionary<string, int> ordinalByFieldName,
            FieldDefinition[] requiredFields,
            FieldDefinition[] computedFields,
            Dictionary<string, TransitionPlan> transitionByName
            )
        {
            FieldByIdentity = fieldByIdentity;
            FieldByName = fieldByName;
            FieldsByOrdinal = fieldsByOrdinal;
            OrdinalByFieldName = ordinalByFieldName;
            RequiredFields = requiredFields;
            ComputedFields = computedFields;
            TransitionByName = transitionByName;
        }

        public Dictionary<string, FieldDefinition> FieldByIdentity { get; }

        public Dictionary<string, FieldDefinition> FieldByName { get; }

        public FieldDefinition[] FieldsByOrdinal { get; }

        public Dictionary<string, int> OrdinalByFieldName { get; }

        public FieldDefinition[] RequiredFields { get; }

        public FieldDefinition[] ComputedFields { get; }

        public Dictionary<string, TransitionPlan> TransitionByName { get; }

        public static CompiledEntityPlan Build(EntityDefinition entityDefinition)
        {
            Dictionary<string, FieldDefinition> fieldByIdentity = new(StringComparer.Ordinal);
            foreach (var field in entityDefinition.Fields)
                fieldByIdentity[field.Name.Value] = field;

            var fieldByName = entityDefinition.Fields.ToDictionary(keySelector: x => x.Name.Value, comparer: StringComparer.Ordinal);
            var fieldsByOrdinal = entityDefinition.Fields.ToArray();
            Dictionary<string, int> ordinalByFieldName = new(StringComparer.Ordinal);
            for (var i = 0; i < fieldsByOrdinal.Length; i++)
                ordinalByFieldName[fieldsByOrdinal[i].Name.Value] = i;
            var requiredFields = fieldsByOrdinal.Where(x => x.Presence == FieldPresence.Required).ToArray();
            var computedFields = fieldsByOrdinal
                .Where(x => x.Mutability == FieldMutability.Computed && x.Compute is not null)
                .ToArray();
            Dictionary<string, TransitionPlan> transitionByName = new(StringComparer.Ordinal);
            foreach (var transition in entityDefinition.Transitions)
            {
                var parameterByName = transition.Inputs.ToDictionary(keySelector: x => x.Name, comparer: StringComparer.Ordinal);
                var requiredParameterNames = transition.Inputs
                    .Where(x => x.IsRequired)
                    .Select(x => x.Name)
                    .ToArray();
                var readFieldNames = ResolveFieldNames(
                    transition: transition,
                    fieldNames: transition.ReadSet,
                    fieldByIdentity: fieldByIdentity,
                    context: "read set");
                var writeFieldNames = ResolveFieldNames(
                    transition: transition,
                    fieldNames: transition.WriteSet,
                    fieldByIdentity: fieldByIdentity,
                    context: "write set");
                var snapshotFieldNames = ResolveSnapshotFieldNames(
                    transition: transition,
                    fieldByIdentity: fieldByIdentity);

                transitionByName[transition.Name] = new(
                    transition: transition,
                    parameterByName: parameterByName,
                    requiredParameterNames: requiredParameterNames,
                    readFieldNames: readFieldNames,
                    writeFieldNames: writeFieldNames,
                    snapshotFieldNames: snapshotFieldNames
                    );
            }

            return new(
                fieldByIdentity: fieldByIdentity,
                fieldByName: fieldByName,
                fieldsByOrdinal: fieldsByOrdinal,
                ordinalByFieldName: ordinalByFieldName,
                requiredFields: requiredFields,
                computedFields: computedFields,
                transitionByName: transitionByName
                );
        }
    }

    sealed class TransitionPlan(
        TransitionDefinition transition,
        Dictionary<string, TransitionParameterDefinition> parameterByName,
        IReadOnlyList<string> requiredParameterNames,
        IReadOnlyList<string> readFieldNames,
        IReadOnlyList<string> writeFieldNames,
        IReadOnlyList<string> snapshotFieldNames
        )
    {
        public TransitionDefinition Transition { get; } = transition;

        public Dictionary<string, TransitionParameterDefinition> ParameterByName { get; } = parameterByName;

        public IReadOnlyList<string> RequiredParameterNames { get; } = requiredParameterNames;

        public IReadOnlyList<string> ReadFieldNames { get; } = readFieldNames;

        public IReadOnlyList<string> WriteFieldNames { get; } = writeFieldNames;

        public IReadOnlyList<string> SnapshotFieldNames { get; } = snapshotFieldNames;
    }

    sealed class MutableStateBuffer
    {
        static readonly ObservationValue NullValue = ObservationValue.Null;

        readonly string entityId;
        readonly EntityDefinition entityDefinition;
        readonly EntityStateLineage lineage;
        readonly IReadOnlyList<FieldDefinition> fieldsByOrdinal;
        readonly IReadOnlyDictionary<string, int> ordinalByFieldName;
        readonly ObservationValue[] originalByOrdinal;
        readonly bool[] hasOriginalByOrdinal;
        readonly ObservationValue[] dirtyByOrdinal;
        readonly bool[] hasDirtyByOrdinal;

        public MutableStateBuffer(
            string entityId,
            EntityDefinition entityDefinition,
            EntityState state,
            IReadOnlyList<FieldDefinition> fieldsByOrdinal,
            IReadOnlyDictionary<string, int> ordinalByFieldName
            )
        {
            ArgumentNullException.ThrowIfNull(state);
            this.entityId = Guard.RequireNotNullOrWhiteSpace(entityId);
            this.entityDefinition = Guard.RequireNotNull(entityDefinition);
            lineage = state.Lineage;
            this.fieldsByOrdinal = Guard.RequireNotNull(fieldsByOrdinal);
            this.ordinalByFieldName = Guard.RequireNotNull(ordinalByFieldName);

            var count = this.fieldsByOrdinal.Count;
            originalByOrdinal = new ObservationValue[count];
            hasOriginalByOrdinal = new bool[count];
            dirtyByOrdinal = new ObservationValue[count];
            hasDirtyByOrdinal = new bool[count];

            foreach (var (name, value) in state.Fields)
            {
                if (!this.ordinalByFieldName.TryGetValue(name, out var ordinal))
                {
                    throw new SemanticRuleViolationException(
                        $"State for entity '{entityId}' contains unknown field '{name}' for entity type '{this.entityDefinition.Name.Value}'.");
                }

                originalByOrdinal[ordinal] = value;
                hasOriginalByOrdinal[ordinal] = true;
            }
        }

        public bool TryGetValue(string fieldName, out ObservationValue value)
        {
            if (!ordinalByFieldName.TryGetValue(fieldName, out var ordinal))
            {
                value = default;
                return false;
            }

            if (hasDirtyByOrdinal[ordinal])
            {
                value = dirtyByOrdinal[ordinal];
                return true;
            }

            value = hasOriginalByOrdinal[ordinal]
                ? originalByOrdinal[ordinal]
                : NullValue;
            return true;
        }

        public void Set(string fieldName, ObservationValue value)
        {
            if (!ordinalByFieldName.TryGetValue(fieldName, out var ordinal))
                throw new KeyNotFoundException($"State does not contain field '{fieldName}'.");

            dirtyByOrdinal[ordinal] = value;
            hasDirtyByOrdinal[ordinal] = true;
        }

        public EntityState ToEntityState(long version)
        {
            Dictionary<string, ObservationValue> values = new(StringComparer.Ordinal);
            for (var i = 0; i < fieldsByOrdinal.Count; i++)
                values[fieldsByOrdinal[i].Name.Value] = GetObservationValue(i);
            return entityDefinition.CreateState(entityId, values, version, lineage);
        }

        public IReadOnlyDictionary<string, ObservationValue> ToObservationValueDictionary(IReadOnlyList<string> fieldNames)
        {
            Dictionary<string, ObservationValue> values = new(StringComparer.Ordinal);
            foreach (var fieldName in fieldNames)
            {
                if (!ordinalByFieldName.TryGetValue(fieldName, out var ordinal))
                    continue;

                values[fieldName] = GetObservationValue(ordinal);
            }

            return values;
        }

        ObservationValue GetObservationValue(int ordinal)
        {
            if (hasDirtyByOrdinal[ordinal])
                return dirtyByOrdinal[ordinal];

            if (hasOriginalByOrdinal[ordinal])
                return originalByOrdinal[ordinal];

            return NullValue;
        }
    }

    sealed record EvaluationContext(
        string EntityId,
        IReadOnlyDictionary<string, FieldDefinition> FieldByIdentity,
        MutableStateBuffer State,
        IReadOnlyDictionary<string, ObservationValue> TransitionInputValues
        );

}
