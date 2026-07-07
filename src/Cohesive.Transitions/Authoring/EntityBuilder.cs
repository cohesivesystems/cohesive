using System.Collections.Immutable;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Fluent builder for semantic entity definitions.
/// </summary>
public sealed class EntityBuilder
{
    readonly EntityTypeName name;
    readonly List<FieldDefinition> fields = [];
    readonly List<InvariantDefinition> invariants = [];
    readonly List<TransitionDefinition> transitions = [];
    ImmutableDictionary<AnnotationKey, AnnotationValue>.Builder? annotations;

    /// <summary>
    /// Creates an entity builder for the supplied type name.
    /// </summary>
    public EntityBuilder(EntityTypeName name)
    {
        this.name = name;
    }

    /// <summary>
    /// Adds a field definition to the entity using the canonical field name.
    /// </summary>
    public EntityBuilder Field(
        string name,
        TypeRef type,
        Action<FieldBuilder>? configure = null)
    {
        var fieldBuilder = new FieldBuilder(new FieldName(value: name), type);
        configure?.Invoke(fieldBuilder);
        fields.Add(fieldBuilder.Build());
        return this;
    }

    /// <summary>
    /// Adds an existing field definition to the entity.
    /// </summary>
    public EntityBuilder Field(FieldDefinition definition)
    {
        fields.Add(Guard.RequireNotNull(definition));
        return this;
    }

    /// <summary>
    /// Adds an annotation entry at the entity shape level.
    /// </summary>
    public EntityBuilder Annotation(string key, AnnotationValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        annotations ??= ImmutableDictionary.CreateBuilder<AnnotationKey, AnnotationValue>();
        annotations[new AnnotationKey(key)] = value;
        return this;
    }

    /// <summary>
    /// Adds an annotation entry at the entity shape level from a CLR value or object graph.
    /// </summary>
    public EntityBuilder Annotation<TValue>(string key, TValue value) =>
        Annotation(key, AnnotationValue.FromObject(value));

    /// <summary>
    /// Adds an invariant definition to the entity.
    /// </summary>
    public EntityBuilder Invariant(string name, Expr expression, string? message = null)
    {
        invariants.Add(new InvariantDefinition(name: name, expression: expression, message: message));
        return this;
    }

    /// <summary>
    /// Adds an existing invariant definition to the entity.
    /// </summary>
    public EntityBuilder Invariant(InvariantDefinition definition)
    {
        invariants.Add(Guard.RequireNotNull(definition));
        return this;
    }

    /// <summary>
    /// Adds a transition definition to the entity.
    /// </summary>
    public EntityBuilder Transition(string name, Action<TransitionBuilder>? configure = null)
    {
        var transitionBuilder = new TransitionBuilder();
        configure?.Invoke(transitionBuilder);
        transitions.Add(transitionBuilder.Build(name: name));
        return this;
    }

    /// <summary>
    /// Adds an existing transition definition to the entity.
    /// </summary>
    public EntityBuilder Transition(TransitionDefinition definition)
    {
        transitions.Add(Guard.RequireNotNull(definition));
        return this;
    }

    /// <summary>
    /// Adds a transition authored with typed C# expressions.
    /// </summary>
    public EntityBuilder Transition<TEntity, TParameters>(
        string name,
        Action<TransitionExpressionBuilder<TEntity, TParameters>> configure,
        ITransitionExpressionCompiler? compiler = null
        ) where TEntity : Entity
    {
        ArgumentNullException.ThrowIfNull(argument: configure);
        if (fields.Count == 0)
        {
            throw new TransitionExpressionTranslationException(
                message: "Entity must declare at least one field before adding an expression-authored transition.");
        }

        var resolvedCompiler = compiler ?? new TransitionExpressionCompiler();
        var provisionalDefinition = new EntityDefinition(
            name: this.name,
            fields: [.. fields],
            invariants: [.. invariants],
            transitions: [.. transitions]
            );
        transitions.Add(resolvedCompiler.Compile(
            entityDefinition: provisionalDefinition,
            transitionName: name,
            configure: configure)
        );
        return this;
    }

    /// <summary>
    /// Materializes the immutable entity definition.
    /// </summary>
    public EntityDefinition Build()
    {
	        return new(
	            name: name,
	            shape: new Shape(
	                id: new($"shape.entity.{name.Value}"),
	                role: ShapeRoles.Entity,
	                fields: [..fields],
	                annotations: annotations?.ToImmutable()),
            invariants: [.. invariants],
            transitions: [.. transitions]
        );
    }
}
