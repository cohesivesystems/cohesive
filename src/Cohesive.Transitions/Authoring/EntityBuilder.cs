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
    ImmutableDictionary<AnnotationKey, AnnotationValue>.Builder? annotations;

    /// <summary>
    /// Creates an entity builder for the supplied type name.
    /// </summary>
    /// <param name="name">The stable logical entity type name.</param>
    public EntityBuilder(EntityTypeName name)
    {
        this.name = name;
    }

    /// <summary>
    /// Adds a field definition to the entity using the canonical field name.
    /// </summary>
    /// <param name="name">The canonical field name.</param>
    /// <param name="type">The semantic field type.</param>
    /// <param name="configure">Optional field configuration applied before the field is materialized.</param>
    /// <returns>This builder for further declarations.</returns>
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
    /// <param name="definition">The canonical field definition to add.</param>
    /// <returns>This builder for further declarations.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public EntityBuilder Field(FieldDefinition definition)
    {
        fields.Add(Guard.RequireNotNull(definition));
        return this;
    }

    /// <summary>
    /// Adds an annotation entry at the entity shape level.
    /// </summary>
    /// <param name="key">The annotation key.</param>
    /// <param name="value">The annotation value.</param>
    /// <returns>This builder for further declarations.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
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
    /// <typeparam name="TValue">The CLR value type.</typeparam>
    /// <param name="key">The annotation key.</param>
    /// <param name="value">The value to encode as an annotation.</param>
    /// <returns>This builder for further declarations.</returns>
    public EntityBuilder Annotation<TValue>(string key, TValue value) =>
        Annotation(key, AnnotationValue.FromObject(value));

    /// <summary>
    /// Adds an invariant definition to the entity.
    /// </summary>
    /// <param name="name">The stable invariant name.</param>
    /// <param name="expression">The portable predicate that must hold for valid entity state.</param>
    /// <param name="message">An optional violation message.</param>
    /// <returns>This builder for further declarations.</returns>
    public EntityBuilder Invariant(string name, Expr expression, string? message = null)
    {
        invariants.Add(new InvariantDefinition(name: name, expression: expression, message: message));
        return this;
    }

    /// <summary>
    /// Adds an existing invariant definition to the entity.
    /// </summary>
    /// <param name="definition">The canonical invariant definition to add.</param>
    /// <returns>This builder for further declarations.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public EntityBuilder Invariant(InvariantDefinition definition)
    {
        invariants.Add(Guard.RequireNotNull(definition));
        return this;
    }

    /// <summary>
    /// Materializes the immutable entity definition.
    /// </summary>
    /// <returns>The canonical entity definition produced by this builder.</returns>
    public EntityDefinition Build()
    {
        return new(
            name: name,
            shape: new Shape(
                id: new($"shape.entity.{name.Value}"),
                role: ShapeRoles.Entity,
                fields: [..fields],
                annotations: annotations?.ToImmutable()),
            invariants: [.. invariants]
        );
    }
}
