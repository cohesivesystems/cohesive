using System.Collections.Immutable;

namespace Cohesive.Identity;

/// <summary>
/// Immutable in-memory identity directory used for bootstrap and tests.
/// </summary>
/// <param name="Scopes">Known scopes.</param>
/// <param name="Principals">Known principals.</param>
/// <param name="Memberships">Known scope memberships.</param>
public sealed record InMemoryIdentityDirectory(
    ImmutableArray<IdentityScopeRecord> Scopes,
    ImmutableArray<PrincipalAccountRecord> Principals,
    ImmutableArray<ScopeMembershipRecord> Memberships
    );

/// <summary>
/// In-memory projection of <see cref="IdentityScope"/>.
/// </summary>
public sealed record IdentityScopeRecord(
    string Id,
    string Kind,
    string Name,
    IdentityScopeStatus Status = IdentityScopeStatus.Active,
    string? ParentScopeId = null,
    string? PartitionKey = null
    );

/// <summary>
/// In-memory projection of <see cref="PrincipalAccount"/>.
/// </summary>
public sealed record PrincipalAccountRecord(
    string Id,
    PrincipalKind Kind,
    PrincipalAccountStatus Status = PrincipalAccountStatus.Active,
    string? DisplayName = null,
    string? Email = null,
    string? Subject = null,
    string? ClientId = null
    );

/// <summary>
/// In-memory projection of <see cref="ScopeMembership"/>.
/// </summary>
public sealed record ScopeMembershipRecord(
    string Id,
    string PrincipalId,
    string ScopeId,
    string ScopeKind,
    ImmutableArray<string> Capabilities,
    ScopeMembershipStatus Status = ScopeMembershipStatus.Active,
    bool IsDefaultScope = false
    );