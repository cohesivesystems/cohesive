using Cohesive.Prelude;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>Instance-owned domain authoring over the existing domain builder.</summary>
/// <remarks>Declare entities during construction, then share the instance. First Definition access freezes
/// authoring and caches either the immutable definition or its validation failure. No virtual method runs
/// from the base constructor, and discovery is explicit and scoped.</remarks>
public abstract class DomainModel : IAuthoringModel
{
    void IAuthoringModel.Complete() => _ = Definition;

    readonly DomainModelBuilder builder;
    readonly AuthoringState<DomainModelDefinition> definition;

    /// <summary>Creates an independently owned domain declaration.</summary>
    /// <param name="revision">Explicit semantic version, independent of the CLR class name.</param>
    /// <exception cref="ArgumentException">Revision is empty.</exception>
    protected DomainModel(string revision)
    {
        builder = new DomainModelBuilder().Version(revision);
        definition = new(builder.Build);
    }

    /// <summary>The cached canonical domain definition; first access completes authoring.</summary>
    /// <exception cref="ArgumentException">The domain is empty or invalid.</exception>
    public DomainModelDefinition Definition => definition.Value;

    /// <summary>Adds explicitly scoped discovered entities before this model freezes.</summary>
    /// <param name="discovery">Prepared discovery catalog.</param>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    /// <exception cref="ArgumentException">An entity identity is duplicated.</exception>
    protected void Discover(DomainDiscovery discovery) => definition.Edit(() => builder.Discover(discovery));

    /// <summary>Verifies discovered state-shape coverage when this model freezes.</summary>
    /// <param name="discovery">Prepared discovery catalog.</param>
    /// <param name="coverage">Whether explicit additional entities are allowed.</param>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    protected void VerifyDiscovery(DomainDiscovery discovery,
        DomainDiscoveryCoverage coverage = DomainDiscoveryCoverage.DiscoveredEntities) =>
        definition.Edit(() => builder.VerifyDiscovery(discovery, coverage));

    /// <summary>Declares an entity through the same builder available to inline callers.</summary>
    /// <typeparam name="T">Canonical state authoring type.</typeparam>
    /// <param name="name">Stable semantic entity name.</param>
    /// <returns>The exact typed handle retained by the definition.</returns>
    /// <exception cref="InvalidOperationException">Definition has already been requested.</exception>
    /// <exception cref="ArgumentException">The entity name is invalid or duplicated.</exception>
    protected DomainEntity<T> Entity<T>(string name) where T : notnull
    {
        return definition.Edit(() => builder.Entity<T>(name));
    }
}
