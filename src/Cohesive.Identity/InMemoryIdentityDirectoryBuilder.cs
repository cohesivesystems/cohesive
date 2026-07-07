namespace Cohesive.Identity;

/// <summary>
/// Builder for validated in-memory identity directories used by bootstrap code and tests.
/// </summary>
public sealed class InMemoryIdentityDirectoryBuilder
{
    readonly List<IdentityScopeRecord> scopes = [];
    readonly List<PrincipalAccountRecord> principals = [];
    readonly List<ScopeMembershipRecord> memberships = [];
    readonly HashSet<(string Kind, string Id)> scopeKeys = [];
    readonly HashSet<string> principalIds = new(StringComparer.Ordinal);
    readonly HashSet<string> membershipIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds an identity scope to the directory.
    /// </summary>
    /// <param name="scope">Scope record to add.</param>
    /// <returns>The current builder.</returns>
    public InMemoryIdentityDirectoryBuilder AddScope(IdentityScopeRecord scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireNonWhiteSpace(scope.Id, nameof(scope.Id));
        RequireNonWhiteSpace(scope.Kind, nameof(scope.Kind));
        RequireNonWhiteSpace(scope.Name, nameof(scope.Name));

        var key = CreateScopeKey(scope.Kind, scope.Id);
        if (!scopeKeys.Add(key))
            throw new InvalidOperationException($"Duplicate identity scope '{scope.Kind}:{scope.Id}' was configured.");

        scopes.Add(scope);
        return this;
    }

    /// <summary>
    /// Adds a principal account to the directory.
    /// </summary>
    /// <param name="principal">Principal account record to add.</param>
    /// <returns>The current builder.</returns>
    public InMemoryIdentityDirectoryBuilder AddPrincipal(PrincipalAccountRecord principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        RequireNonWhiteSpace(principal.Id, nameof(principal.Id));

        if (!principalIds.Add(principal.Id))
            throw new InvalidOperationException($"Duplicate identity principal '{principal.Id}' was configured.");

        principals.Add(principal);
        return this;
    }

    /// <summary>
    /// Adds a scope membership to the directory.
    /// </summary>
    /// <param name="membership">Scope membership record to add.</param>
    /// <returns>The current builder.</returns>
    public InMemoryIdentityDirectoryBuilder AddMembership(ScopeMembershipRecord membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        RequireNonWhiteSpace(membership.Id, nameof(membership.Id));
        RequireNonWhiteSpace(membership.PrincipalId, nameof(membership.PrincipalId));
        RequireNonWhiteSpace(membership.ScopeId, nameof(membership.ScopeId));
        RequireNonWhiteSpace(membership.ScopeKind, nameof(membership.ScopeKind));

        if (!principalIds.Contains(membership.PrincipalId))
            throw new InvalidOperationException($"Identity membership '{membership.Id}' references unknown principal '{membership.PrincipalId}'.");

        if (!scopeKeys.Contains(CreateScopeKey(membership.ScopeKind, membership.ScopeId)))
            throw new InvalidOperationException($"Identity membership '{membership.Id}' references unknown scope '{membership.ScopeKind}:{membership.ScopeId}'.");

        if (!membershipIds.Add(membership.Id))
            throw new InvalidOperationException($"Duplicate identity membership '{membership.Id}' was configured.");

        if (membership.IsDefaultScope
            && membership.Status == ScopeMembershipStatus.Active
            && memberships.Any(existing =>
                existing.IsDefaultScope
                && existing.Status == ScopeMembershipStatus.Active
                && string.Equals(existing.PrincipalId, membership.PrincipalId, StringComparison.Ordinal)
                && string.Equals(existing.ScopeKind, membership.ScopeKind, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Principal '{membership.PrincipalId}' already has a default '{membership.ScopeKind}' scope.");
        }

        memberships.Add(membership);
        return this;
    }

    /// <summary>
    /// Adds a capability grant over a scope as an in-memory scope membership.
    /// </summary>
    /// <param name="principalId">Principal receiving the grant.</param>
    /// <param name="scopeId">Scope id covered by the grant.</param>
    /// <param name="scopeKind">Scope kind covered by the grant.</param>
    /// <param name="capabilities">Capabilities granted to the principal in the scope.</param>
    /// <param name="isDefaultScope">Whether this grant marks the scope as the principal's default for the scope kind.</param>
    /// <param name="membershipId">Optional explicit membership id.</param>
    /// <returns>The current builder.</returns>
    public InMemoryIdentityDirectoryBuilder AddScopeGrant(
        string principalId,
        string scopeId,
        string scopeKind,
        IEnumerable<string> capabilities,
        bool isDefaultScope = false,
        string? membershipId = null
        )
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        RequireNonWhiteSpace(principalId, nameof(principalId));
        RequireNonWhiteSpace(scopeId, nameof(scopeId));
        RequireNonWhiteSpace(scopeKind, nameof(scopeKind));

        return AddMembership(new(
            Id: membershipId ?? $"{principalId}:{scopeKind}:{scopeId}",
            PrincipalId: principalId,
            ScopeId: scopeId,
            ScopeKind: scopeKind,
            Capabilities: [..capabilities],
            IsDefaultScope: isDefaultScope
            ));
    }

    /// <summary>
    /// Returns whether the directory contains the specified scope.
    /// </summary>
    /// <param name="scopeKind">Scope kind to test.</param>
    /// <param name="scopeId">Scope id to test.</param>
    /// <returns><see langword="true"/> when the scope has been added.</returns>
    public bool ContainsScope(string scopeKind, string scopeId) =>
        scopeKeys.Contains(CreateScopeKey(scopeKind, scopeId));

    /// <summary>
    /// Returns whether the directory contains the specified principal.
    /// </summary>
    /// <param name="principalId">Principal id to test.</param>
    /// <returns><see langword="true"/> when the principal has been added.</returns>
    public bool ContainsPrincipal(string principalId) =>
        principalIds.Contains(principalId);

    /// <summary>
    /// Builds the immutable in-memory identity directory.
    /// </summary>
    /// <returns>An immutable in-memory identity directory.</returns>
    public InMemoryIdentityDirectory Build() => new(
        [..scopes],
        [..principals],
        [..memberships]
        );

    static (string Kind, string Id) CreateScopeKey(string scopeKind, string scopeId) =>
        (scopeKind, scopeId);

    static void RequireNonWhiteSpace(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value must not be blank.", parameterName);
    }
}
