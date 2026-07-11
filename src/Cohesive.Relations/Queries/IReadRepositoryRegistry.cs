using System.Collections.Concurrent;

namespace Cohesive.Relations.Queries;

/// <summary>
/// Repository registry used by query execution.
/// </summary>
public interface IReadRepositoryRegistry
{
    /// <summary>
    /// Resolves the repository registered for the supplied query source.
    /// </summary>
    /// <exception cref="InvalidOperationException">No read repository is registered for the given source.</exception>
    IReadRepository GetRequired(QuerySource source);
}

/// <summary>
/// A mutable repository registry that resolves repositories by <see cref="QuerySource"/>.
/// </summary>
public sealed class DispatchingReadRepositoryRegistry : IReadRepositoryRegistry
{
    readonly ConcurrentDictionary<string, IReadRepository> repositories = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a repository for a source.
    /// </summary>
    /// <exception cref="InvalidOperationException">A read repository is already registered for the given source.</exception>
    public DispatchingReadRepositoryRegistry Register(QuerySource source, IReadRepository repository)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(repository);

        if (!repositories.TryAdd(source.Name, repository))
            throw new InvalidOperationException($"A read repository is already registered for source '{source.Name}'.");

        return this;
    }

    /// <inheritdoc />
    public IReadRepository GetRequired(QuerySource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (repositories.TryGetValue(source.Name, out var repository))
            return repository;

        throw new InvalidOperationException($"No read repository is registered for source '{source.Name}'.");
    }
}
