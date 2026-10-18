using Cohesive.Model;

namespace Cohesive.Storage;

/// <summary>One caller-owned entity repository and its canonical query acquisition source.</summary>
/// <remarks>The adapter supplies both from one storage declaration; this registration performs no IO and owns no clients.</remarks>
public sealed class EntityStorageRegistration
{
    /// <summary>Associates repository access and query acquisition for the same persisted entity.</summary>
    /// <param name="repository">Repository supplied by the storage declaration.</param>
    /// <param name="source">Query source supplied by that same declaration.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="ArgumentException">The source's persisted shape or identity differs from the repository.</exception>
    public EntityStorageRegistration(IEntityRepository repository, EntityRelationQuerySourceRegistration source)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(source);
        if (repository.EntityDefinition.StateShape.QualifiedId != source.PersistedObservationType
            || repository.IdentityField is not { } identity
            || source.IdentitySemanticPath != FieldPath.FromField(identity))
            throw new ArgumentException("Repository and query source must share persisted entity and declared identity.", nameof(source));
        Repository = repository;
        Source = source;
    }
    /// <summary>Caller-owned repository, including its native commit capabilities.</summary>
    public IEntityRepository Repository { get; }
    /// <summary>Exact runtime query source used by prepared placement.</summary>
    public EntityRelationQuerySourceRegistration Source { get; }
}
