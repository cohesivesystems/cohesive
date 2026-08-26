using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Physical;
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
    static readonly RelationQueryPhysicalPlanningPolicy DefaultPhysicalPlanningPolicy =
        CreateDefaultPhysicalPlanningPolicy();

    /// <summary>
    /// Creates a repository-backed identity directory over these in-memory repositories.
    /// </summary>
    /// <param name="physicalPlanningPolicy">
    /// Optional bounded canonical planning policy; <see langword="null"/> uses deterministic in-memory defaults.
    /// </param>
    /// <returns>An identity directory backed by these repositories.</returns>
    /// <exception cref="ArgumentNullException">One of the repository properties is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A repository does not represent its expected graph-qualified Identity shape.
    /// </exception>
    public IIdentityDirectory CreateDirectory(
        RelationQueryPhysicalPlanningPolicy? physicalPlanningPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(Scopes);
        ArgumentNullException.ThrowIfNull(PrincipalAccounts);
        ArgumentNullException.ThrowIfNull(ScopeMemberships);
        EntityRelationQuerySourceCatalog sources = new(
        [
            EntityRelationQuerySourceRegistration.InMemory(
                IdentityDomainModel.ScopeShape,
                Scopes,
                RelationQueryLogicalPartitionIdentity.WholeSource),
            EntityRelationQuerySourceRegistration.InMemory(
                IdentityDomainModel.PrincipalAccountShape,
                PrincipalAccounts,
                RelationQueryLogicalPartitionIdentity.WholeSource),
            EntityRelationQuerySourceRegistration.InMemory(
                IdentityDomainModel.ScopeMembershipShape,
                ScopeMemberships,
                RelationQueryLogicalPartitionIdentity.WholeSource)
        ]);
        return new EntityRepositoryIdentityDirectory(
            sources.CreateEvaluator(physicalPlanningPolicy ?? DefaultPhysicalPlanningPolicy));
    }

    static RelationQueryPhysicalPlanningPolicy CreateDefaultPhysicalPlanningPolicy()
    {
        var limits = InMemoryEntityRelationQuerySourceReader.DefaultLimits;
        return new(
            new("cohesive.identity/in-memory-directory/v1"),
            conventionSetVersion: "cohesive.identity/in-memory-directory-conventions/v1",
            maximumBatchSize: limits.MaximumBatchSize,
            maximumBufferedRows: limits.MaximumBufferedRows,
            maximumLocalRows: limits.MaximumBufferedRows,
            maximumFanOut: limits.MaximumFanOut,
            maximumReferenceKeysPerObservation: limits.MaximumBatchSize,
            maximumConcurrency: limits.MaximumConcurrency);
    }
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
    /// <returns>Seeded in-memory identity repositories.</returns>
    public static InMemoryIdentityDomainRepositories Create(
        InMemoryIdentityDirectory directory
        )
    {
        ArgumentNullException.ThrowIfNull(directory);
        return Create(
            scopes: directory.Scopes,
            principals: directory.Principals,
            memberships: directory.Memberships
            );
    }

    /// <summary>
    /// Creates seeded in-memory repositories for the identity domain.
    /// </summary>
    /// <param name="scopes">Scope records to seed.</param>
    /// <param name="principals">Principal account records to seed.</param>
    /// <param name="memberships">Scope membership records to seed.</param>
    /// <returns>Seeded in-memory identity repositories.</returns>
    public static InMemoryIdentityDomainRepositories Create(
        IEnumerable<IdentityScopeRecord> scopes,
        IEnumerable<PrincipalAccountRecord> principals,
        IEnumerable<ScopeMembershipRecord> memberships
        )
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(principals);
        ArgumentNullException.ThrowIfNull(memberships);

        return new(
            Scopes: new InMemoryEntityOutboxRepository(
                IdentityDomainModel.Scope.Definition,
                seedData: scopes.Select(ToIdentityScopeSeed),
                partitionKeyFieldName: nameof(IdentityScopeRecord.Kind)
                ),
            PrincipalAccounts: new InMemoryEntityOutboxRepository(
                IdentityDomainModel.PrincipalAccount.Definition,
                seedData: principals.Select(ToPrincipalAccountSeed),
                partitionKeyFieldName: nameof(PrincipalAccountRecord.Id)
                ),
            ScopeMemberships: new InMemoryEntityOutboxRepository(
                IdentityDomainModel.ScopeMembership.Definition,
                seedData: memberships.Select(ToScopeMembershipSeed),
                partitionKeyFieldName: nameof(ScopeMembershipRecord.PrincipalId)
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
