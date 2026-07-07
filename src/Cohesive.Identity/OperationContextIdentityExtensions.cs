using System.Collections.Immutable;

namespace Cohesive.Identity;

/// <summary>
/// <see cref="OperationContext"/> integration for normalized identity state.
/// </summary>
public static class OperationContextIdentityExtensions
{
    static readonly PrincipalRef AmbientPrincipal = new(
        Id: "ambient",
        Kind: PrincipalKind.SystemAccount,
        DisplayName: "Ambient operation"
        );

    /// <summary>
    /// Operation-context item key used for the normalized identity context.
    /// </summary>
    public const string IdentityContextItemKey = "cohesive.identity.context";

    extension(OperationContext context)
    {
        /// <summary>
        /// Returns a copy of the context with the normalized identity context attached.
        /// </summary>
        public OperationContext WithIdentityContext(IdentityContext identity) =>
            context.WithItem(IdentityContextItemKey, Guard.RequireNotNull(identity));

        /// <summary>
        /// Returns a copy of the context with one effective scope selected for this operation.
        /// </summary>
        /// <param name="scopeKind">Scope kind, such as a tenant, workspace, or organization kind.</param>
        /// <param name="scopeId">Stable identifier of the selected scope.</param>
        /// <param name="source">Source that supplied the scope selection.</param>
        /// <param name="displayName">Optional human-facing scope name.</param>
        /// <param name="partitionKey">Optional physical partition key interpretation for the scope.</param>
        /// <param name="parentScopeId">Optional parent scope for hierarchical authorization models.</param>
        public OperationContext WithSingleEffectiveScope(
            string scopeKind,
            string scopeId,
            ScopeSelectionSource source = ScopeSelectionSource.Ambient,
            string? displayName = null,
            string? partitionKey = null,
            string? parentScopeId = null
            )
        {
            var scope = new ScopeRef(
                Id: Guard.RequireNotNullOrWhiteSpace(scopeId),
                Kind: Guard.RequireNotNullOrWhiteSpace(scopeKind),
                DisplayName: displayName,
                PartitionKey: partitionKey,
                ParentScopeId: parentScopeId
                );
            var effectiveScope = new EffectiveScope(
                Scopes: [scope],
                Mode: ScopeSelectionMode.Single,
                Source: source
                );
            var currentIdentity = context.GetIdentityContextOrDefault();
            var identity = currentIdentity is null
                ? new(
                    Actor: ResolveAmbientPrincipal(context),
                    EffectiveScope: effectiveScope
                    )
                : currentIdentity with { EffectiveScope = effectiveScope };
            return context.WithIdentityContext(identity);
        }

        /// <summary>
        /// Attempts to read the normalized identity context.
        /// </summary>
        public bool TryGetIdentityContext(out IdentityContext? identity) =>
            context.TryGetItem(IdentityContextItemKey, out identity);

        /// <summary>
        /// Reads the normalized identity context or returns <see langword="null"/>.
        /// </summary>
        public IdentityContext? GetIdentityContextOrDefault() =>
            context.TryGetIdentityContext(out var identity) ? identity : null;

        /// <summary>
        /// Returns effective scopes of the requested kind selected for this operation.
        /// </summary>
        public ImmutableArray<ScopeRef> GetEffectiveScopes(string scopeKind) =>
            context.TryGetIdentityContext(out var identity) && identity is not null
                ? identity.GetEffectiveScopes(scopeKind)
                : [];

        /// <summary>
        /// Returns effective scope ids of the requested kind selected for this operation.
        /// </summary>
        public ImmutableArray<string> GetEffectiveScopeIds(string scopeKind) =>
            context.TryGetIdentityContext(out var identity) && identity is not null
                ? identity.GetEffectiveScopeIds(scopeKind)
                : [];

        /// <summary>
        /// Returns effective scopes of the requested kind with physical placement metadata.
        /// </summary>
        public ImmutableArray<ResolvedScope> GetEffectiveResolvedScopes(string scopeKind) =>
            context.TryGetIdentityContext(out var identity) && identity is not null
                ? identity.GetEffectiveResolvedScopes(scopeKind)
                : [];

        /// <summary>
        /// Returns granted scopes of the requested kind visible to this operation.
        /// </summary>
        public ImmutableArray<ScopeRef> GetGrantedScopes(string scopeKind) =>
            context.TryGetIdentityContext(out var identity) && identity is not null
                ? identity.GetGrantedScopes(scopeKind)
                : [];

        /// <summary>
        /// Returns granted scope ids of the requested kind visible to this operation.
        /// </summary>
        public ImmutableArray<string> GetGrantedScopeIds(string scopeKind) =>
            context.TryGetIdentityContext(out var identity) && identity is not null
                ? identity.GetGrantedScopeIds(scopeKind)
                : [];

        /// <summary>
        /// Returns accessible scopes of the requested kind, preferring grants and falling back to effective scopes when no grants exist.
        /// </summary>
        public ImmutableArray<ScopeRef> ResolveAccessibleScopes(string scopeKind) =>
            context.TryGetIdentityContext(out var identity) && identity is not null
                ? identity.ResolveAccessibleScopes(scopeKind)
                : [];

        /// <summary>
        /// Returns accessible scope ids of the requested kind.
        /// </summary>
        public ImmutableArray<string> ResolveAccessibleScopeIds(string scopeKind) =>
            context.TryGetIdentityContext(out var identity) && identity is not null
                ? identity.ResolveAccessibleScopeIds(scopeKind)
                : [];

        /// <summary>
        /// Returns accessible scopes of the requested kind with physical placement metadata.
        /// </summary>
        public ImmutableArray<ResolvedScope> ResolveAccessibleResolvedScopes(string scopeKind) =>
            context.TryGetIdentityContext(out var identity) && identity is not null
                ? identity.ResolveAccessibleResolvedScopes(scopeKind)
                : [];

        /// <summary>
        /// Resolves requested scope ids against the current identity's accessible scopes.
        /// </summary>
        public RequestedScopeResolution ResolveRequestedScopes(string scopeKind, IReadOnlyList<string>? requestedScopeIds) =>
            context.TryGetIdentityContext(out var identity) && identity is not null
                ? identity.ResolveRequestedScopes(scopeKind, requestedScopeIds)
                : IdentityScopeResolver.ResolveRequestedScopes([], requestedScopeIds);

        /// <summary>
        /// Resolves requested scope ids against the current identity's accessible scopes and includes physical placement metadata.
        /// </summary>
        public RequestedResolvedScopeResolution ResolveRequestedResolvedScopes(string scopeKind, IReadOnlyList<string>? requestedScopeIds) =>
            context.TryGetIdentityContext(out var identity) && identity is not null
                ? identity.ResolveRequestedResolvedScopes(scopeKind, requestedScopeIds)
                : IdentityScopeResolver.ResolveRequestedResolvedScopes([], requestedScopeIds);

        /// <summary>
        /// Resolves one requested scope id against the current identity's accessible scopes and includes physical placement metadata.
        /// </summary>
        /// <exception cref="InvalidOperationException">The scope is not accessible or cannot satisfy the requested kind.</exception>
        public ResolvedScope ResolveScope(string scopeKind, string scopeId) =>
            context.TryGetIdentityContext(out var identity) && identity is not null
                ? identity.ResolveScope(scopeKind, scopeId)
                : IdentityScopeResolver.ResolveScope([], scopeKind, scopeId);

        /// <summary>
        /// Resolves requested scope ids to accessible scope ids.
        /// </summary>
        public ImmutableArray<string> ResolveRequestedScopeIds(string scopeKind, IReadOnlyList<string>? requestedScopeIds) =>
            context.TryGetIdentityContext(out var identity) && identity is not null
                ? identity.ResolveRequestedScopeIds(scopeKind, requestedScopeIds)
                : [];

        /// <summary>
        /// Attempts to read exactly one effective scope of the requested kind.
        /// </summary>
        public bool TryGetSingleEffectiveScope(string scopeKind, out ScopeRef? scope)
        {
            if (context.TryGetIdentityContext(out var identity) && identity is not null)
                return identity.TryGetSingleEffectiveScope(scopeKind, out scope);

            scope = null;
            return false;
        }

        /// <summary>
        /// Returns the single effective scope id of the requested kind, or <see langword="null"/>.
        /// </summary>
        public string? TryGetSingleEffectiveScopeId(string scopeKind) =>
            context.TryGetSingleEffectiveScope(scopeKind, out var scope)
                ? scope?.Id
                : null;

        /// <summary>
        /// Attempts to read exactly one effective scope of the requested kind with physical placement metadata.
        /// </summary>
        public bool TryResolveSingleScope(string scopeKind, out ResolvedScope? scope)
        {
            if (context.TryGetIdentityContext(out var identity) && identity is not null)
                return identity.TryResolveSingleScope(scopeKind, out scope);

            scope = null;
            return false;
        }

        /// <summary>
        /// Reads exactly one effective scope of the requested kind with physical placement metadata.
        /// </summary>
        public ResolvedScope RequireSingleScope(string scopeKind)
        {
            if (context.TryGetIdentityContext(out var identity) && identity is not null)
                return identity.RequireSingleScope(scopeKind);

            throw new InvalidOperationException(
                $"The operation requires exactly one selected '{Guard.RequireNotNullOrWhiteSpace(scopeKind).Trim()}' scope.");
        }
    }

    static PrincipalRef ResolveAmbientPrincipal(OperationContext context)
    {
        var subject = context.Principal.GetSubject();
        if (!string.IsNullOrWhiteSpace(subject))
        {
            return new(
                Id: subject,
                Kind: PrincipalKind.User,
                DisplayName: context.Principal.GetDisplayName()
                );
        }

        var email = context.Principal.GetEmail();
        if (!string.IsNullOrWhiteSpace(email))
        {
            return new(
                Id: email,
                Kind: PrincipalKind.User,
                DisplayName: context.Principal.GetDisplayName()
                );
        }

        var clientId = context.Principal.GetClientId();
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            return new(
                Id: clientId,
                Kind: PrincipalKind.ServiceAccount,
                DisplayName: context.Principal.GetDisplayName()
                );
        }

        return AmbientPrincipal;
    }
}
