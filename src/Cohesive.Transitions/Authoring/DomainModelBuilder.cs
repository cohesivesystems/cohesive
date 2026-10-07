using System.Collections.Immutable;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Fluent builder for top-level domain model definitions.
/// </summary>
public sealed class DomainModelBuilder
{
    readonly List<EntityDefinition> entities = [];
    ImmutableDictionary<AnnotationKey, AnnotationValue>.Builder? annotations;
    string? version;

    /// <summary>
    /// Sets a model version string.
    /// </summary>
    public DomainModelBuilder Version(string version)
    {
        this.version = Guard.RequireNotNullOrWhiteSpace(value: version);
        return this;
    }

    /// <summary>
    /// Adds an annotation entry at the model level.
    /// </summary>
    public DomainModelBuilder Annotation(string key, AnnotationValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        annotations ??= ImmutableDictionary.CreateBuilder<AnnotationKey, AnnotationValue>();
        annotations[new(key)] = value;
        return this;
    }

    /// <summary>
    /// Adds an annotation entry at the model level from a CLR value or object graph.
    /// </summary>
    public DomainModelBuilder Annotation<TValue>(string key, TValue value) =>
        Annotation(key, AnnotationValue.FromObject(value));

    /// <summary>
    /// Adds an entity definition.
    /// </summary>
    public DomainModelBuilder Entity(string name, Action<EntityBuilder> configure)
    {
        var builder = new EntityBuilder(name: new EntityTypeName(value: name));
        configure(obj: builder);
        entities.Add(item: builder.Build());
        return this;
    }

    /// <summary>Declares a POCO entity once and returns a typed reference for downstream bindings.</summary>
    /// <typeparam name="T">Canonical state authoring type.</typeparam>
    /// <param name="name">Stable semantic name, independent of the CLR type name.</param>
    /// <returns>A handle retaining the exact definition included in Build.</returns>
    /// <exception cref="ArgumentException">The entity name is already declared or invalid.</exception>
    public DomainEntity<T> Entity<T>(string name) where T : notnull
    {
        if (entities.Any(entity => entity.Name.Value == name))
            throw new ArgumentException($"Entity '{name}' is already declared.", nameof(name));
        var definition = ObjectEntityDefinition.For<T>(new(name));
        entities.Add(definition);
        return new(definition);
    }

    /// <summary>
    /// Materializes the immutable domain model definition.
    /// </summary>
    public DomainModelDefinition Build() =>
        new(entities: [..entities], version: version, annotations: annotations?.ToImmutable());
}
