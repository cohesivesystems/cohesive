using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;

namespace Cohesive.Identity;

/// <summary>
/// Stable canonical query definitions used by <see cref="EntityRepositoryIdentityDirectory"/>.
/// </summary>
/// <remarks>
/// The definitions are authored once against the persisted Identity shape graph. Runtime lookup values are
/// supplied only as typed evaluation parameters and never become definition constants.
/// </remarks>
public static class IdentityDirectoryQueries
{
    const string RowsResultIdentity = "rows";
    const int UniqueLookupLimit = 2;

    static readonly ScalarTypeRef StringType = new(ScalarTypeKind.String);
    static readonly ArrayTypeRef StringArrayType = new(StringType);
    static readonly QueryResultId RowsResultIdentityId = new(RowsResultIdentity);

    static readonly ImmutableArray<RelationQueryFieldReference> PrincipalFields = Fields(
        IdentityDomainModel.PrincipalAccountShape,
        nameof(PrincipalAccountRecord.Id),
        nameof(PrincipalAccountRecord.Kind),
        nameof(PrincipalAccountRecord.Status),
        nameof(PrincipalAccountRecord.DisplayName),
        nameof(PrincipalAccountRecord.Email),
        nameof(PrincipalAccountRecord.Subject),
        nameof(PrincipalAccountRecord.ClientId));

    static readonly ImmutableArray<RelationQueryFieldReference> MembershipFields = Fields(
        IdentityDomainModel.ScopeMembershipShape,
        nameof(ScopeMembershipRecord.Id),
        nameof(ScopeMembershipRecord.PrincipalId),
        nameof(ScopeMembershipRecord.ScopeId),
        nameof(ScopeMembershipRecord.ScopeKind),
        nameof(ScopeMembershipRecord.Capabilities),
        nameof(ScopeMembershipRecord.Status),
        nameof(ScopeMembershipRecord.IsDefaultScope));

    static readonly ImmutableArray<RelationQueryFieldReference> ScopeFields = Fields(
        IdentityDomainModel.ScopeShape,
        nameof(IdentityScopeRecord.Id),
        nameof(IdentityScopeRecord.Kind),
        nameof(IdentityScopeRecord.Name),
        nameof(IdentityScopeRecord.Status),
        nameof(IdentityScopeRecord.ParentScopeId),
        nameof(IdentityScopeRecord.PartitionKey));

    static readonly ParameterizedRowsQuery PrincipalByIdQuery = CreatePrincipalLookup(
        queryId: "cohesive.identity/principals/by-id/v1",
        queryName: "IdentityPrincipalById",
        parameterId: "principalId",
        fieldName: nameof(PrincipalAccountRecord.Id));

    static readonly ParameterizedRowsQuery PrincipalByEmailQuery = CreatePrincipalLookup(
        queryId: "cohesive.identity/principals/by-email/v1",
        queryName: "IdentityPrincipalByEmail",
        parameterId: "email",
        fieldName: nameof(PrincipalAccountRecord.Email));

    static readonly ParameterizedRowsQuery PrincipalBySubjectQuery = CreatePrincipalLookup(
        queryId: "cohesive.identity/principals/by-subject/v1",
        queryName: "IdentityPrincipalBySubject",
        parameterId: "subject",
        fieldName: nameof(PrincipalAccountRecord.Subject));

    static readonly ParameterizedRowsQuery PrincipalByClientIdQuery = CreatePrincipalLookup(
        queryId: "cohesive.identity/principals/by-client-id/v1",
        queryName: "IdentityPrincipalByClientId",
        parameterId: "clientId",
        fieldName: nameof(PrincipalAccountRecord.ClientId));

    static readonly ParameterizedRowsQuery ActiveMembershipsByPrincipalQuery =
        CreateMembershipLookup();

    static readonly ParameterizedRowsQuery ActiveDefaultMembershipsByPrincipalAndKindQuery =
        CreateDefaultMembershipLookup();

    static readonly ParameterizedRowsQuery ActiveScopeByKindAndIdQuery = CreateScopeLookup();

    /// <summary>Canonical active-principal lookup by local principal identity.</summary>
    public static RelationQueryDocument PrincipalById => PrincipalByIdQuery.Document;

    /// <summary>Canonical active-principal lookup by normalized email.</summary>
    public static RelationQueryDocument PrincipalByEmail => PrincipalByEmailQuery.Document;

    /// <summary>Canonical active-principal lookup by identity-provider subject.</summary>
    public static RelationQueryDocument PrincipalBySubject => PrincipalBySubjectQuery.Document;

    /// <summary>Canonical active-principal lookup by OAuth client identity.</summary>
    public static RelationQueryDocument PrincipalByClientId => PrincipalByClientIdQuery.Document;

    /// <summary>Canonical active-membership lookup by principal identity.</summary>
    public static RelationQueryDocument ActiveMembershipsByPrincipal =>
        ActiveMembershipsByPrincipalQuery.Document;

    /// <summary>
    /// Canonical active default-membership lookup by principal identity, scope kind, and visible candidate scopes.
    /// </summary>
    public static RelationQueryDocument ActiveDefaultMembershipsByPrincipalAndKind =>
        ActiveDefaultMembershipsByPrincipalAndKindQuery.Document;

    /// <summary>Canonical active-scope lookup by scope kind and scope identity.</summary>
    public static RelationQueryDocument ActiveScopeByKindAndId => ActiveScopeByKindAndIdQuery.Document;

    internal static QueryResultId RowsResultId => RowsResultIdentityId;

    internal static RelationQueryEvaluation EvaluatePrincipalById(
        RelationQueryEvaluationId evaluationId,
        string principalId) =>
        PrincipalByIdQuery.Evaluate(evaluationId, ObservationValue.FromString(principalId));

    internal static RelationQueryEvaluation EvaluatePrincipalByEmail(
        RelationQueryEvaluationId evaluationId,
        string email) =>
        PrincipalByEmailQuery.Evaluate(evaluationId, ObservationValue.FromString(email));

    internal static RelationQueryEvaluation EvaluatePrincipalBySubject(
        RelationQueryEvaluationId evaluationId,
        string subject) =>
        PrincipalBySubjectQuery.Evaluate(evaluationId, ObservationValue.FromString(subject));

    internal static RelationQueryEvaluation EvaluatePrincipalByClientId(
        RelationQueryEvaluationId evaluationId,
        string clientId) =>
        PrincipalByClientIdQuery.Evaluate(evaluationId, ObservationValue.FromString(clientId));

    internal static RelationQueryEvaluation EvaluateActiveMembershipsByPrincipal(
        RelationQueryEvaluationId evaluationId,
        string principalId) =>
        ActiveMembershipsByPrincipalQuery.Evaluate(
            evaluationId,
            ObservationValue.FromString(principalId));

    internal static RelationQueryEvaluation EvaluateActiveDefaultMembershipsByPrincipalAndKind(
        RelationQueryEvaluationId evaluationId,
        string principalId,
        string scopeKind,
        string[] candidateScopeIds) =>
        ActiveDefaultMembershipsByPrincipalAndKindQuery.Evaluate(
            evaluationId,
            ObservationValue.FromString(principalId),
            ObservationValue.FromString(scopeKind),
            ObservationValue.FromImmutableArray(
                [.. candidateScopeIds.Select(ObservationValue.FromString)]));

    internal static RelationQueryEvaluation EvaluateActiveScopeByKindAndId(
        RelationQueryEvaluationId evaluationId,
        string scopeKind,
        string scopeId) =>
        ActiveScopeByKindAndIdQuery.Evaluate(
            evaluationId,
            ObservationValue.FromString(scopeKind),
            ObservationValue.FromString(scopeId));

    static ParameterizedRowsQuery CreatePrincipalLookup(
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
                    Expr.Const(nameof(PrincipalAccountStatus.Active))),
                Expr.Eq(principals.Binding.Field(fieldName), parameter.Expression)));
        var limited = author.Page(filtered, new OffsetPageDefinition(limit: UniqueLookupLimit));
        var rows = author.Rows(limited, id: RowsResultId);
        return new(
            RequireValid(author.BuildQuery(new(queryId), new(queryName), [rows])),
            [parameter],
            rows,
            PrincipalFields);
    }

    static ParameterizedRowsQuery CreateMembershipLookup()
    {
        var author = RelationQuery.Structural();
        var principalId = author.Parameter(StringType, id: new("principalId"));
        var memberships = author.Source(IdentityDomainModel.ScopeMembershipShape);
        var filtered = author.Filter(
            memberships.Node,
            All(
                Expr.Eq(
                    memberships.Binding.Field(nameof(ScopeMembershipRecord.Status)),
                    Expr.Const(nameof(ScopeMembershipStatus.Active))),
                Expr.Eq(
                    memberships.Binding.Field(nameof(ScopeMembershipRecord.PrincipalId)),
                    principalId.Expression)));
        var rows = author.Rows(filtered, id: RowsResultId);
        return new(
            RequireValid(author.BuildQuery(
                new("cohesive.identity/memberships/active-by-principal/v1"),
                new("IdentityActiveMembershipsByPrincipal"),
                [rows])),
            [principalId],
            rows,
            MembershipFields);
    }

    static ParameterizedRowsQuery CreateDefaultMembershipLookup()
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
                    Expr.Const(nameof(ScopeMembershipStatus.Active))),
                Expr.Eq(
                    memberships.Binding.Field(nameof(ScopeMembershipRecord.PrincipalId)),
                    principalId.Expression),
                Expr.Eq(
                    memberships.Binding.Field(nameof(ScopeMembershipRecord.ScopeKind)),
                    scopeKind.Expression),
                Expr.Eq(
                    memberships.Binding.Field(nameof(ScopeMembershipRecord.IsDefaultScope)),
                    Expr.Const(true)),
                Expr.Contains(
                    candidateScopeIds.Expression,
                    memberships.Binding.Field(nameof(ScopeMembershipRecord.ScopeId)))));
        var limited = author.Page(filtered, new OffsetPageDefinition(limit: UniqueLookupLimit));
        var rows = author.Rows(limited, id: RowsResultId);
        return new(
            RequireValid(author.BuildQuery(
                new("cohesive.identity/memberships/active-default-by-principal-and-kind/v1"),
                new("IdentityActiveDefaultMembershipByPrincipalAndKind"),
                [rows])),
            [principalId, scopeKind, candidateScopeIds],
            rows,
            MembershipFields);
    }

    static ParameterizedRowsQuery CreateScopeLookup()
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
                    Expr.Const(nameof(IdentityScopeStatus.Active))),
                Expr.Eq(scopes.Binding.Field(nameof(IdentityScopeRecord.Kind)), scopeKind.Expression),
                Expr.Eq(scopes.Binding.Field(nameof(IdentityScopeRecord.Id)), scopeId.Expression)));
        var limited = author.Page(filtered, new OffsetPageDefinition(limit: UniqueLookupLimit));
        var rows = author.Rows(limited, id: RowsResultId);
        return new(
            RequireValid(author.BuildQuery(
                new("cohesive.identity/scopes/active-by-kind-and-id/v1"),
                new("IdentityActiveScopeByKindAndId"),
                [rows])),
            [scopeKind, scopeId],
            rows,
            ScopeFields);
    }

    static ImmutableArray<RelationQueryFieldReference> Fields(
        QualifiedShapeId shape,
        params string[] fieldNames) =>
        [.. fieldNames.Select(fieldName => new RelationQueryFieldReference(shape, FieldPath.FromField(fieldName)))];

    static Expr All(params Expr[] predicates)
    {
        if (predicates.Length == 0)
            throw new ArgumentException("At least one canonical predicate is required.", nameof(predicates));

        var result = predicates[0];
        for (var index = 1; index < predicates.Length; index++)
            result = Expr.And(result, predicates[index]);
        return result;
    }

    static RelationQueryAuthoringResult<QueryDefinition> RequireValid(
        RelationQueryAuthoringResult<QueryDefinition> query)
    {
        if (query.Validation.IsValid)
            return query;

        throw new InvalidOperationException(
            $"Canonical Identity query '{query.Definition.Id.Value}' is invalid: "
            + string.Join(
                "; ",
                query.Validation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    sealed class ParameterizedRowsQuery(
        RelationQueryAuthoringResult<QueryDefinition> query,
        ImmutableArray<RelationQueryParameterHandle> parameters,
        RelationQueryResultHandle<RowsQueryResultDefinition> rows,
        ImmutableArray<RelationQueryFieldReference> selectedFields)
    {
        public RelationQueryDocument Document { get; } = query.CreateDocument();

        public RelationQueryEvaluation Evaluate(
            RelationQueryEvaluationId evaluationId,
            params ObservationValue[] values)
        {
            if (values.Length != parameters.Length)
            {
                throw new ArgumentException(
                    $"Identity query '{query.Definition.Id.Value}' requires {parameters.Length} parameter values.",
                    nameof(values));
            }

            var builder = Document.Evaluate(
                evaluationId,
                [IdentityDomainModel.ShapeGraphDocument]);
            for (var index = 0; index < parameters.Length; index++)
            {
                builder.Set(
                    parameters[index].Id,
                    values[index],
                    evidenceReference: "cohesive.identity/directory-parameter");
            }

            return builder.Select(rows.Id, selectedFields).Build();
        }
    }
}
