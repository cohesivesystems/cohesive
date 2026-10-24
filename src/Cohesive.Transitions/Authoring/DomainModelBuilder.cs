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
    readonly List<(DomainDiscovery Discovery, DomainDiscoveryCoverage Coverage)> verifications = [];

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
    public DomainModelDefinition Build()
    {
        var model = new DomainModelDefinition(entities: [..entities], version: version, annotations: annotations?.ToImmutable());
        var diagnostics = verifications.SelectMany(check => check.Discovery.Verify(model, check.Coverage).Diagnostics).ToImmutableArray();
        if (!diagnostics.IsEmpty)
            throw new DomainDiscoveryException(new(diagnostics));
        return model;
    }

    /// <summary>Adds a scoped discovery catalog through the ordinary canonical entity path.</summary>
    /// <param name="discovery">Immutable, explicitly scoped declarations.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">The catalog is null.</exception>
    /// <exception cref="ArgumentException">An identity is already registered; no entities are added.</exception>
    public DomainModelBuilder Discover(DomainDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        foreach (var entity in discovery.Entities)
            if (entities.Any(existing => existing.Name == entity.Name))
                throw new ArgumentException($"Entity '{entity.Name}' is already declared.", nameof(discovery));
        entities.AddRange(discovery.Entities);
        return this;
    }

    /// <summary>Schedules coverage verification at Build, independent of registration order.</summary>
    /// <param name="discovery">Scoped declarations to compare.</param>
    /// <param name="coverage">Whether explicit additional entities are allowed.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">The catalog is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The coverage mode is unknown.</exception>
    public DomainModelBuilder VerifyDiscovery(DomainDiscovery discovery,
        DomainDiscoveryCoverage coverage = DomainDiscoveryCoverage.DiscoveredEntities)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        if (!Enum.IsDefined(coverage)) throw new ArgumentOutOfRangeException(nameof(coverage));
        verifications.Add((discovery, coverage));
        return this;
    }
}
