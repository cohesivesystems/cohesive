using System.Collections.Immutable;
using System.Security.Claims;

namespace Cohesive.Identity;

/// <summary>
/// Resolves normalized operation identity for a request.
/// </summary>
public interface IIdentityContextResolver
{
    /// <summary>
    /// Resolves identity and effective scope for a request.
    /// </summary>
    ValueTask<IdentityContext> ResolveAsync(IdentityContextResolutionRequest request, CancellationToken ct = default);
}

/// <summary>
/// Scope selection requested by a transport adapter.
/// </summary>
/// <param name="ScopeKind">Requested scope kind.</param>
/// <param name="ScopeIds">Requested scope identifiers.</param>
/// <param name="Mode">Requested selection mode.</param>
/// <param name="Source">Request source.</param>
public sealed record RequestedScopeSelection(
    string ScopeKind,
    ImmutableArray<string> ScopeIds,
    ScopeSelectionMode Mode,
    ScopeSelectionSource Source
);

/// <summary>
/// Identity resolution request created at an adapter boundary.
/// </summary>
/// <param name="Principal">Transport principal from the host runtime.</param>
/// <param name="RequestedScope">Optional requested scope selection.</param>
public sealed record IdentityContextResolutionRequest(
    ClaimsPrincipal Principal,
    RequestedScopeSelection? RequestedScope = null
);