using System.Collections.Immutable;
using Cohesive.Identity;

namespace Cohesive.Tests.Identity;

public sealed class OperationContextScopeResolutionTests
{
    static readonly PrincipalRef Actor = new("user-001", PrincipalKind.User, "Test User");
    static readonly OperationScopeResolutionPolicy TenantPolicy = new(
        scopeKind: "tenant",
        scopeName: "tenant",
        allowAmbientScopeFallback: true
        );
    static readonly OperationScopeResolutionPolicy WorkspacePolicy = new(
        scopeKind: "workspace",
        scopeName: "workspace"
        );

    [Fact]
    public void ResolveSelectedScopes_ThrowsWithScopeNameWhenSelectionIsMissing()
    {
        var context = CreateContext(effectiveScopes: []);

        var error = Assert.Throws<InvalidOperationException>(() => context.ResolveSelectedScopes(TenantPolicy));

        Assert.Equal("The operation requires a selected tenant.", error.Message);
    }

    [Fact]
    public void RequireSingleScope_PreservesSelectedScopePartitionKey()
    {
        var context = CreateContext(
            effectiveScopes:
            [
                Scope("tenant-a", partitionKey: "partition-a")
            ]);

        var scope = context.RequireSingleScope(TenantPolicy);

        Assert.Equal("tenant-a", scope.Id);
        Assert.Equal("partition-a", scope.PartitionKey);
    }

    [Fact]
    public void ResolveRequestedScopes_RejectsInaccessibleScopeWithScopeName()
    {
        var context = CreateContext(
            effectiveScopes: [],
            grants:
            [
                Grant("tenant-a")
            ]);

        var error = Assert.Throws<InvalidOperationException>(() => context.ResolveRequestedScopes(TenantPolicy, ["tenant-b"]));

        Assert.Equal("The requested tenant is not accessible to the current principal.", error.Message);
    }

    [Fact]
    public void ResolveRequestedScopes_PreservesAccessibleScopePartitionKeys()
    {
        var context = CreateContext(
            effectiveScopes: [],
            grants:
            [
                Grant("tenant-a", partitionKey: "partition-a"),
                Grant("tenant-b", partitionKey: "partition-b")
            ]);

        var scopes = context.ResolveRequestedScopes(TenantPolicy, ["tenant-b"]);
        var scope = Assert.Single(scopes);

        Assert.Equal("tenant-b", scope.Id);
        Assert.Equal("partition-b", scope.PartitionKey);
    }

    [Fact]
    public void ResolveScope_UsesAmbientScopeFallbackWhenAllowed()
    {
        var scope = OperationContext.Create().ResolveScope(TenantPolicy, "tenant-a");

        Assert.Equal("tenant-a", scope.Id);
        Assert.Equal("tenant", scope.Kind);
        Assert.Equal("tenant-a", scope.PartitionKey);
        Assert.Equal("tenant-a", scope.Scope.Id);
    }

    [Fact]
    public void ResolveScope_RejectsAmbientScopeFallbackWhenNotAllowed()
    {
        var error = Assert.Throws<InvalidOperationException>(() => OperationContext.Create().ResolveScope(WorkspacePolicy, "workspace-a"));

        Assert.Equal("The requested workspace is not accessible to the current principal.", error.Message);
    }

    static OperationContext CreateContext(
        IReadOnlyList<ScopeRef> effectiveScopes,
        IReadOnlyList<IdentityScopeGrant>? grants = null
        )
    {
        var effective = new EffectiveScope(
            Scopes: [..effectiveScopes],
            Mode: effectiveScopes.Count == 1 ? ScopeSelectionMode.Single : ScopeSelectionMode.Multiple,
            Source: ScopeSelectionSource.Ambient
            );
        var identity = new IdentityContext(
            Actor: Actor,
            EffectiveScope: effectiveScopes.Count == 0 ? null : effective,
            Grants: grants is null || grants.Count == 0 ? default : grants.ToImmutableArray()
            );
        return OperationContext.Create().WithIdentityContext(identity);
    }

    static ScopeRef Scope(string id, string? partitionKey = null) => new(
        Id: id,
        Kind: "tenant",
        PartitionKey: partitionKey
        );

    static IdentityScopeGrant Grant(string id, string? partitionKey = null) => new(
        Grantee: Actor,
        Scope: Scope(id, partitionKey),
        Capabilities: ["read"],
        Source: "test");
}
