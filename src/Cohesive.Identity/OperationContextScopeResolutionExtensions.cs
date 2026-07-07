namespace Cohesive.Identity;

/// <summary>
/// Declares how an operation resolves one semantic scope kind from its normalized identity context.
/// </summary>
public sealed record OperationScopeResolutionPolicy
{
    /// <summary>
    /// Creates a scope resolution policy.
    /// </summary>
    /// <param name="scopeKind">Scope kind to resolve, such as a tenant, workspace, or organization kind.</param>
    /// <param name="scopeName">Human-facing singular scope name used in diagnostics.</param>
    /// <param name="allowAmbientScopeFallback">
    /// Allows trusted contexts without an identity context to resolve an explicitly supplied scope id as an ambient scope.
    /// </param>
    public OperationScopeResolutionPolicy(
        string scopeKind,
        string scopeName,
        bool allowAmbientScopeFallback = false
        )
    {
        ScopeKind = Guard.RequireNotNullOrWhiteSpace(scopeKind).Trim();
        ScopeName = Guard.RequireNotNullOrWhiteSpace(scopeName).Trim();
        AllowAmbientScopeFallback = allowAmbientScopeFallback;
    }

    /// <summary>
    /// Scope kind to resolve, such as a tenant, workspace, or organization kind.
    /// </summary>
    public string ScopeKind { get; }

    /// <summary>
    /// Human-facing singular scope name used in diagnostics.
    /// </summary>
    public string ScopeName { get; }

    /// <summary>
    /// Allows trusted contexts without an identity context to resolve an explicitly supplied scope id as an ambient scope.
    /// </summary>
    public bool AllowAmbientScopeFallback { get; }
}

/// <summary>
/// Operation-context helpers for resolving a declared scope kind with consistent cardinality and access semantics.
/// </summary>
public static class OperationContextScopeResolutionExtensions
{
    extension(OperationContext context)
    {
        /// <summary>
        /// Returns all selected scopes for the policy's scope kind.
        /// </summary>
        public IReadOnlyList<ResolvedScope> ResolveSelectedScopes(OperationScopeResolutionPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            var scopes = context.GetEffectiveResolvedScopes(policy.ScopeKind);
            return scopes.IsDefaultOrEmpty
                ? throw new InvalidOperationException($"The operation requires a selected {policy.ScopeName}.")
                : scopes;
        }

        /// <summary>
        /// Returns all accessible scopes for the policy's scope kind.
        /// </summary>
        public IReadOnlyList<ResolvedScope> ResolveAccessibleScopes(OperationScopeResolutionPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            return context.ResolveAccessibleResolvedScopes(policy.ScopeKind);
        }

        /// <summary>
        /// Resolves requested scope ids against the current identity's accessible scopes.
        /// </summary>
        public IReadOnlyList<ResolvedScope> ResolveRequestedScopes(
            OperationScopeResolutionPolicy policy,
            IReadOnlyList<string>? requestedScopeIds
            )
        {
            ArgumentNullException.ThrowIfNull(policy);
            var resolution = context.ResolveRequestedResolvedScopes(policy.ScopeKind, requestedScopeIds);
            return resolution.RejectedScopeIds.IsDefaultOrEmpty
                ? resolution.Scopes
                : throw new InvalidOperationException($"The requested {policy.ScopeName} is not accessible to the current principal.");
        }

        /// <summary>
        /// Resolves one explicit scope id to its semantic scope and physical placement.
        /// </summary>
        public ResolvedScope ResolveScope(
            OperationScopeResolutionPolicy policy,
            string scopeId
            )
        {
            ArgumentNullException.ThrowIfNull(policy);
            scopeId = Guard.RequireNotNullOrWhiteSpace(scopeId).Trim();
            if (policy.AllowAmbientScopeFallback
                && (!context.TryGetIdentityContext(out var identity) || identity is null))
            {
                return CreateAmbientScope(policy, scopeId);
            }

            try
            {
                return context.ResolveScope(policy.ScopeKind, scopeId);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"The requested {policy.ScopeName} is not accessible to the current principal.",
                    ex);
            }
        }

        /// <summary>
        /// Requires exactly one selected scope for the policy's scope kind.
        /// </summary>
        public ResolvedScope RequireSingleScope(OperationScopeResolutionPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            try
            {
                return context.RequireSingleScope(policy.ScopeKind);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"The operation requires exactly one selected {policy.ScopeName}.",
                    ex);
            }
        }

        /// <summary>
        /// Attempts to resolve exactly one selected scope for the policy's scope kind.
        /// </summary>
        public bool TryResolveSingleScope(OperationScopeResolutionPolicy policy, out ResolvedScope? scope)
        {
            ArgumentNullException.ThrowIfNull(policy);
            return context.TryResolveSingleScope(policy.ScopeKind, out scope);
        }
    }

    static ResolvedScope CreateAmbientScope(OperationScopeResolutionPolicy policy, string scopeId) => new(
        Id: scopeId,
        Kind: policy.ScopeKind,
        PartitionKey: scopeId,
        Scope: new(
            Id: scopeId,
            Kind: policy.ScopeKind
            ));
}
