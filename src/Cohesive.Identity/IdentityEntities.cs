using Cohesive.Transitions.Authoring;

namespace Cohesive.Identity;

/// <summary>
/// Semantic identity model entities.
/// </summary>
public static class IdentityDomainModel
{
    /// <summary>Security scope entity.</summary>
    public static readonly IdentityScope Scope = IdentityScope.Instance;

    /// <summary>Principal account entity.</summary>
    public static readonly PrincipalAccount PrincipalAccount = PrincipalAccount.Instance;

    /// <summary>Principal membership in a scope.</summary>
    public static readonly ScopeMembership ScopeMembership = ScopeMembership.Instance;
}

/// <summary>
/// Security scope such as a tenant, workspace, organization, or system domain.
/// </summary>
public sealed class IdentityScope : Entity<IdentityScope>
{
    /// <summary>Input used to rename a scope.</summary>
    /// <param name="Name">New display name.</param>
    /// <param name="UpdatedAtUtc">Update timestamp.</param>
    public sealed record RenameScopeInput(string Name, DateTimeOffset UpdatedAtUtc);

    /// <summary>Input used to archive a scope.</summary>
    /// <param name="ArchivedAtUtc">Archive timestamp.</param>
    public sealed record ArchiveScopeInput(DateTimeOffset ArchivedAtUtc);

    /// <summary>Initializes a new instance of the identity scope type.</summary>
    public IdentityScope()
    {
        Id = WriteOnceField<string>(nameof(Id));
        Kind = WriteOnceField<string>(nameof(Kind));
        Name = MutableField<string>(nameof(Name));
        ParentScopeId = MutableField<string?>(nameof(ParentScopeId));
        PartitionKey = MutableField<string?>(nameof(PartitionKey));
        Status = MutableField<IdentityScopeStatus>(nameof(Status));
        CreatedAtUtc = WriteOnceField<DateTimeOffset>(nameof(CreatedAtUtc));
        UpdatedAtUtc = MutableField<DateTimeOffset?>(nameof(UpdatedAtUtc));
        ArchivedAtUtc = MutableField<DateTimeOffset?>(nameof(ArchivedAtUtc));

        Invariant("IdentityScopeIdentityIsRequired", scope =>
            scope.Id != "" &&
            scope.Kind != "" &&
            scope.Name != ""
        );

        Rename = Transition<RenameScopeInput>(nameof(Rename), t => t
            .Requires("ScopeMustBeActive", (scope, _) => scope.Status == IdentityScopeStatus.Active)
            .Set(scope => scope.Name, (_, input) => input.Name)
            .Set(scope => scope.UpdatedAtUtc, (_, input) => input.UpdatedAtUtc)
        );

        Archive = Transition<ArchiveScopeInput>(nameof(Archive), t => t
            .Requires("ScopeMustNotAlreadyBeArchived", (scope, _) => scope.Status != IdentityScopeStatus.Archived)
            .Set(scope => scope.Status, (_, _) => IdentityScopeStatus.Archived)
            .Set(scope => scope.ArchivedAtUtc, (_, input) => input.ArchivedAtUtc)
            .Set(scope => scope.UpdatedAtUtc, (_, input) => input.ArchivedAtUtc)
        );
    }

    /// <summary>Stable scope identifier.</summary>
    public Field<string> Id { get; }

    /// <summary>Scope kind, for example <c>sample.tenant</c>.</summary>
    public Field<string> Kind { get; }

    /// <summary>Human-facing scope name.</summary>
    public Field<string> Name { get; }

    /// <summary>Optional parent scope identifier for hierarchical scope models.</summary>
    public Field<string?> ParentScopeId { get; }

    /// <summary>Optional physical partition key interpretation.</summary>
    public Field<string?> PartitionKey { get; }

    /// <summary>Scope lifecycle status.</summary>
    public Field<IdentityScopeStatus> Status { get; }

    /// <summary>Creation timestamp.</summary>
    public Field<DateTimeOffset> CreatedAtUtc { get; }

    /// <summary>Last update timestamp.</summary>
    public Field<DateTimeOffset?> UpdatedAtUtc { get; }

    /// <summary>Archive timestamp.</summary>
    public Field<DateTimeOffset?> ArchivedAtUtc { get; }

    /// <summary>Renames the scope.</summary>
    public Transition<IdentityScope, RenameScopeInput> Rename { get; }

    /// <summary>Archives the scope.</summary>
    public Transition<IdentityScope, ArchiveScopeInput> Archive { get; }
}

/// <summary>
/// Scope lifecycle status.
/// </summary>
public enum IdentityScopeStatus : byte
{
    /// <summary>Scope is active.</summary>
    Active = 0,

    /// <summary>Scope is archived.</summary>
    Archived = 1
}

/// <summary>
/// Principal account known to the identity directory.
/// </summary>
public sealed class PrincipalAccount : Entity<PrincipalAccount>
{
    /// <summary>Input used to deactivate a principal.</summary>
    /// <param name="DeactivatedAtUtc">Deactivation timestamp.</param>
    public sealed record DeactivatePrincipalInput(DateTimeOffset DeactivatedAtUtc);

    /// <summary>Initializes a new instance of the principal account type.</summary>
    public PrincipalAccount()
    {
        Id = WriteOnceField<string>(nameof(Id));
        Kind = WriteOnceField<PrincipalKind>(nameof(Kind));
        DisplayName = MutableField<string?>(nameof(DisplayName));
        Email = MutableField<string?>(nameof(Email));
        Subject = MutableField<string?>(nameof(Subject));
        ClientId = MutableField<string?>(nameof(ClientId));
        Status = MutableField<PrincipalAccountStatus>(nameof(Status));
        CreatedAtUtc = WriteOnceField<DateTimeOffset>(nameof(CreatedAtUtc));
        UpdatedAtUtc = MutableField<DateTimeOffset?>(nameof(UpdatedAtUtc));
        DeactivatedAtUtc = MutableField<DateTimeOffset?>(nameof(DeactivatedAtUtc));

        Invariant("PrincipalAccountIdentityIsRequired", principal => principal.Id != "");

        Deactivate = Transition<DeactivatePrincipalInput>(nameof(Deactivate), t => t
            .Requires("PrincipalMustBeActive", (principal, _) => principal.Status == PrincipalAccountStatus.Active)
            .Set(principal => principal.Status, (_, _) => PrincipalAccountStatus.Deactivated)
            .Set(principal => principal.DeactivatedAtUtc, (_, input) => input.DeactivatedAtUtc)
            .Set(principal => principal.UpdatedAtUtc, (_, input) => input.DeactivatedAtUtc));
    }

    /// <summary>Stable principal identifier.</summary>
    public Field<string> Id { get; }

    /// <summary>Principal kind.</summary>
    public Field<PrincipalKind> Kind { get; }

    /// <summary>Human-facing display name.</summary>
    public Field<string?> DisplayName { get; }

    /// <summary>Optional email or user principal name.</summary>
    public Field<string?> Email { get; }

    /// <summary>Optional external subject claim value.</summary>
    public Field<string?> Subject { get; }

    /// <summary>Optional OAuth client identifier.</summary>
    public Field<string?> ClientId { get; }

    /// <summary>Principal lifecycle status.</summary>
    public Field<PrincipalAccountStatus> Status { get; }

    /// <summary>Creation timestamp.</summary>
    public Field<DateTimeOffset> CreatedAtUtc { get; }

    /// <summary>Last update timestamp.</summary>
    public Field<DateTimeOffset?> UpdatedAtUtc { get; }

    /// <summary>Deactivation timestamp.</summary>
    public Field<DateTimeOffset?> DeactivatedAtUtc { get; }

    /// <summary>Deactivates the principal.</summary>
    public Transition<PrincipalAccount, DeactivatePrincipalInput> Deactivate { get; }
}

/// <summary>
/// Principal account lifecycle status.
/// </summary>
public enum PrincipalAccountStatus : byte
{
    /// Principal may be authorized.
    Active = 0,

    /// Principal is deactivated.
    Deactivated = 1
}

/// <summary>
/// Principal membership and capability grant in a scope.
/// </summary>
public sealed class ScopeMembership : Entity<ScopeMembership>
{
    /// <summary>Input used to replace membership capabilities.</summary>
    /// <param name="Capabilities">Replacement capability identifiers.</param>
    /// <param name="UpdatedAtUtc">Update timestamp.</param>
    public sealed record ReplaceCapabilitiesInput(string[] Capabilities, DateTimeOffset UpdatedAtUtc);

    /// <summary>Input used to revoke a membership.</summary>
    /// <param name="RevokedAtUtc">Revocation timestamp.</param>
    public sealed record RevokeMembershipInput(DateTimeOffset RevokedAtUtc);

    /// <summary>Initializes a new instance of the scope membership type.</summary>
    public ScopeMembership()
    {
        Id = WriteOnceField<string>(nameof(Id));
        PrincipalId = WriteOnceField<string>(nameof(PrincipalId));
        ScopeId = WriteOnceField<string>(nameof(ScopeId));
        ScopeKind = WriteOnceField<string>(nameof(ScopeKind));
        Capabilities = MutableField<string[]>(nameof(Capabilities));
        IsDefaultScope = MutableField<bool>(nameof(IsDefaultScope));
        Status = MutableField<ScopeMembershipStatus>(nameof(Status));
        CreatedAtUtc = WriteOnceField<DateTimeOffset>(nameof(CreatedAtUtc));
        UpdatedAtUtc = MutableField<DateTimeOffset?>(nameof(UpdatedAtUtc));
        RevokedAtUtc = MutableField<DateTimeOffset?>(nameof(RevokedAtUtc));

        Invariant("ScopeMembershipIdentityIsRequired", membership =>
            membership.Id != "" &&
            membership.PrincipalId != "" &&
            membership.ScopeId != "" &&
            membership.ScopeKind != "");

        ReplaceCapabilities = Transition<ReplaceCapabilitiesInput>(nameof(ReplaceCapabilities), t => t
            .Requires("MembershipMustBeActive", (membership, _) => membership.Status == ScopeMembershipStatus.Active)
            .Set(membership => membership.Capabilities, (_, input) => input.Capabilities)
            .Set(membership => membership.UpdatedAtUtc, (_, input) => input.UpdatedAtUtc));

        Revoke = Transition<RevokeMembershipInput>(nameof(Revoke), t => t
            .Requires("MembershipMustBeActive", (membership, _) => membership.Status == ScopeMembershipStatus.Active)
            .Set(membership => membership.Status, (_, _) => ScopeMembershipStatus.Revoked)
            .Set(membership => membership.RevokedAtUtc, (_, input) => input.RevokedAtUtc)
            .Set(membership => membership.UpdatedAtUtc, (_, input) => input.RevokedAtUtc));
    }

    /// <summary>Stable membership identifier.</summary>
    public Field<string> Id { get; }

    /// <summary>Principal receiving scope access.</summary>
    public Field<string> PrincipalId { get; }

    /// <summary>Scope identifier.</summary>
    public Field<string> ScopeId { get; }

    /// <summary>Scope kind.</summary>
    public Field<string> ScopeKind { get; }

    /// <summary>Capability identifiers granted by the membership.</summary>
    public Field<string[]> Capabilities { get; }

    /// <summary>Whether this scope should be selected by default for the principal.</summary>
    public Field<bool> IsDefaultScope { get; }

    /// <summary>Membership lifecycle status.</summary>
    public Field<ScopeMembershipStatus> Status { get; }

    /// <summary>Creation timestamp.</summary>
    public Field<DateTimeOffset> CreatedAtUtc { get; }

    /// <summary>Last update timestamp.</summary>
    public Field<DateTimeOffset?> UpdatedAtUtc { get; }

    /// <summary>Revocation timestamp.</summary>
    public Field<DateTimeOffset?> RevokedAtUtc { get; }

    /// <summary>Replaces membership capabilities.</summary>
    public Transition<ScopeMembership, ReplaceCapabilitiesInput> ReplaceCapabilities { get; }

    /// <summary>Revokes the membership.</summary>
    public Transition<ScopeMembership, RevokeMembershipInput> Revoke { get; }
}

/// <summary>
/// Scope membership lifecycle status.
/// </summary>
public enum ScopeMembershipStatus : byte
{
    /// <summary>Membership is active.</summary>
    Active = 0,

    /// <summary>Membership has been revoked.</summary>
    Revoked = 1
}
