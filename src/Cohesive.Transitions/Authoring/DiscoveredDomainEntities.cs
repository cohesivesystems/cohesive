using System.Collections.Immutable;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>Read-only typed selection over one catalog registered into a particular domain.</summary>
/// <remarks>Handles share the domain's exact definitions. The scope exposes no builder mutation,
/// excludes omitted declarations, and remains valid after the domain freezes. If selection fails,
/// retained handles remain usable for authoring but their definitions are not registered in the domain;
/// they cannot stand in for a later registration's storage authority.</remarks>
public sealed class DiscoveredDomainEntities
{
    readonly ImmutableDictionary<Type, EntityDefinition> entities;
    internal DiscoveredDomainEntities(ImmutableDictionary<Type, EntityDefinition> entities) => this.entities = entities;

    /// <summary>Resolves an included entity without repeating its semantic name.</summary>
    /// <typeparam name="T">Included CLR entity type.</typeparam>
    /// <returns>A handle to the owning domain's exact definition.</returns>
    /// <exception cref="ArgumentException">The type is outside this catalog or was excluded.</exception>
    public DomainEntity<T> Entity<T>() where T : notnull => entities.TryGetValue(typeof(T), out var entity)
        ? new(entity)
        : throw new ArgumentException($"'{typeof(T)}' is not included in this domain discovery scope.");
}
