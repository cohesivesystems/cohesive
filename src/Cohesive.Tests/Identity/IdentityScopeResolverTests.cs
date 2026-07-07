using System.Collections.Immutable;
using Cohesive.Identity;

namespace Cohesive.Tests.Identity;

public sealed class IdentityScopeResolverTests
{
    static readonly PrincipalRef Actor = new("user-001", PrincipalKind.User, "Test User");

    [Fact]
    public void ResolveAccessibleScopes_PrefersGrantedScopes()
    {
        var identity = CreateIdentity(
            effectiveScopes:
            [
                Scope("tenant-a")
            ],
            grants:
            [
                Grant("tenant-b")
            ]);

        var scopes = identity.ResolveAccessibleScopes("tenant");

        Assert.Equal(["tenant-b"], scopes.Select(static scope => scope.Id));
    }

    [Fact]
    public void ResolveAccessibleScopes_FallsBackToEffectiveScopesWhenNoGrantsExist()
    {
        var identity = CreateIdentity(
            effectiveScopes:
            [
                Scope("tenant-a", partitionKey: "partition-a")
            ]);

        var scopes = identity.ResolveAccessibleScopes("tenant");

        var scope = Assert.Single(scopes);
        Assert.Equal("tenant-a", scope.Id);
        Assert.Equal("partition-a", scope.PartitionKey);
    }

    [Fact]
    public void ResolveRequestedScopes_FiltersDistinctRequestedScopeIds()
    {
        var context = CreateContext(
            effectiveScopes: [],
            grants:
            [
                Grant("tenant-a"),
                Grant("tenant-b")
            ]);

        var resolution = context.ResolveRequestedScopes("tenant", ["tenant-b", "tenant-b", " "]);

        Assert.Empty(resolution.RejectedScopeIds);
        Assert.Equal(["tenant-b"], resolution.Scopes.Select(static scope => scope.Id));
    }

    [Fact]
    public void ResolveRequestedScopes_ReturnsRejectedScopeIds()
    {
        var identity = CreateIdentity(
            effectiveScopes: [],
            grants:
            [
                Grant("tenant-a"),
                Grant("tenant-b")
            ]);

        var resolution = identity.ResolveRequestedScopes("tenant", ["tenant-b", "tenant-c"]);

        Assert.Equal(["tenant-b"], resolution.Scopes.Select(static scope => scope.Id));
        Assert.Equal(["tenant-c"], resolution.RejectedScopeIds.ToArray());
    }

    [Fact]
    public void ResolveDefaultScope_PrefersConfiguredDefaultScopeId()
    {
        var selected = IdentityScopeResolver.ResolveDefaultScope(
            accessibleScopes:
            [
                Scope("tenant-a"),
                Scope("tenant-b")
            ],
            defaultScopeId: "tenant-b");

        Assert.NotNull(selected);
        Assert.Equal("tenant-b", selected.Id);
    }

    [Fact]
    public void ResolvePartitionKey_UsesExplicitPartitionKeyWhenPresent()
    {
        var scope = Scope("tenant-a", partitionKey: "partition-a");

        var partitionKey = scope.ResolvePartitionKey();

        Assert.Equal("partition-a", partitionKey);
    }

    [Fact]
    public void ResolvePartitionKey_FallsBackToScopeId()
    {
        var scope = Scope("tenant-a");

        var partitionKey = scope.ResolvePartitionKey();

        Assert.Equal("tenant-a", partitionKey);
    }

    [Fact]
    public void ResolveScope_UsesAccessibleScopePartitionKey()
    {
        var context = CreateContext(
            effectiveScopes: [],
            grants:
            [
                Grant("tenant-a", partitionKey: "partition-a")
            ]);

        var scope = context.ResolveScope("tenant", "tenant-a");

        Assert.Equal("tenant-a", scope.Id);
        Assert.Equal("tenant", scope.Kind);
        Assert.Equal("partition-a", scope.PartitionKey);
        Assert.Equal("partition-a", scope.Scope.PartitionKey);
    }

    [Fact]
    public void ResolveRequestedResolvedScopes_PreservesPartitionKeys()
    {
        var context = CreateContext(
            effectiveScopes: [],
            grants:
            [
                Grant("tenant-a", partitionKey: "partition-a"),
                Grant("tenant-b", partitionKey: "partition-b")
            ]);

        var resolution = context.ResolveRequestedResolvedScopes("tenant", ["tenant-b"]);

        Assert.Empty(resolution.RejectedScopeIds);
        var scope = Assert.Single(resolution.Scopes);
        Assert.Equal("tenant-b", scope.Id);
        Assert.Equal("partition-b", scope.PartitionKey);
    }

    [Fact]
    public void TryResolveSingleScope_UsesEffectiveScopePartitionKey()
    {
        var context = CreateContext(
            effectiveScopes:
            [
                Scope("tenant-a", partitionKey: "partition-a")
            ]);

        var resolved = context.TryResolveSingleScope("tenant", out var scope);

        Assert.True(resolved);
        Assert.NotNull(scope);
        Assert.Equal("tenant-a", scope.Id);
        Assert.Equal("partition-a", scope.PartitionKey);
    }

    [Fact]
    public void RequireSingleScope_ThrowsWhenSingleEffectiveScopeIsMissing()
    {
        var context = CreateContext(effectiveScopes: []);

        var error = Assert.Throws<InvalidOperationException>(() => context.RequireSingleScope("tenant"));

        Assert.Equal("The operation requires exactly one selected 'tenant' scope.", error.Message);
    }

    static IdentityContext CreateIdentity(
        IReadOnlyList<ScopeRef> effectiveScopes,
        IReadOnlyList<IdentityScopeGrant>? grants = null
        )
    {
        var effective = new EffectiveScope(
            Scopes: [..effectiveScopes],
            Mode: effectiveScopes.Count == 1 ? ScopeSelectionMode.Single : ScopeSelectionMode.Multiple,
            Source: ScopeSelectionSource.Ambient
            );
        return new(
            Actor: Actor,
            EffectiveScope: effectiveScopes.Count == 0 ? null : effective,
            Grants: grants is null || grants.Count == 0 ? default : grants.ToImmutableArray()
            );
    }

    static OperationContext CreateContext(
        IReadOnlyList<ScopeRef> effectiveScopes,
        IReadOnlyList<IdentityScopeGrant>? grants = null
        ) => OperationContext.Create().WithIdentityContext(CreateIdentity(effectiveScopes, grants));

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
