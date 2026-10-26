using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Tests.Identity;

/// <summary>
/// Frozen structural authoring of the Identity directory queries, kept as the reference that the expression-authored
/// definitions in <see cref="IdentityDirectoryQueries"/> must reproduce exactly. It differs from the original
/// structural definitions only in writing constants as typed literals.
/// </summary>
static class IdentityDirectoryStructuralReference
{
    const string RowsResultId = "rows";
    const int UniqueLookupLimit = 2;

    static readonly ScalarTypeRef StringType = new(ScalarTypeKind.String);
    static readonly ArrayTypeRef StringArrayType = new(StringType);

    public static RelationQueryDocument PrincipalById { get; } = CreatePrincipalLookup(
        "cohesive.identity/principals/by-id/v1", "IdentityPrincipalById", "principalId", nameof(PrincipalAccountRecord.Id));

    public static RelationQueryDocument PrincipalByEmail { get; } = CreatePrincipalLookup(
        "cohesive.identity/principals/by-email/v1", "IdentityPrincipalByEmail", "email", nameof(PrincipalAccountRecord.Email));

    public static RelationQueryDocument PrincipalBySubject { get; } = CreatePrincipalLookup(
        "cohesive.identity/principals/by-subject/v1", "IdentityPrincipalBySubject", "subject", nameof(PrincipalAccountRecord.Subject));

    public static RelationQueryDocument PrincipalByClientId { get; } = CreatePrincipalLookup(
        "cohesive.identity/principals/by-client-id/v1", "IdentityPrincipalByClientId", "clientId", nameof(PrincipalAccountRecord.ClientId));

    public static RelationQueryDocument ActiveMembershipsByPrincipal { get; } = CreateMembershipLookup();

    public static RelationQueryDocument ActiveDefaultMembershipsByPrincipalAndKind { get; } = CreateDefaultMembershipLookup();

    public static RelationQueryDocument ActiveScopeByKindAndId { get; } = CreateScopeLookup();

    static RelationQueryDocument CreatePrincipalLookup(
        string queryId,
        string queryName,
        string parameterId,
        string fieldName)
    {
        var author = RelationQuery.Structural();
        var parameter = author.Parameter(StringType, id: new(parameterId));
        var principals = author.Source(IdentityDomainModel.PrincipalAccountShape);
        var filtered = author.Filter(
            principals.Node,
            All(
                Expr.Eq(
                    principals.Binding.Field(nameof(PrincipalAccountRecord.Status)),
                    Active<PrincipalAccountStatus>()),
                Expr.Eq(principals.Binding.Field(fieldName), parameter.Expression)));
        var limited = author.Page(filtered, new OffsetPageDefinition(limit: UniqueLookupLimit));
        var rows = author.Rows(limited, id: new(RowsResultId));
        return author.BuildQuery(new(queryId), new(queryName), [rows]).CreateDocument();
    }

    static RelationQueryDocument CreateMembershipLookup()
    {
        var author = RelationQuery.Structural();
        var principalId = author.Parameter(StringType, id: new("principalId"));
        var memberships = author.Source(IdentityDomainModel.ScopeMembershipShape);
        var filtered = author.Filter(
            memberships.Node,
            All(
                Expr.Eq(
                    memberships.Binding.Field(nameof(ScopeMembershipRecord.Status)),
                    Active<ScopeMembershipStatus>()),
                Expr.Eq(
                    memberships.Binding.Field(nameof(ScopeMembershipRecord.PrincipalId)),
                    principalId.Expression)));
        var rows = author.Rows(filtered, id: new(RowsResultId));
        return author.BuildQuery(
                new("cohesive.identity/memberships/active-by-principal/v1"),
                new("IdentityActiveMembershipsByPrincipal"),
                [rows]).CreateDocument();
    }

    static RelationQueryDocument CreateDefaultMembershipLookup()
    {
        var author = RelationQuery.Structural();
        var principalId = author.Parameter(StringType, id: new("principalId"));
        var scopeKind = author.Parameter(StringType, id: new("scopeKind"));
        var candidateScopeIds = author.Parameter(StringArrayType, id: new("candidateScopeIds"));
        var memberships = author.Source(IdentityDomainModel.ScopeMembershipShape);
        var filtered = author.Filter(
            memberships.Node,
            All(
                Expr.Eq(
                    memberships.Binding.Field(nameof(ScopeMembershipRecord.Status)),
                    Active<ScopeMembershipStatus>()),
                Expr.Eq(
                    memberships.Binding.Field(nameof(ScopeMembershipRecord.PrincipalId)),
                    principalId.Expression),
                Expr.Eq(
                    memberships.Binding.Field(nameof(ScopeMembershipRecord.ScopeKind)),
                    scopeKind.Expression),
                Expr.Eq(
                    memberships.Binding.Field(nameof(ScopeMembershipRecord.IsDefaultScope)),
                    new LiteralExpr(new ScalarTypeRef(ScalarTypeKind.Bool), ObservationValue.FromBool(true))),
                Expr.Contains(
                    candidateScopeIds.Expression,
                    memberships.Binding.Field(nameof(ScopeMembershipRecord.ScopeId)))));
        var limited = author.Page(filtered, new OffsetPageDefinition(limit: UniqueLookupLimit));
        var rows = author.Rows(limited, id: new(RowsResultId));
        return author.BuildQuery(
                new("cohesive.identity/memberships/active-default-by-principal-and-kind/v1"),
                new("IdentityActiveDefaultMembershipByPrincipalAndKind"),
                [rows]).CreateDocument();
    }

    static RelationQueryDocument CreateScopeLookup()
    {
        var author = RelationQuery.Structural();
        var scopeKind = author.Parameter(StringType, id: new("scopeKind"));
        var scopeId = author.Parameter(StringType, id: new("scopeId"));
        var scopes = author.Source(IdentityDomainModel.ScopeShape);
        var filtered = author.Filter(
            scopes.Node,
            All(
                Expr.Eq(
                    scopes.Binding.Field(nameof(IdentityScopeRecord.Status)),
                    Active<IdentityScopeStatus>()),
                Expr.Eq(scopes.Binding.Field(nameof(IdentityScopeRecord.Kind)), scopeKind.Expression),
                Expr.Eq(scopes.Binding.Field(nameof(IdentityScopeRecord.Id)), scopeId.Expression)));
        var limited = author.Page(filtered, new OffsetPageDefinition(limit: UniqueLookupLimit));
        var rows = author.Rows(limited, id: new(RowsResultId));
        return author.BuildQuery(
                new("cohesive.identity/scopes/active-by-kind-and-id/v1"),
                new("IdentityActiveScopeByKindAndId"),
                [rows]).CreateDocument();
    }

    static Expr All(params Expr[] predicates)
    {
        if (predicates.Length == 0)
            throw new ArgumentException("At least one canonical predicate is required.", nameof(predicates));

        var result = predicates[0];
        for (var index = 1; index < predicates.Length; index++)
            result = Expr.And(result, predicates[index]);
        return result;
    }

    // Typed literals are the canonical constant form: the enum literal carries its declared type and members.
    static Expr Active<TStatus>()
        where TStatus : struct, Enum =>
        new LiteralExpr(
            new EnumTypeRef(typeof(TStatus).Name, [.. Enum.GetNames<TStatus>()]),
            ObservationValue.FromString("Active"));
}
