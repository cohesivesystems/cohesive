using System.Collections.Immutable;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Fluent builder for top-level domain model definitions.
/// </summary>
/// <remarks>Single-threaded setup object. All mutations and Build throw InvalidOperationException
/// during discovery selection, including calls through captured references. Read-only handle resolution
/// remains available. A selector failure clears the guard and leaves registration unchanged.</remarks>
public sealed class DomainModelBuilder
{
    readonly List<EntityDefinition> entities = [];
    readonly HashSet<string> entityNames = new(StringComparer.Ordinal);
    readonly Dictionary<Type, EntityDefinition> discovered = [];
    ImmutableDictionary<AnnotationKey, AnnotationValue>.Builder? annotations;
    string? version;
    bool selecting;
    readonly List<(DomainDiscovery Discovery, DomainDiscoveryCoverage Coverage)> verifications = [];

    /// <summary>
    /// Sets a model version string.
    /// </summary>
    /// <exception cref="InvalidOperationException">Discovery selection is active on this builder.</exception>
    public DomainModelBuilder Version(string version)
    {
        RequireNotSelecting();
        this.version = Guard.RequireNotNullOrWhiteSpace(value: version);
        return this;
    }

    /// <summary>
    /// Adds an annotation entry at the model level.
    /// </summary>
    /// <exception cref="InvalidOperationException">Discovery selection is active on this builder.</exception>
    public DomainModelBuilder Annotation(string key, AnnotationValue value)
    {
        RequireNotSelecting();
        ArgumentNullException.ThrowIfNull(value);
        annotations ??= ImmutableDictionary.CreateBuilder<AnnotationKey, AnnotationValue>();
        annotations[new(key)] = value;
        return this;
    }

    /// <summary>
    /// Adds an annotation entry at the model level from a CLR value or object graph.
    /// </summary>
    /// <exception cref="InvalidOperationException">Discovery selection is active on this builder.</exception>
    public DomainModelBuilder Annotation<TValue>(string key, TValue value)
    {
        RequireNotSelecting();
        return Annotation(key, AnnotationValue.FromObject(value));
    }

    /// <summary>
    /// Adds an entity definition.
    /// </summary>
    /// <exception cref="InvalidOperationException">Discovery selection is active on this builder.</exception>
    public DomainModelBuilder Entity(string name, Action<EntityBuilder> configure)
    {
        RequireNotSelecting();
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
    /// <remarks>Use this overload for explicit declarations. For discovery, prefer the Discover projection
    /// overload; matching named adoption is supported for shared explicit/discovery authoring code.</remarks>
    /// <exception cref="ArgumentException">The entity name is already declared, invalid, or differs from its discovered name.</exception>
    /// <exception cref="InvalidOperationException">Discovery selection is active on this builder.</exception>
    public DomainEntity<T> Entity<T>(string name) where T : notnull
    {
        RequireNotSelecting();
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
    /// <exception cref="InvalidOperationException">Discovery selection is active on this builder.</exception>
    public DomainModelDefinition Build()
    {
        RequireNotSelecting();
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
    /// <exception cref="InvalidOperationException">Discovery selection is active on this builder.</exception>
    public DomainModelBuilder Discover(DomainDiscovery discovery)
    {
        RequireNotSelecting();
        ArgumentNullException.ThrowIfNull(discovery);
        var declarations = discovery.IncludedDeclarations;
        RequireAvailable(declarations);
        foreach (var (type, template) in declarations)
            Register(type, template with { });
        return this;
    }

    /// <summary>Registers a catalog and projects its domain-owned typed handles in one call.</summary>
    /// <typeparam name="TResult">Caller-selected handle group, typically a tuple.</typeparam>
    /// <param name="discovery">Catalog to register.</param>
    /// <param name="select">Setup-only projection; its scope cannot mutate the builder.</param>
    /// <returns>The projected handles.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">Registration conflicts or selection requests an excluded type.</exception>
    /// <remarks>Selection runs on staged definitions. A failed selection registers nothing. Builder changes or materialization through captured references throw InvalidOperationException.</remarks>
    /// <exception cref="InvalidOperationException">Discovery selection is active on this builder.</exception>
    public TResult Discover<TResult>(DomainDiscovery discovery, Func<DiscoveredDomainEntities, TResult> select)
    {
        RequireNotSelecting();
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(select);
        var declarations = discovery.IncludedDeclarations;
        RequireAvailable(declarations);
        var selected = ImmutableDictionary.CreateBuilder<Type, EntityDefinition>();
        foreach (var (type, template) in declarations)
            // Share immutable shape preparation, but retain domain-local definition authority.
            selected.Add(type, template with { });
        var staged = selected.ToImmutable();
        TResult result;
        selecting = true;
        try { result = select(new(staged)); }
        finally { selecting = false; }
        foreach (var (type, _) in declarations)
            Register(type, staged[type]);
        return result;
    }

    void RequireAvailable(ImmutableArray<KeyValuePair<Type, EntityDefinition>> declarations)
    {
        foreach (var (type, entity) in declarations)
            if (entityNames.Contains(entity.Name.Value) || discovered.ContainsKey(type))
                throw new ArgumentException($"Entity '{entity.Name}' is already declared.", "discovery");
    }

    void Register(Type type, EntityDefinition entity)
    {
        discovered.Add(type, entity);
        entityNames.Add(entity.Name.Value);
        entities.Add(entity);
    }

    void RequireNotSelecting()
    {
        if (selecting)
            throw new InvalidOperationException("Domain builder changes and materialization are not allowed during discovery selection. Use the supplied read-only entity scope.");
    }

    /// <summary>Schedules coverage verification at Build, independent of registration order.</summary>
    /// <param name="discovery">Scoped declarations to compare.</param>
    /// <param name="coverage">Whether explicit additional entities are allowed.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">The catalog is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The coverage mode is unknown.</exception>
    /// <exception cref="InvalidOperationException">Discovery selection is active on this builder.</exception>
    public DomainModelBuilder VerifyDiscovery(DomainDiscovery discovery,
        DomainDiscoveryCoverage coverage = DomainDiscoveryCoverage.DiscoveredEntities)
    {
        RequireNotSelecting();
        ArgumentNullException.ThrowIfNull(discovery);
        if (!Enum.IsDefined(coverage)) throw new ArgumentOutOfRangeException(nameof(coverage));
        verifications.Add((discovery, coverage));
        return this;
    }
}
