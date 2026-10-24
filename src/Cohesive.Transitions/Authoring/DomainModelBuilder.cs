using System.Collections.Immutable;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Fluent builder for top-level domain model definitions.
/// </summary>
public sealed class DomainModelBuilder
{
    readonly List<EntityDefinition> entities = [];
    readonly HashSet<string> entityNames = new(StringComparer.Ordinal);
    readonly Dictionary<Type, EntityDefinition> discovered = [];
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
        if (entityNames.Contains(name))
            throw new ArgumentException($"Entity '{name}' is already declared.", nameof(name));
        var builder = new EntityBuilder(name: new EntityTypeName(value: name));
        configure(obj: builder);
        entities.Add(item: builder.Build());
        entityNames.Add(name);
        return this;
    }

    /// <summary>Declares a POCO entity or adopts a matching discovered declaration for downstream bindings.</summary>
    /// <typeparam name="T">Canonical state authoring type.</typeparam>
    /// <param name="name">Stable semantic name, independent of the CLR type name.</param>
    /// <returns>A handle retaining the exact definition included in Build.</returns>
    /// <exception cref="ArgumentException">The entity name is already declared, invalid, or differs from its discovered name.</exception>
    public DomainEntity<T> Entity<T>(string name) where T : notnull
    {
        if (discovered.TryGetValue(typeof(T), out var adopted))
        {
            if (adopted.Name.Value != name)
                throw new ArgumentException($"Discovered entity '{typeof(T)}' is named '{adopted.Name.Value}', not '{name}'.", nameof(name));
            return new(adopted);
        }
        if (entityNames.Contains(name))
            throw new ArgumentException($"Entity '{name}' is already declared.", nameof(name));
        var definition = ObjectEntityDefinition.For<T>(new(name));
        entities.Add(definition);
        entityNames.Add(name);
        return new(definition);
    }

    /// <summary>Resolves a discovered entity owned by this domain without repeating its semantic name.</summary>
    /// <typeparam name="T">An included discovered CLR entity type.</typeparam>
    /// <returns>A typed handle sharing this domain's exact registered definition.</returns>
    /// <exception cref="ArgumentException">The type has not been discovered or was excluded.</exception>
    public DomainEntity<T> Entity<T>() where T : notnull => discovered.TryGetValue(typeof(T), out var entity)
        ? new(entity)
        : throw new ArgumentException($"'{typeof(T)}' is not a discovered entity in this domain.");

    /// <summary>
    /// Materializes the immutable domain model definition.
    /// </summary>
    /// <exception cref="DomainDiscoveryException">Scheduled discovery verification fails.</exception>
    /// <exception cref="ArgumentException">The domain is empty or invalid.</exception>
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
        var declarations = discovery.IncludedDeclarations;
        foreach (var (_, entity) in declarations)
            if (entityNames.Contains(entity.Name.Value))
                throw new ArgumentException($"Entity '{entity.Name}' is already declared.", nameof(discovery));
        foreach (var (type, template) in declarations)
        {
            // Retain immutable shape preparation, but preserve domain-local authority for native bindings.
            var entity = template with { };
            discovered.Add(type, entity);
            entityNames.Add(entity.Name.Value);
            entities.Add(entity);
        }
        return this;
    }

    /// <summary>Registers a catalog and projects its domain-owned typed handles in one call.</summary>
    /// <typeparam name="TResult">Caller-selected handle group, typically a tuple.</typeparam>
    /// <param name="discovery">Catalog to register.</param>
    /// <param name="select">Setup-only projection; its scope cannot mutate the builder.</param>
    /// <returns>The projected handles.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">Registration conflicts or selection requests an excluded type.</exception>
    /// <remarks>Registration completes before selection. If the callback throws, the entities remain registered.</remarks>
    public TResult Discover<TResult>(DomainDiscovery discovery, Func<DiscoveredDomainEntities, TResult> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        Discover(discovery);
        var selected = ImmutableDictionary.CreateBuilder<Type, EntityDefinition>();
        foreach (var (type, _) in discovery.IncludedDeclarations)
            selected.Add(type, discovered[type]);
        return select(new(selected.ToImmutable()));
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
