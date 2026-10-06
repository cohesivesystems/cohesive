using Cohesive.Storage;
using Cohesive.Transitions.Model;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Entities;

/// <summary>Discoverable native ASP.NET entry point for typed entity API binding.</summary>
public static class TypedEntityApiEndpointRouteBuilderExtensions
{
    /// <summary>Configures typed endpoint bindings and maps them once through the existing entity API pipeline.</summary>
    /// <typeparam name="TEntity">POCO materialized from the canonical entity definition.</typeparam>
    /// <param name="routes">Native endpoint builder, including WebApplication and route groups.</param>
    /// <param name="entity">Canonical entity authority.</param>
    /// <param name="repository">Caller-owned repository using that exact definition.</param>
    /// <param name="partition">Explicit point-read partition; not an authorization policy.</param>
    /// <param name="configure">Synchronous binding authoring; supports combined or separate declarations.</param>
    /// <returns>The same endpoint builder.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Repository, partition or endpoint contracts are incompatible.</exception>
    /// <exception cref="InvalidOperationException">A binding is duplicated, incomplete or fails preparation.</exception>
    /// <remarks>Preparation is registration-scoped. The completed binding session cannot be mutated or mapped again.</remarks>
    public static IEndpointRouteBuilder MapEntityApi<TEntity>(this IEndpointRouteBuilder routes,
        EntityDefinition entity, IEntityRepository repository, string partition,
        Action<TypedEntityApiBindings<TEntity>> configure) where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(configure);
        var bindings = new TypedEntityApiBindings<TEntity>(entity, repository, partition);
        configure(bindings);
        bindings.Map(routes);
        return routes;
    }
}
