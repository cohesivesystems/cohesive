using System.Collections.Immutable;

namespace Cohesive.Identity;

/// <summary>
/// Scope resolved with its physical placement interpretation.
/// </summary>
/// <param name="Id">Stable semantic scope identifier.</param>
/// <param name="Kind">Scope kind, for example <c>sample.tenant</c>.</param>
/// <param name="PartitionKey">Physical partition key associated with this scope.</param>
/// <param name="Scope">Underlying normalized identity scope.</param>
public sealed record ResolvedScope(
    string Id,
    string Kind,
    string PartitionKey,
    ScopeRef Scope
    )
{
    /// <summary>
    /// Creates a resolved scope from a normalized identity scope.
    /// </summary>
    public static ResolvedScope FromScopeRef(ScopeRef scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var id = Guard.RequireNotNullOrWhiteSpace(scope.Id).Trim();
        var kind = Guard.RequireNotNullOrWhiteSpace(scope.Kind).Trim();
        return new(
            Id: id,
            Kind: kind,
            PartitionKey: scope.ResolvePartitionKey(),
            Scope: scope
            );
    }
}

/// <summary>
/// Result of resolving requested scope ids against an accessible scope set, including placement metadata.
/// </summary>
/// <param name="Scopes">Resolved scopes that matched the request, or all accessible scopes when no explicit request was supplied.</param>
/// <param name="RejectedScopeIds">Requested scope ids that were not accessible.</param>
public sealed record RequestedResolvedScopeResolution(
    ImmutableArray<ResolvedScope> Scopes,
    ImmutableArray<string> RejectedScopeIds
    );
