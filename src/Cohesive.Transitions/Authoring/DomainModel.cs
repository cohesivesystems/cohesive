using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>Instance-owned domain authoring over the existing domain builder.</summary>
/// <remarks>Declare entities during construction, then share the instance. First Definition access freezes
/// authoring and caches either the immutable definition or its validation failure. No virtual method runs
/// from the base constructor, and no reflection discovers members.</remarks>
public abstract class DomainModel
{
    readonly DomainModelBuilder builder;
    readonly object gate = new();
    readonly Lazy<DomainModelDefinition> definition;
    bool frozen;

    /// <summary>Creates an independently owned domain declaration.</summary>
    /// <param name="revision">Explicit semantic version, independent of the CLR class name.</param>
    /// <exception cref="ArgumentException">Revision is empty.</exception>
    protected DomainModel(string revision)
    {
        builder = new DomainModelBuilder().Version(revision);
        definition = new(() =>
        {
            lock (gate)
            {
                frozen = true;
                return builder.Build();
            }
        });
    }

    /// <summary>The cached canonical domain definition; first access completes authoring.</summary>
    /// <exception cref="ArgumentException">The domain is empty or invalid.</exception>
    public DomainModelDefinition Definition => definition.Value;

    /// <summary>Declares an entity through the same builder available to inline callers.</summary>
    /// <typeparam name="T">Canonical state authoring type.</typeparam>
    /// <param name="name">Stable semantic entity name.</param>
    /// <returns>The exact typed handle retained by the definition.</returns>
    /// <exception cref="InvalidOperationException">Definition has already been requested.</exception>
    /// <exception cref="ArgumentException">The entity name is invalid or duplicated.</exception>
    protected DomainEntity<T> Entity<T>(string name) where T : notnull
    {
        lock (gate)
        {
            if (frozen) throw new InvalidOperationException("The domain definition is frozen.");
            return builder.Entity<T>(name);
        }
    }
}
