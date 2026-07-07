using System.Collections.Immutable;

namespace Cohesive.Identity;

/// <summary>
/// Effective-scope helpers for normalized identity contexts.
/// </summary>
public static class IdentityContextScopeExtensions
{
    extension(IdentityContext identity)
    {
        /// <summary>
        /// Returns effective scopes of the requested kind selected for this operation.
        /// </summary>
        public ImmutableArray<ScopeRef> GetEffectiveScopes(string scopeKind)
        {
            ArgumentNullException.ThrowIfNull(identity);
            scopeKind = Guard.RequireNotNullOrWhiteSpace(scopeKind);
            var scopes = identity.EffectiveScope?.Scopes;
            if (scopes is null || scopes.Value.IsDefaultOrEmpty)
                return [];

            var builder = ImmutableArray.CreateBuilder<ScopeRef>();
            HashSet<string>? seenScopeIds = null;
            foreach (var scope in scopes.Value)
            {
                if (!string.Equals(scope.Kind, scopeKind, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(scope.Id))
                {
                    continue;
                }

                seenScopeIds ??= new(StringComparer.Ordinal);
                if (seenScopeIds.Add(scope.Id))
                    builder.Add(scope);
            }

            return builder.ToImmutable();
        }

        /// <summary>
        /// Returns effective scope ids of the requested kind selected for this operation.
        /// </summary>
        public ImmutableArray<string> GetEffectiveScopeIds(string scopeKind) =>
        [
            ..identity.GetEffectiveScopes(scopeKind).Select(static scope => scope.Id)
        ];

        /// <summary>
        /// Returns effective scopes of the requested kind with physical placement metadata.
        /// </summary>
        public ImmutableArray<ResolvedScope> GetEffectiveResolvedScopes(string scopeKind) =>
            IdentityScopeResolver.ToResolvedScopes(identity.GetEffectiveScopes(scopeKind));

        /// <summary>
        /// Returns grants over scopes of the requested kind.
        /// </summary>
        public ImmutableArray<IdentityScopeGrant> GetScopeGrants(string scopeKind)
        {
            ArgumentNullException.ThrowIfNull(identity);
            scopeKind = Guard.RequireNotNullOrWhiteSpace(scopeKind);
            if (identity.Grants.IsDefaultOrEmpty)
                return [];

            return [
                ..identity.Grants
                    .Where(grant => string.Equals(grant.Scope.Kind, scopeKind, StringComparison.Ordinal))
            ];
        }

        /// <summary>
        /// Returns distinct granted scopes of the requested kind.
        /// </summary>
        public ImmutableArray<ScopeRef> GetGrantedScopes(string scopeKind)
        {
            var grants = identity.GetScopeGrants(scopeKind);
            if (grants.IsDefaultOrEmpty)
                return [];

            var builder = ImmutableArray.CreateBuilder<ScopeRef>();
            HashSet<string>? seenScopeIds = null;
            foreach (var grant in grants)
            {
                var scope = grant.Scope;
                if (string.IsNullOrWhiteSpace(scope.Id))
                    continue;

                seenScopeIds ??= new(StringComparer.Ordinal);
                if (seenScopeIds.Add(scope.Id))
                    builder.Add(scope);
            }

            return builder.ToImmutable();
        }

        /// <summary>
        /// Returns scopes accessible to this identity for the requested kind, preferring grants and falling back to the effective scope when no grants exist.
        /// </summary>
        public ImmutableArray<ScopeRef> ResolveAccessibleScopes(string scopeKind) =>
            IdentityScopeResolver.ResolveAccessibleScopes(identity.Grants, identity.GetEffectiveScopes(scopeKind), scopeKind);

        /// <summary>
        /// Returns accessible scope ids for the requested kind.
        /// </summary>
        public ImmutableArray<string> ResolveAccessibleScopeIds(string scopeKind) =>
        [
            ..identity.ResolveAccessibleScopes(scopeKind).Select(static scope => scope.Id)
        ];

        /// <summary>
        /// Returns accessible scopes of the requested kind with physical placement metadata.
        /// </summary>
        public ImmutableArray<ResolvedScope> ResolveAccessibleResolvedScopes(string scopeKind) =>
            IdentityScopeResolver.ToResolvedScopes(identity.ResolveAccessibleScopes(scopeKind));

        /// <summary>
        /// Resolves requested scope ids against the identity's accessible scopes.
        /// </summary>
        public RequestedScopeResolution ResolveRequestedScopes(string scopeKind, IReadOnlyList<string>? requestedScopeIds) =>
            IdentityScopeResolver.ResolveRequestedScopes(identity.ResolveAccessibleScopes(scopeKind), requestedScopeIds);

        /// <summary>
        /// Resolves requested scope ids against the identity's accessible scopes and includes physical placement metadata.
        /// </summary>
        public RequestedResolvedScopeResolution ResolveRequestedResolvedScopes(string scopeKind, IReadOnlyList<string>? requestedScopeIds) =>
            IdentityScopeResolver.ResolveRequestedResolvedScopes(identity.ResolveAccessibleScopes(scopeKind), requestedScopeIds);

        /// <summary>
        /// Resolves one requested scope id against the identity's accessible scopes and includes physical placement metadata.
        /// </summary>
        public ResolvedScope ResolveScope(string scopeKind, string scopeId) =>
            IdentityScopeResolver.ResolveScope(identity.ResolveAccessibleScopes(scopeKind), scopeKind, scopeId);

        /// <summary>
        /// Resolves requested scope ids to accessible scope ids.
        /// </summary>
        public ImmutableArray<string> ResolveRequestedScopeIds(string scopeKind, IReadOnlyList<string>? requestedScopeIds) =>
        [
            ..identity.ResolveRequestedScopes(scopeKind, requestedScopeIds).Scopes.Select(static scope => scope.Id)
        ];

        /// <summary>
        /// Returns distinct granted scope ids of the requested kind.
        /// </summary>
        public ImmutableArray<string> GetGrantedScopeIds(string scopeKind) =>
        [
            ..identity.GetGrantedScopes(scopeKind).Select(static scope => scope.Id)
        ];

        /// <summary>
        /// Attempts to read exactly one effective scope of the requested kind.
        /// </summary>
        public bool TryGetSingleEffectiveScope(string scopeKind, out ScopeRef? scope)
        {
            var scopes = identity.GetEffectiveScopes(scopeKind);
            if (scopes.Length == 1)
            {
                scope = scopes[0];
                return true;
            }

            scope = null;
            return false;
        }

        /// <summary>
        /// Returns the single effective scope id of the requested kind, or <see langword="null"/>.
        /// </summary>
        public string? TryGetSingleEffectiveScopeId(string scopeKind) =>
            identity.TryGetSingleEffectiveScope(scopeKind, out var scope)
                ? scope?.Id
                : null;

        /// <summary>
        /// Attempts to read exactly one effective scope of the requested kind with physical placement metadata.
        /// </summary>
        public bool TryResolveSingleScope(string scopeKind, out ResolvedScope? scope)
        {
            if (identity.TryGetSingleEffectiveScope(scopeKind, out var scopeRef) && scopeRef is not null)
            {
                scope = ResolvedScope.FromScopeRef(scopeRef);
                return true;
            }

            scope = null;
            return false;
        }

        /// <summary>
        /// Reads exactly one effective scope of the requested kind with physical placement metadata.
        /// </summary>
        public ResolvedScope RequireSingleScope(string scopeKind) =>
            identity.TryResolveSingleScope(scopeKind, out var scope) && scope is not null
                ? scope
                : throw new InvalidOperationException(
                    $"The operation requires exactly one selected '{Guard.RequireNotNullOrWhiteSpace(scopeKind).Trim()}' scope.");
    }
}
