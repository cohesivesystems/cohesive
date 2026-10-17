using System.Security.Cryptography;
using Cohesive.Model;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Processes.Authoring;

/// <summary>Declares typed entity point reads without hand-authoring hosted-query metadata.</summary>
public static class EntityPointReadAuthoring
{
    /// <summary>Derives the canonical identity-key read and optional entity result from an entity declaration.</summary>
    /// <typeparam name="TEntity">Entity state.</typeparam>
    /// <param name="entity">Exact entity authority; owns the cached read's lifetime.</param>
    /// <returns>The shared observational read for this declaration, without resolving persistence.</returns>
    /// <exception cref="ArgumentNullException">Entity is null.</exception>
    /// <remarks>Identity follows the entity name; the revision fingerprints its canonical definition and
    /// the fixed read protocol. Repeated process occurrences retain the same attachment authority.</remarks>
    public static EntityPointRead<TEntity, string, EntityPointReadResult<TEntity>> PointRead<TEntity>(
        this DomainEntity<TEntity> entity) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        return Derived<TEntity>.Reads.Get(entity, static value =>
        {
            var bytes = StrictDocumentJson.GetCanonicalBytes(value.Definition, Derived<TEntity>.Options);
            var revision = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return value.PointRead(new($"{value.Definition.Name.Value}/point-read"), new($"v1-{revision}"),
                static item => new EntityPointReadResult<TEntity>(item));
        });
    }

    static class Derived<TEntity> where TEntity : class
    {
        internal static readonly WeakPreparationCache<DomainEntity<TEntity>, EntityPointRead<TEntity, string, EntityPointReadResult<TEntity>>> Reads = new();
        internal static readonly System.Text.Json.JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();
    }

    /// <summary>Declares an observational read by entity key in the repository's trusted partition.</summary>
    /// <typeparam name="TEntity">Entity state.</typeparam>
    /// <typeparam name="TResult">Projected result, including an explicit representation of absence.</typeparam>
    /// <param name="entity">Canonical entity authority used to resolve persistence at host setup.</param>
    /// <param name="id">Stable read identity, also used to identify its host implementation.</param>
    /// <param name="revision">Read contract and result-projection revision; change when projection behavior changes.</param>
    /// <param name="result">Thread-safe projection of the entity or null when absent; retained as a host attachment.</param>
    /// <returns>A typed dependency accepted by ProcessContext.Query, with a string entity key as input.</returns>
    /// <exception cref="ArgumentNullException">Entity or result is null.</exception>
    /// <exception cref="ArgumentException">Identity, revision, or the typed contract is invalid.</exception>
    /// <remarks>Preparation performs no storage IO. The generated dependency selects the entity's configured
    /// repository and trusted partition. The result delegate is not portable IR: the explicit revision owns
    /// its behavior. Use the EntityPointRead constructor to attach an independently declared query contract.</remarks>
    public static EntityPointRead<TEntity, string, TResult> PointRead<TEntity, TResult>(
        this DomainEntity<TEntity> entity, ExecutionDefinitionId id, ExecutionRevisionId revision,
        Func<TEntity?, TResult> result) where TEntity : class where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(result);
        var query = HostedQuery<string, TResult>.Create(
            id, revision, new($"{id.Value}/entity-point-read", revision.Value),
            entity.Definition.Shape.Id.Value,
            new(new("cohesive.processes.entity-point-read/v1"), new(id.Value), DocumentOrigin.Generated));
        return new(query, entity, static key => key, result);
    }
}

/// <summary>A point-read observation with explicit absence, carried in a concrete query-result envelope.</summary>
/// <typeparam name="TEntity">Entity state.</typeparam>
/// <param name="Entity">Observed state, or null if no entity exists at the selected key and trusted partition.</param>
public sealed record EntityPointReadResult<TEntity>(TEntity? Entity) where TEntity : class;
