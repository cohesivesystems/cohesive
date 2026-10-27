using Cohesive.Identity;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Tests.Identity;

public sealed class IdentityDirectoryAuthoringEquivalenceTests
{
    public static TheoryData<string> Queries =>
    [
        nameof(IdentityDirectoryQueries.PrincipalById),
        nameof(IdentityDirectoryQueries.PrincipalByEmail),
        nameof(IdentityDirectoryQueries.PrincipalBySubject),
        nameof(IdentityDirectoryQueries.PrincipalByClientId),
        nameof(IdentityDirectoryQueries.ActiveMembershipsByPrincipal),
        nameof(IdentityDirectoryQueries.ActiveDefaultMembershipsByPrincipalAndKind),
        nameof(IdentityDirectoryQueries.ActiveScopeByKindAndId),
    ];

    [Theory]
    [MemberData(nameof(Queries))]
    public void Expression_authored_queries_reproduce_the_structural_definitions_exactly(string query)
    {
        var (expression, structural) = Pair(query);

        Assert.Equal(structural.DefinitionFingerprint, expression.DefinitionFingerprint);
        Assert.Equal(
            RelationQueryJsonSerializer.Serialize(structural, indented: false),
            RelationQueryJsonSerializer.Serialize(expression, indented: false));
    }

    static (RelationQueryDocument Expression, RelationQueryDocument Structural) Pair(string query) => query switch
    {
        nameof(IdentityDirectoryQueries.PrincipalById) =>
            (IdentityDirectoryQueries.PrincipalById, IdentityDirectoryStructuralReference.PrincipalById),
        nameof(IdentityDirectoryQueries.PrincipalByEmail) =>
            (IdentityDirectoryQueries.PrincipalByEmail, IdentityDirectoryStructuralReference.PrincipalByEmail),
        nameof(IdentityDirectoryQueries.PrincipalBySubject) =>
            (IdentityDirectoryQueries.PrincipalBySubject, IdentityDirectoryStructuralReference.PrincipalBySubject),
        nameof(IdentityDirectoryQueries.PrincipalByClientId) =>
            (IdentityDirectoryQueries.PrincipalByClientId, IdentityDirectoryStructuralReference.PrincipalByClientId),
        nameof(IdentityDirectoryQueries.ActiveMembershipsByPrincipal) =>
            (IdentityDirectoryQueries.ActiveMembershipsByPrincipal,
                IdentityDirectoryStructuralReference.ActiveMembershipsByPrincipal),
        nameof(IdentityDirectoryQueries.ActiveDefaultMembershipsByPrincipalAndKind) =>
            (IdentityDirectoryQueries.ActiveDefaultMembershipsByPrincipalAndKind,
                IdentityDirectoryStructuralReference.ActiveDefaultMembershipsByPrincipalAndKind),
        nameof(IdentityDirectoryQueries.ActiveScopeByKindAndId) =>
            (IdentityDirectoryQueries.ActiveScopeByKindAndId, IdentityDirectoryStructuralReference.ActiveScopeByKindAndId),
        _ => throw new ArgumentOutOfRangeException(nameof(query), query, "Unknown Identity directory query."),
    };
}
