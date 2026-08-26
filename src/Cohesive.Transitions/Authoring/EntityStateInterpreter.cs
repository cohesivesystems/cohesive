using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Transitions.Compilation;

namespace Cohesive.Transitions.Model;

/// <summary>
/// Internal entity-state interpreter for computed fields, declarative constraints, and invariants.
/// </summary>
internal sealed class EntityStateInterpreter
{
    static readonly ConditionalWeakTable<EntityDefinition, CompiledEntityPlan> PlanByEntityDefinition = [];

    readonly EntityDefinition entityDefinition;
    readonly Dictionary<string, FieldDefinition> fieldByIdentity;
    readonly IReadOnlyList<FieldDefinition> fieldsByOrdinal;
    readonly IReadOnlyDictionary<string, int> ordinalByFieldName;
    readonly IReadOnlyList<FieldDefinition> requiredFields;
    readonly IReadOnlyList<FieldDefinition> computedFields;
    readonly PortableExpressionReferenceEvaluator expressionEvaluator = new(
        TransitionExpressionLanguage.Capabilities,
        interpreterName: "entity-state interpreter");

    /// <summary>
    /// Creates a runtime interpreter for the supplied entity definition.
    /// </summary>
    public EntityStateInterpreter(EntityDefinition entityDefinition)
    {
        this.entityDefinition = Guard.RequireNotNull(entityDefinition);
        var compiledPlan = PlanByEntityDefinition.GetValue(this.entityDefinition, static x => CompiledEntityPlan.Build(x));
        fieldByIdentity = compiledPlan.FieldByIdentity;
        fieldsByOrdinal = compiledPlan.FieldsByOrdinal;
        ordinalByFieldName = compiledPlan.OrdinalByFieldName;
        requiredFields = compiledPlan.RequiredFields;
        computedFields = compiledPlan.ComputedFields;
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
            State: mutableState);
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
            State: mutableState);
        ApplyComputedFields(entityId: entityId, context: context);
        return mutableState.ToEntityState(state.Version);
    }

    internal IReadOnlyDictionary<string, ObservationValue> NormalizeStateValues(
        string entityId,
        IReadOnlyDictionary<string, ObservationValue> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(values);

        if (computedFields.Count == 0)
            return values;

        var mutableState = CreateMutableState(entityId, values);
        var context = new EvaluationContext(
            EntityId: entityId,
            FieldByIdentity: fieldByIdentity,
            State: mutableState);
        ApplyComputedFields(entityId: entityId, context: context);
        return mutableState.ToValues();
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

    MutableStateBuffer CreateMutableState(
        string entityId,
        IReadOnlyDictionary<string, ObservationValue> values)
    {
        return new(
            entityId: entityId,
            entityDefinition: entityDefinition,
            values: values,
            fieldsByOrdinal: fieldsByOrdinal,
            ordinalByFieldName: ordinalByFieldName);
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
            foreach (var constraint in field.GetEntityConstraints())
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

    void EnsureValueMatchesType(string entityId, TypeRef type, ObservationValue value, string context)
    {
        if (!JsonTypeSemantics.MatchesType(
                type: type,
                value: value,
                graph: entityDefinition.ShapeGraph?.Document.Graph))
        {
            throw new SemanticRuleViolationException($"{context} on entity '{entityId}' does not satisfy expected type '{JsonTypeSemantics.DescribeType(type)}'.");
        }
    }

    ObservationValue Evaluate(Expr expression, EvaluationContext context)
    {
        try
        {
            return expressionEvaluator.Evaluate(
                    expression,
                    new()
                    {
                        ResolveBinding = binding => throw new SemanticRuleViolationException(
                            $"Entity-state expression cannot resolve binding '{binding.Value}'."),
                        ResolveField = (binding, path) => ResolveField(context, binding, path),
                        ResolveParameter = parameter => throw new SemanticRuleViolationException(
                            $"Entity-state expression cannot resolve parameter '{parameter}'.")
                    })
                .RequireObservation("entity-state expression");
        }
        catch (PortableExpressionEvaluationException exception)
        {
            throw new SemanticRuleViolationException(
                $"Entity-state expression on entity '{context.EntityId}' failed: {exception.Message}");
        }
    }

    static PortableExpressionValue ResolveField(
        EvaluationContext context,
        ValueBindingId? binding,
        FieldPath path)
    {
        if (binding is not null)
        {
            throw new SemanticRuleViolationException(
                $"Entity-state expression cannot resolve binding-qualified field '{path}'.");
        }
        if (path.Segments.IsDefaultOrEmpty
            || path.Segments[0] is not { Kind: SegmentKind.Field, Segment: { } fieldIdentity }
            || string.IsNullOrWhiteSpace(fieldIdentity))
        {
            throw new SemanticRuleViolationException(
                $"Entity-state expression on entity '{context.EntityId}' has invalid field path '{path}'.");
        }
        if (!context.FieldByIdentity.TryGetValue(fieldIdentity, out var field)
            || !context.State.TryGetValue(field.Name.Value, out var value))
        {
            throw new SemanticRuleViolationException(
                $"Entity-state expression on entity '{context.EntityId}' references unknown field '{fieldIdentity}'.");
        }

        return PortableExpressionValue.FromObservation(value).Project(path, startIndex: 1);
    }

    static bool AsBoolean(ObservationValue value, string context)
    {
        try
        {
            return PortableExpressionReferenceEvaluator.RequireBoolean(
                PortableExpressionValue.FromObservation(value),
                context);
        }
        catch (PortableExpressionEvaluationException exception)
        {
            throw new SemanticRuleViolationException(exception.Message);
        }
    }

    sealed class CompiledEntityPlan
    {
        CompiledEntityPlan(
            Dictionary<string, FieldDefinition> fieldByIdentity,
            FieldDefinition[] fieldsByOrdinal,
            Dictionary<string, int> ordinalByFieldName,
            FieldDefinition[] requiredFields,
            FieldDefinition[] computedFields)
        {
            FieldByIdentity = fieldByIdentity;
            FieldsByOrdinal = fieldsByOrdinal;
            OrdinalByFieldName = ordinalByFieldName;
            RequiredFields = requiredFields;
            ComputedFields = computedFields;
        }

        public Dictionary<string, FieldDefinition> FieldByIdentity { get; }

        public FieldDefinition[] FieldsByOrdinal { get; }

        public Dictionary<string, int> OrdinalByFieldName { get; }

        public FieldDefinition[] RequiredFields { get; }

        public FieldDefinition[] ComputedFields { get; }

        public static CompiledEntityPlan Build(EntityDefinition entityDefinition)
        {
            Dictionary<string, FieldDefinition> fieldByIdentity = new(StringComparer.Ordinal);
            foreach (var field in entityDefinition.Fields)
                fieldByIdentity[field.Name.Value] = field;

            var fieldsByOrdinal = entityDefinition.Fields.ToArray();
            Dictionary<string, int> ordinalByFieldName = new(StringComparer.Ordinal);
            for (var index = 0; index < fieldsByOrdinal.Length; index++)
                ordinalByFieldName[fieldsByOrdinal[index].Name.Value] = index;

            return new(
                fieldByIdentity,
                fieldsByOrdinal,
                ordinalByFieldName,
                [.. fieldsByOrdinal.Where(static field => field.Presence == FieldPresence.Required)],
                [.. fieldsByOrdinal.Where(static field =>
                    field.Mutability == FieldMutability.Computed && field.Compute is not null)]);
        }
    }

    sealed class MutableStateBuffer
    {
        static readonly ObservationValue NullValue = ObservationValue.Null;

        readonly string entityId;
        readonly EntityDefinition entityDefinition;
        readonly EntityStateLineage? lineage;
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
            : this(
                entityId,
                entityDefinition,
                state.Fields,
                fieldsByOrdinal,
                ordinalByFieldName,
                state.Lineage)
        {
        }

        public MutableStateBuffer(
            string entityId,
            EntityDefinition entityDefinition,
            IReadOnlyDictionary<string, ObservationValue> values,
            IReadOnlyList<FieldDefinition> fieldsByOrdinal,
            IReadOnlyDictionary<string, int> ordinalByFieldName)
            : this(
                entityId,
                entityDefinition,
                values,
                fieldsByOrdinal,
                ordinalByFieldName,
                lineage: null)
        {
        }

        MutableStateBuffer(
            string entityId,
            EntityDefinition entityDefinition,
            IReadOnlyDictionary<string, ObservationValue> values,
            IReadOnlyList<FieldDefinition> fieldsByOrdinal,
            IReadOnlyDictionary<string, int> ordinalByFieldName,
            EntityStateLineage? lineage)
        {
            ArgumentNullException.ThrowIfNull(values);
            this.entityId = Guard.RequireNotNullOrWhiteSpace(entityId);
            this.entityDefinition = Guard.RequireNotNull(entityDefinition);
            this.lineage = lineage;
            this.fieldsByOrdinal = Guard.RequireNotNull(fieldsByOrdinal);
            this.ordinalByFieldName = Guard.RequireNotNull(ordinalByFieldName);

            var count = this.fieldsByOrdinal.Count;
            originalByOrdinal = new ObservationValue[count];
            hasOriginalByOrdinal = new bool[count];
            dirtyByOrdinal = new ObservationValue[count];
            hasDirtyByOrdinal = new bool[count];

            foreach (var (name, value) in values)
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
            var values = ToValues();
            return lineage is null
                ? entityDefinition.CreateState(entityId, values, version)
                : entityDefinition.CreateState(entityId, values, version, lineage);
        }

        public IReadOnlyDictionary<string, ObservationValue> ToValues()
        {
            Dictionary<string, ObservationValue> values = new(StringComparer.Ordinal);
            for (var i = 0; i < fieldsByOrdinal.Count; i++)
                values[fieldsByOrdinal[i].Name.Value] = GetObservationValue(i);
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
        MutableStateBuffer State
        );

}
