using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Transitions.Model;

/// <summary>
/// Semantic entity definition backed by a canonical entity shape.
/// </summary>
public sealed record EntityDefinition
{
    static readonly ConditionalWeakTable<EntityDefinition, StateValidationPlan> StateValidationPlanByEntityDefinition = [];

    /// <summary>
    /// Creates a semantic entity definition.
    /// </summary>
    /// <param name="name">Stable logical entity type name.</param>
    /// <param name="fields">Fields used when <paramref name="shape"/> is not supplied.</param>
    /// <param name="invariants">Entity-level invariants.</param>
    /// <param name="shape">Optional explicit entity shape whose identifier and metadata are preserved.</param>
    /// <param name="shapeGraph">
    /// Optional exact graph-qualified snapshot used to resolve named state types. Omit for genuinely inline shapes.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is default, the resolved shape is not an entity shape, has no fields,
    /// carries an entity-type annotation that contradicts <paramref name="name"/>.
    /// </exception>
    [JsonConstructor]
    public EntityDefinition(
        EntityTypeName name,
        ImmutableArray<FieldDefinition> fields,
        ImmutableArray<InvariantDefinition> invariants = default,
        Shape? shape = null,
        EntityShapeGraphBinding? shapeGraph = null
        )
    {
        if (string.IsNullOrWhiteSpace(name.Value))
            throw new ArgumentException("An entity type name is required.", nameof(name));

        Name = name;
        var resolvedShape = shape ?? CreateDefaultShape(name, fields.IsDefault ? [] : fields);
        if (!resolvedShape.HasRole(ShapeRoles.Entity))
            throw new ArgumentException(message: $"Entity '{name.Value}' must reference a shape with role '{ShapeRoles.Entity}'.", paramName: nameof(shape));
        if (resolvedShape.Fields.IsDefaultOrEmpty)
            throw new ArgumentException(
                message: $"Entity '{name}' must declare at least one field.",
                paramName: shape is null ? nameof(fields) : nameof(shape));
        try
        {
            Shape = resolvedShape.WithEntityType(name);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(exception.Message, nameof(shape), exception);
        }
        Invariants = invariants.IsDefault ? [] : invariants;
        ShapeGraph = shapeGraph;
    }

    /// <summary>
    /// Creates a semantic entity definition from an explicit shape.
    /// </summary>
    /// <param name="name">Stable logical entity type name.</param>
    /// <param name="shape">Explicit entity shape whose identifier and metadata are preserved.</param>
    /// <param name="invariants">Entity-level invariants.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is default, or <paramref name="shape"/> is not an entity shape, has no
    /// fields, or carries an entity-type annotation that contradicts <paramref name="name"/>.
    /// </exception>
    public EntityDefinition(
        EntityTypeName name,
        Shape shape,
        ImmutableArray<InvariantDefinition> invariants = default
        )
        : this(
            name: name,
            fields: Guard.RequireNotNull(shape).Fields,
            invariants: invariants,
            shape: shape,
            shapeGraph: null)
    {
    }

    /// <summary>Creates a graph-backed entity definition from one exact canonical root-shape snapshot.</summary>
    /// <param name="name">Stable logical entity type name.</param>
    /// <param name="shapeGraph">Exact graph-qualified root and immutable graph document.</param>
    /// <param name="invariants">Entity-level invariants.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shapeGraph"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">The referenced root shape is absent from the supplied graph.</exception>
    /// <exception cref="ArgumentException">The graph revision or entity shape is invalid or inconsistent.</exception>
    public EntityDefinition(
        EntityTypeName name,
        EntityShapeGraphBinding shapeGraph,
        ImmutableArray<InvariantDefinition> invariants = default)
        : this(
            name: name,
            resolvedShape: ResolveShape(name, shapeGraph),
            shapeGraph: shapeGraph,
            invariants: invariants)
    {
    }

    EntityDefinition(
        EntityTypeName name,
        Shape resolvedShape,
        EntityShapeGraphBinding shapeGraph,
        ImmutableArray<InvariantDefinition> invariants)
        : this(
            name: name,
            fields: resolvedShape.Fields,
            invariants: invariants,
            shape: resolvedShape,
            shapeGraph: shapeGraph)
    {
    }

    /// <summary>
    /// Logical entity name.
    /// </summary>
    public EntityTypeName Name { get; init; }
    
    /// <summary>
    /// Canonical entity shape.
    /// </summary>
    public Shape Shape { get; init; }

    /// <summary>
    /// Exact graph-qualified snapshot used to resolve named entity-state types, or null for an inline declaration.
    /// </summary>
    public EntityShapeGraphBinding? ShapeGraph { get; init; }

    /// <summary>
    /// Owned field definitions.
    /// </summary>
    public ImmutableArray<FieldDefinition> Fields => Shape.Fields;

    /// <summary>
    /// Entity-level annotations carried by the backing shape.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations => Shape.Annotations;

    /// <summary>
    /// Entity-level invariants.
    /// </summary>
    public ImmutableArray<InvariantDefinition> Invariants { get; init; }

    /// <summary>Validates this entity's inline or graph-backed state-schema linkage.</summary>
    /// <returns>Structured deterministic shape-graph diagnostics.</returns>
    public DocumentValidationResult ValidateShapeGraph() => EntityShapeGraphValidator.Validate(this);
    
    /// <summary>
    /// Creates an immutable state snapshot for this entity definition after validating field names and types.
    /// </summary>
    /// <param name="entityId">The stable identity of the entity instance.</param>
    /// <param name="values">The field values keyed by canonical field name.</param>
    /// <param name="version">The entity-state version.</param>
    /// <returns>A validated immutable entity-state snapshot.</returns>
    /// <exception cref="ArgumentException"><paramref name="entityId"/> is empty or consists only of white-space characters.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    /// <exception cref="SemanticRuleViolationException">Thrown when the supplied values do not satisfy this entity schema.</exception>
    public EntityState CreateState(string entityId, IReadOnlyDictionary<string, ObservationValue> values, long version = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(values);
        var observed = ObservationValue.FromObject(values);
        var stateValues = observed.Fields ?? throw new SemanticRuleViolationException($"State for entity type '{Name.Value}' must be an object value.");
        var normalizedValues = NormalizeStateValues(stateValues);
        ValidateStateValues(normalizedValues);
        return BuildEntityState(entityId: new(entityId), normalizedValues, version);
    }

    internal EntityState CreateState(string entityId, IReadOnlyDictionary<string, ObservationValue> values, long version, EntityStateLineage lineage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(lineage);
        var observed = ObservationValue.FromObject(values);
        var stateValues = observed.Fields ?? throw new SemanticRuleViolationException($"State for entity type '{Name.Value}' must be an object value.");
        var normalizedValues = NormalizeStateValues(stateValues);
        ValidateStateValues(normalizedValues);
        return BuildEntityState(entityId: new(entityId), valuesByName: normalizedValues, version: version, lineage: lineage);
    }

    /// <summary>
    /// Creates a state snapshot with an ephemeral generated entity id.
    /// </summary>
    /// <param name="values">The field values keyed by canonical field name.</param>
    /// <param name="version">The entity-state version.</param>
    /// <returns>A validated immutable entity-state snapshot with a generated identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    /// <exception cref="SemanticRuleViolationException">The supplied values do not satisfy this entity schema.</exception>
    public EntityState CreateState(IReadOnlyDictionary<string, ObservationValue> values, long version = 0) =>
        CreateState(entityId: Guid.NewGuid().ToString("N"), values, version);
    
    /// <summary>
    /// Creates an immutable state snapshot from an object expression after validating it against this entity schema.
    /// </summary>
    /// <param name="entityId">The stable identity of the entity instance.</param>
    /// <param name="stateObject">The object whose properties supply field values.</param>
    /// <param name="version">The entity-state version.</param>
    /// <returns>A validated immutable entity-state snapshot.</returns>
    /// <exception cref="ArgumentException"><paramref name="entityId"/> is empty or consists only of white-space characters.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stateObject"/> is <see langword="null"/>.</exception>
    /// <exception cref="SemanticRuleViolationException">Thrown when the supplied values do not satisfy this entity schema.</exception>
    public EntityState CreateState(string entityId, object stateObject, long version = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(stateObject);
        var observed = ObservationValue.FromObject(stateObject);
        if (observed.Kind != ObservationValueKind.Object || observed.Fields is null)
            throw new SemanticRuleViolationException($"State for entity type '{Name.Value}' must serialize to a JSON object.");
        var normalizedValues = NormalizeStateValues(observed.Fields);
        ValidateStateValues(normalizedValues);
        return BuildEntityState(new EntityId(entityId), normalizedValues, version);
    }

    /// <summary>
    /// Creates a state snapshot with an ephemeral generated entity id.
    /// </summary>
    /// <param name="stateObject">The object whose properties supply field values.</param>
    /// <param name="version">The entity-state version.</param>
    /// <returns>A validated immutable entity-state snapshot with a generated identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stateObject"/> is <see langword="null"/>.</exception>
    /// <exception cref="SemanticRuleViolationException">The supplied values do not satisfy this entity schema.</exception>
    public EntityState CreateState(object stateObject, long version = 0) =>
        CreateState(entityId: Guid.NewGuid().ToString("N"), stateObject: stateObject, version: version);

    /// <summary>
    /// Creates an entity state from an observation after validating its shape and field values.
    /// </summary>
    /// <param name="observation">The canonical observation to validate and wrap.</param>
    /// <returns>An immutable entity state backed by <paramref name="observation"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="observation"/> is <see langword="null"/>.</exception>
    /// <exception cref="SemanticRuleViolationException">
    /// The observation shape, fields, or values do not satisfy this entity definition.
    /// </exception>
    public EntityState CreateState(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.ShapeId != Shape.Id)
            throw new SemanticRuleViolationException($"Observation shape '{observation.ShapeId.Value}' does not match entity '{Name.Value}' shape '{Shape.Id.Value}'.");

        var validationPlan = StateValidationPlanByEntityDefinition.GetValue(key: this, createValueCallback: static definition => StateValidationPlan.Build(definition));
        ValidateObservationStateValues(observation, validationPlan);
        return new(observation);
    }

    /// <summary>
    /// Validates an existing state against the complete portable entity definition, including field contracts,
    /// computed fields, declarative constraints, and entity invariants.
    /// </summary>
    /// <param name="state">Entity state to validate without mutation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="SemanticRuleViolationException">
    /// The state violates its shape, field, computed-field, constraint, or invariant semantics.
    /// </exception>
    public void ValidateState(EntityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var validated = CreateState(state.Observation);
        new EntityStateInterpreter(this).ValidateState(state.EntityId.Value, validated);
    }

    void ValidateStateValues(IReadOnlyDictionary<string, ObservationValue> values)
    {
        var validationPlan = StateValidationPlanByEntityDefinition.GetValue(
            key: this,
            createValueCallback: static definition => StateValidationPlan.Build(definition)
            );

        foreach (var (fieldName, value) in values)
        {
            if (!validationPlan.FieldByName.TryGetValue(fieldName, out var field))
                throw new SemanticRuleViolationException($"State for entity type '{Name.Value}' contains unknown field '{fieldName}'.");

            EnsureFieldValueMatchesType(field, value);
        }

        foreach (var requiredField in validationPlan.RequiredFields)
        {
            if (requiredField.Mutability == FieldMutability.Computed)
                continue;

            if (!values.TryGetValue(requiredField.Name.Value, out var value)
                || value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
            {
                throw new SemanticRuleViolationException($"State for entity type '{Name.Value}' is missing required field '{requiredField.Name.Value}'.");
            }
        }
    }

    void ValidateObservationStateValues(Observation observation, StateValidationPlan validationPlan)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(validationPlan);

        for (var ordinal = 0; ordinal < observation.Layout.Count; ordinal++)
        {
            if (!observation.TryGetField(ordinal, out var value))
                continue;

            var fieldName = observation.Layout.FieldNames[ordinal];
            if (!validationPlan.FieldByName.TryGetValue(fieldName, out var field))
                throw new SemanticRuleViolationException($"Observation for entity type '{Name.Value}' contains unknown field '{fieldName}'.");

            EnsureFieldValueMatchesType(field, value);
        }

        foreach (var requiredField in validationPlan.RequiredFields)
        {
            if (requiredField.Mutability is FieldMutability.Computed)
                continue;

            if (!observation.TryGetField(requiredField, out var value) || value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
                throw new SemanticRuleViolationException($"State for entity type '{Name.Value}' is missing required field '{requiredField.Name.Value}'.");
        }
    }

    void EnsureFieldValueMatchesType(FieldDefinition field, ObservationValue value)
    {
        if (value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
        {
            if (field.Presence == FieldPresence.Required)
                throw new SemanticRuleViolationException($"State for entity type '{Name.Value}' contains null for required field '{field.Name.Value}'.");

            return;
        }

        if (field.Cardinality == FieldCardinality.Single)
        {
            EnsureValueMatchesType(
                type: field.Type,
                value: value,
                context: $"state field '{field.Name.Value}'"
                );
            return;
        }

        if (value.Kind != ObservationValueKind.Array)
            throw new SemanticRuleViolationException($"State for entity type '{Name.Value}' contains a non-array value for collection field '{field.Name.Value}'.");

        foreach (var item in value.EnumerateArray())
        {
            EnsureValueMatchesType(
                type: field.Type,
                value: item,
                context: $"state field '{field.Name.Value}' element"
                );
        }
    }

    void EnsureValueMatchesType(TypeRef type, ObservationValue value, string context)
    {
        if (!JsonTypeSemantics.MatchesType(
                type: type,
                value: value,
                graph: ShapeGraph?.Document.Graph))
            throw new SemanticRuleViolationException($"{context} on entity type '{Name.Value}' does not satisfy expected type '{JsonTypeSemantics.DescribeType(type)}'.");
    }

    EntityState BuildEntityState(EntityId entityId, IReadOnlyDictionary<string, ObservationValue> valuesByName, long version, EntityStateLineage? lineage = null)
    {
        var observation = new Observation(
            shapeId: Shape.Id,
            id: entityId.Value,
            fields: new Dictionary<string, ObservationValue>(valuesByName, StringComparer.Ordinal),
            version: version
            );
        return lineage is null ? new(observation) : new(observation, lineage);
    }

    sealed class StateValidationPlan(
        Dictionary<string, FieldDefinition> fieldByName,
        IReadOnlyList<FieldDefinition> requiredFields
        )
    {
        public Dictionary<string, FieldDefinition> FieldByName { get; } = fieldByName;

        public IReadOnlyList<FieldDefinition> RequiredFields { get; } = requiredFields;

        public static StateValidationPlan Build(EntityDefinition definition)
        {
            var shapeGraphValidation = definition.ValidateShapeGraph();
            if (!shapeGraphValidation.IsValid)
                throw new EntityShapeGraphValidationException(shapeGraphValidation.Diagnostics);

            Dictionary<string, FieldDefinition> fieldByName = new(definition.Fields.Length, StringComparer.Ordinal);
            List<FieldDefinition> requiredFields = [];
            foreach (var field in definition.Fields)
            {
                fieldByName[field.Name.Value] = field;
                if (field.Presence == FieldPresence.Required)
                    requiredFields.Add(field);
            }

            return new(fieldByName, requiredFields);
        }
    }

    Dictionary<string, ObservationValue> NormalizeStateValues(
        IReadOnlyDictionary<string, ObservationValue> values,
        StateValidationPlan? validationPlan = null,
        string source = "State"
        )
    {
        validationPlan ??= StateValidationPlanByEntityDefinition.GetValue(key: this, createValueCallback: static definition => StateValidationPlan.Build(definition));

        Dictionary<string, ObservationValue> normalized = new(StringComparer.Ordinal);
        foreach (var (fieldName, value) in values)
        {
            if (!validationPlan.FieldByName.TryGetValue(fieldName, out var field))
                throw new SemanticRuleViolationException($"{source} for entity type '{Name.Value}' contains unknown field '{fieldName}'.");

            if (!normalized.TryAdd(field.Name.Value, value))
                throw new SemanticRuleViolationException($"{source} for entity type '{Name.Value}' contains multiple aliases for field '{field.Name.Value}'.");
        }

        return normalized;
    }

	static Shape CreateDefaultShape(EntityTypeName name, ImmutableArray<FieldDefinition> fields) => new(
	    id: new($"shape.entity.{name.Value}"),
	    role: ShapeRoles.Entity,
	    fields: fields
	);

    static Shape ResolveShape(EntityTypeName name, EntityShapeGraphBinding shapeGraph)
    {
        ArgumentNullException.ThrowIfNull(shapeGraph);
        if (shapeGraph.Shape.GraphId != shapeGraph.Document.Graph.Id)
        {
            throw new ArgumentException(
                $"Entity shape graph revision '{shapeGraph.Shape.GraphId.Value}' does not match supplied snapshot '{shapeGraph.Document.Graph.Id.Value}'.",
                nameof(shapeGraph));
        }

        var source = shapeGraph.Document.Graph.GetShape(shapeGraph.Shape.ShapeId);
        return new Shape(
                id: source.Id,
                fields: source.Fields,
                constraints: source.Constraints,
                annotations: source.Annotations,
                role: ShapeRoles.Entity)
            .WithEntityType(name);
    }
}
