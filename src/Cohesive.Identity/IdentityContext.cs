using System.Collections.Immutable;

namespace Cohesive.Identity;

/// <summary>
/// An authenticated or ambient principal.
/// </summary>
/// <param name="Id">Stable principal identifier in the identity domain.</param>
/// <param name="Kind">Principal classification used by authorization policy.</param>
/// <param name="DisplayName">Optional human-facing display name.</param>
public sealed record PrincipalRef(
    string Id,
    PrincipalKind Kind,
    string? DisplayName = null
    );

/// <summary>
/// Principal classifications supported by the generic identity model.
/// </summary>
public enum PrincipalKind
{
    /// <summary>User account authenticated interactively.</summary>
    User = 0,

    /// <summary>Machine-to-machine API or service account.</summary>
    ServiceAccount = 1,

    /// <summary>Trusted system account used for platform operations.</summary>
    SystemAccount = 2,

    /// <summary>Unauthenticated or anonymous principal.</summary>
    Anonymous = 3
}

/// <summary>
/// A security scope such as a tenant, workspace, organization, or system domain.
/// </summary>
/// <param name="Id">Stable scope identifier.</param>
/// <param name="Kind">Scope kind, for example <c>sample.tenant</c>.</param>
/// <param name="DisplayName">Optional human-facing display name.</param>
/// <param name="PartitionKey">Optional physical partition key interpretation for this scope.</param>
/// <param name="ParentScopeId">Optional parent scope for hierarchical authorization models.</param>
public sealed record ScopeRef(
    string Id,
    string Kind,
    string? DisplayName = null,
    string? PartitionKey = null,
    string? ParentScopeId = null
    );

/// <summary>
/// Effective scope for an operation selected by a request.
/// </summary>
/// <param name="Scopes">Authorized scopes selected for this operation.</param>
/// <param name="Mode">Selection cardinality and meaning.</param>
/// <param name="Source">Source that supplied the selected scope.</param>
public sealed record EffectiveScope(
    ImmutableArray<ScopeRef> Scopes,
    ScopeSelectionMode Mode,
    ScopeSelectionSource Source
    )
{
    /// <summary>
    /// Returns the single selected scope, or <see langword="null"/> when the selection is multi-scope.
    /// </summary>
    public ScopeRef? SingleScope => Scopes.Length == 1 ? Scopes[0] : null;
}

/// <summary>
/// Scope selection cardinality requested or resolved for an operation.
/// </summary>
public enum ScopeSelectionMode
{
    /// <summary>No explicit selection was supplied; the resolver should choose a default.</summary>
    Default = 0,

    /// <summary>Exactly one scope is selected.</summary>
    Single = 1,

    /// <summary>Several explicitly selected scopes are selected.</summary>
    Multiple = 2,

    /// <summary>All scopes accessible to the principal are selected.</summary>
    AllAccessible = 3,

    /// <summary>System-wide scope is selected.</summary>
    System = 4
}

/// <summary>
/// Source from which the scope selection was derived.
/// </summary>
public enum ScopeSelectionSource
{
    /// <summary>No request source supplied the scope.</summary>
    Default = 0,

    /// <summary>Scope came from an OAuth/JWT claim.</summary>
    TokenClaim = 1,

    /// <summary>Scope came from an HTTP request header.</summary>
    RequestHeader = 2,

    /// <summary>Scope came from an API request body.</summary>
    RequestBody = 3,

    /// <summary>Scope came from ambient host state.</summary>
    Ambient = 4,

    /// <summary>Scope was selected by trusted system policy.</summary>
    System = 5,

    /// <summary>Scope came from an HTTP query string parameter.</summary>
    RequestQuery = 6,

    /// <summary>Scope came from an HTTP route parameter.</summary>
    RequestRoute = 7
}

/// <summary>
/// Authorization grant over a scope.
/// </summary>
/// <param name="Grantee">Principal receiving the grant.</param>
/// <param name="Scope">Scope covered by this grant.</param>
/// <param name="Capabilities">Capability identifiers granted in the scope.</param>
/// <param name="Source">Grant source, such as membership, policy, or bootstrap data.</param>
/// <param name="ExpiresAtUtc">Optional expiration timestamp.</param>
public sealed record IdentityScopeGrant(
    PrincipalRef Grantee,
    ScopeRef Scope,
    ImmutableArray<string> Capabilities,
    string Source,
    DateTimeOffset? ExpiresAtUtc = null
    );

/// <summary>
/// Identity resolved from transport claims, scope selection, memberships, and policy.
/// </summary>
/// <param name="Actor">Credential-bearing principal that is actually executing the operation.</param>
/// <param name="Subject">Optional principal represented by explicit delegation.</param>
/// <param name="Initiator">Optional principal that initiated later async or system work.</param>
/// <param name="EffectiveScope">Effective scope selected for the operation.</param>
/// <param name="Grants">Authorized grants visible to this operation.</param>
public sealed record IdentityContext(
    PrincipalRef Actor,
    PrincipalRef? Subject = null,
    PrincipalRef? Initiator = null,
    EffectiveScope? EffectiveScope = null,
    ImmutableArray<IdentityScopeGrant> Grants = default
    );
