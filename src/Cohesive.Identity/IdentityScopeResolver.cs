using System.Collections.Immutable;

namespace Cohesive.Identity;

/// <summary>
/// Result of resolving requested scope ids against an accessible scope set.
/// </summary>
/// <param name="Scopes">Resolved scopes that matched the request, or all accessible scopes when no explicit request was supplied.</param>
/// <param name="RejectedScopeIds">Requested scope ids that were not accessible.</param>
public sealed record RequestedScopeResolution(
    ImmutableArray<ScopeRef> Scopes,
    ImmutableArray<string> RejectedScopeIds
    );

/// <summary>
/// Shared scope-resolution helpers for normalized identity contexts and grants.
/// </summary>
public static class IdentityScopeResolver
{
    /// <summary>
    /// Resolves accessible scopes for one scope kind, preferring explicit grants and falling back to effective scopes when no grants exist.
    /// </summary>
    public static ImmutableArray<ScopeRef> ResolveAccessibleScopes(
        IReadOnlyList<IdentityScopeGrant> grants,
        IReadOnlyList<ScopeRef>? fallbackScopes,
        string scopeKind
        )
    {
        ArgumentNullException.ThrowIfNull(grants);
        scopeKind = Guard.RequireNotNullOrWhiteSpace(scopeKind);
        var normalizedFallbackScopes = NormalizeScopeList(fallbackScopes);

        ImmutableArray<ScopeRef> grantedScopes = [];
        if (grants is not ImmutableArray<IdentityScopeGrant> immutableGrants || !immutableGrants.IsDefault)
        {
            grantedScopes = DistinctScopes(
                scopes: grants
                    .Where(grant => string.Equals(grant.Scope.Kind, scopeKind, StringComparison.Ordinal))
                    .Select(static grant => grant.Scope),
                scopeKind: scopeKind);
        }
        if (!grantedScopes.IsDefaultOrEmpty)
            return grantedScopes;

        return DistinctScopes(normalizedFallbackScopes, scopeKind);
    }

    /// <summary>
    /// Resolves requested scope ids against an accessible scope set.
    /// </summary>
    public static RequestedScopeResolution ResolveRequestedScopes(
        IReadOnlyList<ScopeRef> accessibleScopes,
        IReadOnlyList<string>? requestedScopeIds
        )
    {
        ArgumentNullException.ThrowIfNull(accessibleScopes);
        var normalizedAccessibleScopes = NormalizeScopeList(accessibleScopes);
        var distinctAccessibleScopes = DistinctScopes(
            normalizedAccessibleScopes,
            scopeKind: normalizedAccessibleScopes.FirstOrDefault(static scope => !string.IsNullOrWhiteSpace(scope.Kind))?.Kind);
        if (requestedScopeIds is null || requestedScopeIds.Count == 0)
            return new(distinctAccessibleScopes, []);

        var accessibleById = distinctAccessibleScopes.ToDictionary(static scope => scope.Id, StringComparer.Ordinal);
        var selected = ImmutableArray.CreateBuilder<ScopeRef>(requestedScopeIds.Count);
        var rejected = ImmutableArray.CreateBuilder<string>();
        HashSet<string>? seenRequestedScopeIds = null;
        var hasExplicitRequest = false;
        foreach (var requestedScopeId in requestedScopeIds)
        {
            if (string.IsNullOrWhiteSpace(requestedScopeId))
                continue;

            var trimmedScopeId = requestedScopeId.Trim();
            hasExplicitRequest = true;
            seenRequestedScopeIds ??= new(StringComparer.Ordinal);
            if (!seenRequestedScopeIds.Add(trimmedScopeId))
                continue;

            if (accessibleById.TryGetValue(trimmedScopeId, out var scope))
                selected.Add(scope);
            else
                rejected.Add(trimmedScopeId);
        }

        return !hasExplicitRequest
            ? new(distinctAccessibleScopes, [])
            : new(selected.ToImmutable(), rejected.ToImmutable());
    }

    /// <summary>
    /// Resolves requested scope ids against an accessible scope set and includes physical placement metadata.
    /// </summary>
    public static RequestedResolvedScopeResolution ResolveRequestedResolvedScopes(
        IReadOnlyList<ScopeRef> accessibleScopes,
        IReadOnlyList<string>? requestedScopeIds
        )
    {
        var resolution = ResolveRequestedScopes(accessibleScopes, requestedScopeIds);
        return new(
            Scopes: ToResolvedScopes(resolution.Scopes),
            RejectedScopeIds: resolution.RejectedScopeIds
            );
    }

    /// <summary>
    /// Resolves one requested scope id against an accessible scope set and includes physical placement metadata.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scope is not accessible or cannot satisfy the requested kind.</exception>
    public static ResolvedScope ResolveScope(
        IReadOnlyList<ScopeRef> accessibleScopes,
        string scopeKind,
        string scopeId
        )
    {
        scopeKind = Guard.RequireNotNullOrWhiteSpace(scopeKind).Trim();
        scopeId = Guard.RequireNotNullOrWhiteSpace(scopeId).Trim();
        var resolution = ResolveRequestedScopes(accessibleScopes, [scopeId]);
        if (!resolution.RejectedScopeIds.IsDefaultOrEmpty || resolution.Scopes.Length != 1)
            throw new InvalidOperationException($"Scope '{scopeId}' of kind '{scopeKind}' is not accessible.");

        var scope = resolution.Scopes[0];
        if (!string.Equals(scope.Kind, scopeKind, StringComparison.Ordinal))
            throw new InvalidOperationException($"Scope '{scopeId}' of kind '{scope.Kind}' cannot satisfy requested kind '{scopeKind}'.");

        return ResolvedScope.FromScopeRef(scope);
    }

    /// <summary>
    /// Converts normalized scope references into resolved scopes with physical placement metadata.
    /// </summary>
    public static ImmutableArray<ResolvedScope> ToResolvedScopes(IReadOnlyList<ScopeRef> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Count == 0)
            return [];

        var builder = ImmutableArray.CreateBuilder<ResolvedScope>(scopes.Count);
        foreach (var scope in scopes)
        {
            if (scope is not null
                && !string.IsNullOrWhiteSpace(scope.Id)
                && !string.IsNullOrWhiteSpace(scope.Kind))
            {
                builder.Add(ResolvedScope.FromScopeRef(scope));
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Resolves the default scope from an accessible scope set.
    /// </summary>
    public static ScopeRef? ResolveDefaultScope(
        IReadOnlyList<ScopeRef> accessibleScopes,
        string? defaultScopeId = null
        )
    {
        ArgumentNullException.ThrowIfNull(accessibleScopes);
        var normalizedAccessibleScopes = NormalizeScopeList(accessibleScopes);
        if (normalizedAccessibleScopes.Count == 0)
            return null;

        var distinctAccessibleScopes = DistinctScopes(
            normalizedAccessibleScopes,
            scopeKind: normalizedAccessibleScopes.FirstOrDefault(static scope => !string.IsNullOrWhiteSpace(scope.Kind))?.Kind);
        if (distinctAccessibleScopes.IsDefaultOrEmpty)
            return null;

        if (!string.IsNullOrWhiteSpace(defaultScopeId))
        {
            var matchingScope = distinctAccessibleScopes.FirstOrDefault(scope => string.Equals(scope.Id, defaultScopeId.Trim(), StringComparison.Ordinal));
            if (matchingScope is not null)
                return matchingScope;
        }

        return distinctAccessibleScopes[0];
    }

    static ImmutableArray<ScopeRef> DistinctScopes(IEnumerable<ScopeRef> scopes, string? scopeKind)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var builder = ImmutableArray.CreateBuilder<ScopeRef>();
        HashSet<string>? seenScopeIds = null;
        foreach (var scope in scopes)
        {
            if (scope is null
                || string.IsNullOrWhiteSpace(scope.Id)
                || !string.IsNullOrWhiteSpace(scopeKind) && !string.Equals(scope.Kind, scopeKind, StringComparison.Ordinal))
            {
                continue;
            }

            seenScopeIds ??= new(StringComparer.Ordinal);
            if (seenScopeIds.Add(scope.Id))
                builder.Add(scope);
        }

        return builder.ToImmutable();
    }

    static IReadOnlyList<ScopeRef> NormalizeScopeList(IReadOnlyList<ScopeRef>? scopes) =>
        scopes switch
        {
            null => [],
            ImmutableArray<ScopeRef> immutableScopes when immutableScopes.IsDefault => [],
            _ => scopes
        };
}
