using Cohesive.Relations.Mapping;
using Cohesive.Storage;

namespace Cohesive.Identity;

/// <summary>
/// In-memory repositories for the semantic identity domain.
/// </summary>
/// <param name="Scopes">Repository for <see cref="IdentityDomainModel.Scope"/>.</param>
/// <param name="PrincipalAccounts">Repository for <see cref="IdentityDomainModel.PrincipalAccount"/>.</param>
/// <param name="ScopeMemberships">Repository for <see cref="IdentityDomainModel.ScopeMembership"/>.</param>
public sealed record InMemoryIdentityDomainRepositories(
    InMemoryEntityOutboxRepository Scopes,
    InMemoryEntityOutboxRepository PrincipalAccounts,
    InMemoryEntityOutboxRepository ScopeMemberships
    )
{
    /// <summary>
    /// Creates a repository-backed identity directory over these in-memory repositories.
    /// </summary>
    /// <param name="mappingContext">Optional mapping context used to materialize identity records.</param>
    /// <returns>An identity directory backed by these repositories.</returns>
    public IIdentityDirectory CreateDirectory(ShapeMappingContext? mappingContext = null) =>
        new EntityRepositoryIdentityDirectory(
            Scopes,
            PrincipalAccounts,
            ScopeMemberships,
            mappingContext ?? Scopes.MappingContext
            );
}

/// <summary>
/// Factory methods for bootstrap identity repositories.
/// </summary>
public static class InMemoryIdentityDomainRepositoryFactory
{
    static readonly DateTimeOffset BootstrapCreatedAtUtc = DateTimeOffset.UnixEpoch;

    /// <summary>
    /// Creates seeded in-memory repositories for the identity domain.
    /// </summary>
    /// <param name="directory">In-memory identity records to seed.</param>
    /// <param name="mappingContext">Optional mapping context used by the repositories.</param>
    /// <returns>Seeded in-memory identity repositories.</returns>
    public static InMemoryIdentityDomainRepositories Create(
        InMemoryIdentityDirectory directory,
        ShapeMappingContext? mappingContext = null
        )
    {
        ArgumentNullException.ThrowIfNull(directory);
        return Create(
            scopes: directory.Scopes,
            principals: directory.Principals,
            memberships: directory.Memberships,
            mappingContext: mappingContext
            );
    }

    /// <summary>
    /// Creates seeded in-memory repositories for the identity domain.
    /// </summary>
    /// <param name="scopes">Scope records to seed.</param>
    /// <param name="principals">Principal account records to seed.</param>
    /// <param name="memberships">Scope membership records to seed.</param>
    /// <param name="mappingContext">Optional mapping context used by the repositories.</param>
    /// <returns>Seeded in-memory identity repositories.</returns>
    public static InMemoryIdentityDomainRepositories Create(
        IEnumerable<IdentityScopeRecord> scopes,
        IEnumerable<PrincipalAccountRecord> principals,
        IEnumerable<ScopeMembershipRecord> memberships,
        ShapeMappingContext? mappingContext = null
        )
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(principals);
        ArgumentNullException.ThrowIfNull(memberships);

        return new(
            Scopes: new InMemoryEntityOutboxRepository(
                IdentityDomainModel.Scope.Definition,
                seedData: scopes.Select(ToIdentityScopeSeed),
                partitionKeyFieldName: nameof(IdentityScopeRecord.Kind),
                mappingContext: mappingContext
                ),
            PrincipalAccounts: new InMemoryEntityOutboxRepository(
                IdentityDomainModel.PrincipalAccount.Definition,
                seedData: principals.Select(ToPrincipalAccountSeed),
                partitionKeyFieldName: nameof(PrincipalAccountRecord.Id),
                mappingContext: mappingContext
                ),
            ScopeMemberships: new InMemoryEntityOutboxRepository(
                IdentityDomainModel.ScopeMembership.Definition,
                seedData: memberships.Select(ToScopeMembershipSeed),
                partitionKeyFieldName: nameof(ScopeMembershipRecord.PrincipalId),
                mappingContext: mappingContext
                )
            );
    }

    static object ToIdentityScopeSeed(IdentityScopeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new
        {
            record.Id,
            record.Kind,
            record.Name,
            record.ParentScopeId,
            record.PartitionKey,
            record.Status,
            CreatedAtUtc = BootstrapCreatedAtUtc,
            UpdatedAtUtc = (DateTimeOffset?)null,
            ArchivedAtUtc = record.Status == IdentityScopeStatus.Archived
                ? BootstrapCreatedAtUtc
                : (DateTimeOffset?)null
        };
    }

    static object ToPrincipalAccountSeed(PrincipalAccountRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new
        {
            record.Id,
            record.Kind,
            record.DisplayName,
            record.Email,
            record.Subject,
            record.ClientId,
            record.Status,
            CreatedAtUtc = BootstrapCreatedAtUtc,
            UpdatedAtUtc = (DateTimeOffset?)null,
            DeactivatedAtUtc = record.Status == PrincipalAccountStatus.Deactivated
                ? BootstrapCreatedAtUtc
                : (DateTimeOffset?)null
        };
    }

    static object ToScopeMembershipSeed(ScopeMembershipRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new
        {
            record.Id,
            record.PrincipalId,
            record.ScopeId,
            record.ScopeKind,
            Capabilities = record.Capabilities.ToArray(),
            record.IsDefaultScope,
            record.Status,
            CreatedAtUtc = BootstrapCreatedAtUtc,
            UpdatedAtUtc = (DateTimeOffset?)null,
            RevokedAtUtc = record.Status == ScopeMembershipStatus.Revoked
                ? BootstrapCreatedAtUtc
                : (DateTimeOffset?)null
        };
    }
}
