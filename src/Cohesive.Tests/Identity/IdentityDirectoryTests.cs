using Cohesive.Identity;

namespace Cohesive.Tests.Identity;

public sealed class IdentityDirectoryTests
{
    const string ScopeKind = "test.scope";

    [Fact]
    public async Task InMemoryIdentityDomainRepositories_ResolveActivePrincipalScopeGrants()
    {
        var directory = InMemoryIdentityDomainRepositoryFactory
            .Create(new InMemoryIdentityDirectoryBuilder()
                .AddScope(new(
                    Id: "scope-active",
                    Kind: ScopeKind,
                    Name: "Active Scope",
                    PartitionKey: "scope-active-partition"
                    ))
                .AddScope(new(
                    Id: "scope-archived",
                    Kind: ScopeKind,
                    Name: "Archived Scope",
                    Status: IdentityScopeStatus.Archived
                    ))
                .AddScope(new(
                    Id: "scope-revoked",
                    Kind: ScopeKind,
                    Name: "Revoked Scope"
                    ))
                .AddPrincipal(new(
                    Id: "user:active",
                    Kind: PrincipalKind.User,
                    Email: "active@example.com"
                    ))
                .AddPrincipal(new(
                    Id: "user:inactive",
                    Kind: PrincipalKind.User,
                    Status: PrincipalAccountStatus.Deactivated,
                    Email: "inactive@example.com"
                    ))
                .AddScopeGrant(
                    "user:active",
                    "scope-active",
                    ScopeKind,
                    ["identity.read"],
                    isDefaultScope: true
                    )
                .AddScopeGrant(
                    "user:active",
                    "scope-archived",
                    ScopeKind,
                    ["identity.read"]
                    )
                .AddMembership(new(
                    Id: "user:active:test.scope:scope-revoked",
                    PrincipalId: "user:active",
                    ScopeId: "scope-revoked",
                    ScopeKind: ScopeKind,
                    Capabilities: ["identity.read"],
                    Status: ScopeMembershipStatus.Revoked
                    ))
                .AddScopeGrant(
                    "user:inactive",
                    "scope-active",
                    ScopeKind,
                    ["identity.read"],
                    membershipId: "user:inactive:test.scope:scope-active"
                    )
                .Build())
            .CreateDirectory();

        var activePrincipal = await directory.FindPrincipalAsync(new(Email: "active@example.com"));
        var inactivePrincipal = await directory.FindPrincipalAsync(new(Email: "inactive@example.com"));
        var activeGrants = await directory.ListScopeGrantsAsync("user:active");
        var inactiveGrants = await directory.ListScopeGrantsAsync("user:inactive");
        var defaultScopeId = await directory.FindDefaultScopeIdAsync(
            "user:active",
            ScopeKind,
            new HashSet<string>(["scope-active", "scope-archived"], StringComparer.Ordinal)
            );

        Assert.NotNull(activePrincipal);
        Assert.Null(inactivePrincipal);
        var grant = Assert.Single(activeGrants);
        Assert.Equal("scope-active", grant.Scope.Id);
        Assert.Equal("scope-active-partition", grant.Scope.PartitionKey);
        Assert.Empty(inactiveGrants);
        Assert.Equal("scope-active", defaultScopeId);
    }
}
