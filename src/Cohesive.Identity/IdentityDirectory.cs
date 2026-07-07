using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.Queries;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Identity;

/// <summary>
/// Directory lookup keys extracted from an authenticated request principal.
/// </summary>
/// <param name="PrincipalId">Optional local principal id.</param>
/// <param name="Email">Optional email claim.</param>
/// <param name="Subject">Optional identity-provider subject claim.</param>
/// <param name="ClientId">Optional OAuth client id claim.</param>
public sealed record IdentityPrincipalLookup(
    string? PrincipalId = null,
    string? Email = null,
    string? Subject = null,
    string? ClientId = null
    );

/// <summary>
/// Directory-backed scope grant materialized from an active membership and active scope.
/// </summary>
/// <param name="Scope">Scope covered by the grant.</param>
/// <param name="Membership">Membership that grants capabilities over the scope.</param>
public sealed record IdentityScopeGrantRecord(
    IdentityScopeRecord Scope,
    ScopeMembershipRecord Membership
    );

/// <summary>
/// Identity directory used by request identity resolution.
/// </summary>
public interface IIdentityDirectory
{
    /// <summary>
    /// Finds an active principal account by one of its stable identity keys.
    /// </summary>
    /// <param name="lookup">Lookup keys extracted from transport claims or host policy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matched principal account, or <see langword="null"/> when no active account matches.</returns>
    ValueTask<PrincipalAccountRecord?> FindPrincipalAsync(
        IdentityPrincipalLookup lookup,
        CancellationToken ct = default
        );

    /// <summary>
    /// Lists active scope grants for a principal.
    /// </summary>
    /// <param name="principalId">Principal id whose grants should be listed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Active membership/scope grant records for the principal.</returns>
    ValueTask<ImmutableArray<IdentityScopeGrantRecord>> ListScopeGrantsAsync(
        string principalId,
        CancellationToken ct = default
        );

    /// <summary>
    /// Finds the principal's default active scope for a scope kind among visible candidate scopes.
    /// </summary>
    /// <param name="principalId">Principal id whose default scope should be resolved.</param>
    /// <param name="scopeKind">Scope kind being selected.</param>
    /// <param name="candidateScopeIds">Visible candidate scope ids.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The default scope id, or <see langword="null"/> when no visible default exists.</returns>
    ValueTask<string?> FindDefaultScopeIdAsync(
        string principalId,
        string scopeKind,
        IReadOnlySet<string> candidateScopeIds,
        CancellationToken ct = default
        );
}

/// <summary>
/// Identity directory backed by the semantic identity entity repositories.
/// </summary>
/// <param name="scopeRepository">Query repository for <see cref="IdentityDomainModel.Scope"/>.</param>
/// <param name="principalRepository">Query repository for <see cref="IdentityDomainModel.PrincipalAccount"/>.</param>
/// <param name="membershipRepository">Query repository for <see cref="IdentityDomainModel.ScopeMembership"/>.</param>
/// <param name="mappingContext">Optional mapping context used to materialize identity records.</param>
public sealed class EntityRepositoryIdentityDirectory(
    IEntityQueryRepository scopeRepository,
    IEntityQueryRepository principalRepository,
    IEntityQueryRepository membershipRepository,
    ShapeMappingContext? mappingContext = null
    ) : IIdentityDirectory
{
    readonly ShapeMappingContext mappingContext = mappingContext ?? principalRepository.MappingContext;

    /// <inheritdoc />
    public async ValueTask<PrincipalAccountRecord?> FindPrincipalAsync(
        IdentityPrincipalLookup lookup,
        CancellationToken ct = default
        )
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ct.ThrowIfCancellationRequested();
        EnsureRepository(scopeRepository, IdentityDomainModel.Scope);
        EnsureRepository(principalRepository, IdentityDomainModel.PrincipalAccount);
        EnsureRepository(membershipRepository, IdentityDomainModel.ScopeMembership);

        var principalId = IdentityBootstrap.NormalizeOptional(lookup.PrincipalId);
        if (principalId is not null)
            return await FindPrincipalByFieldAsync(nameof(PrincipalAccountRecord.Id), principalId, ct);

        var email = IdentityBootstrap.NormalizeEmail(lookup.Email);
        if (email is not null)
            return await FindPrincipalByFieldAsync(nameof(PrincipalAccountRecord.Email), email, ct);

        var subject = IdentityBootstrap.NormalizeOptional(lookup.Subject);
        if (subject is not null)
            return await FindPrincipalByFieldAsync(nameof(PrincipalAccountRecord.Subject), subject, ct);

        var clientId = IdentityBootstrap.NormalizeOptional(lookup.ClientId);
        if (clientId is not null)
            return await FindPrincipalByFieldAsync(nameof(PrincipalAccountRecord.ClientId), clientId, ct);

        return null;
    }

    /// <inheritdoc />
    public async ValueTask<ImmutableArray<IdentityScopeGrantRecord>> ListScopeGrantsAsync(
        string principalId,
        CancellationToken ct = default
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ct.ThrowIfCancellationRequested();

        if (await FindPrincipalAsync(new(PrincipalId: principalId), ct) is null)
            return ImmutableArray<IdentityScopeGrantRecord>.Empty;

        var context = OperationContext.Create(cancellationToken: ct);
        var memberships = await QueryRecordsAsync<ScopeMembershipRecord>(
            membershipRepository,
            context,
            Equal(nameof(ScopeMembershipRecord.PrincipalId), principalId)
            );
        var grants = ImmutableArray.CreateBuilder<IdentityScopeGrantRecord>();
        foreach (var membership in memberships)
        {
            if (membership.Status != ScopeMembershipStatus.Active)
                continue;

            var scope = await FindActiveScopeAsync(membership.ScopeKind, membership.ScopeId, ct);
            if (scope is not null)
                grants.Add(new(scope, membership));
        }

        return grants.ToImmutable();
    }

    /// <inheritdoc />
    public async ValueTask<string?> FindDefaultScopeIdAsync(
        string principalId,
        string scopeKind,
        IReadOnlySet<string> candidateScopeIds,
        CancellationToken ct = default
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKind);
        ArgumentNullException.ThrowIfNull(candidateScopeIds);
        ct.ThrowIfCancellationRequested();

        if (candidateScopeIds.Count == 0
            || await FindPrincipalAsync(new(PrincipalId: principalId), ct) is null)
        {
            return null;
        }

        var context = OperationContext.Create(cancellationToken: ct);
        var memberships = await QueryRecordsAsync<ScopeMembershipRecord>(
            membershipRepository,
            context,
            And(
                EqualField(nameof(ScopeMembershipRecord.PrincipalId), principalId),
                EqualField(nameof(ScopeMembershipRecord.ScopeKind), scopeKind),
                EqualField(nameof(ScopeMembershipRecord.IsDefaultScope), true)
                )
            );

        foreach (var membership in memberships)
        {
            if (membership.Status != ScopeMembershipStatus.Active
                || !candidateScopeIds.Contains(membership.ScopeId))
            {
                continue;
            }

            var scope = await FindActiveScopeAsync(membership.ScopeKind, membership.ScopeId, ct);
            if (scope is not null)
                return membership.ScopeId;
        }

        return null;
    }

    async ValueTask<PrincipalAccountRecord?> FindPrincipalByFieldAsync(
        string fieldName,
        string value,
        CancellationToken ct
        )
    {
        var context = OperationContext.Create(cancellationToken: ct);
        var records = await QueryRecordsAsync<PrincipalAccountRecord>(
            principalRepository,
            context,
            Equal(fieldName, value)
            );

        return records.FirstOrDefault(static principal => principal.Status == PrincipalAccountStatus.Active);
    }

    async ValueTask<IdentityScopeRecord?> FindActiveScopeAsync(
        string scopeKind,
        string scopeId,
        CancellationToken ct
        )
    {
        var context = OperationContext.Create(cancellationToken: ct);
        var records = await QueryRecordsAsync<IdentityScopeRecord>(
            scopeRepository,
            context,
            And(
                EqualField(nameof(IdentityScopeRecord.Kind), scopeKind),
                EqualField(nameof(IdentityScopeRecord.Id), scopeId)
                ),
            limit: 2
            );

        return records.FirstOrDefault(static scope => scope.Status == IdentityScopeStatus.Active);
    }

    async ValueTask<ImmutableArray<TRecord>> QueryRecordsAsync<TRecord>(
        IEntityQueryRepository repository,
        OperationContext context,
        EntityPredicate predicate,
        int? limit = null
        ) where TRecord : notnull
    {
        var response = await repository.Query(
            context,
            EntityQuery.ForRows(
                predicate,
                window: limit is null
                    ? null
                    : new ResultPageOptions(Limit: limit, Mode: ResultPaginationMode.Offset)
                )
            ).ConfigureAwait(false);

        return [.. response.Rows.Select(snapshot => snapshot.Entity.Map<TRecord>(mappingContext))];
    }

    static EntityPredicate Equal(string fieldName, object value) =>
        new(EqualField(fieldName, value));

    static EntityPredicate And(params FieldPredicate[] predicates) =>
        new(BoolExpr.And(predicates));

    static FieldPredicate EqualField(string fieldName, object value) =>
        new(FieldPath.FromField(fieldName), ValuePredicate.EqualTo(value));

    static void EnsureRepository(IEntityRepository repository, Entity entity)
    {
        if (!string.Equals(repository.EntityDefinition.Shape.Id.Value, entity.Definition.Shape.Id.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Identity repository for '{entity.Definition.Shape.Id.Value}' cannot use repository for '{repository.EntityDefinition.Shape.Id.Value}'."
                );
        }
    }
}
