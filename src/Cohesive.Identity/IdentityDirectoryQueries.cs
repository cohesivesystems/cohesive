using System.Collections.Immutable;
using System.Linq.Expressions;
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

    static readonly RelationQueryClrAuthoringContext Clr = new();
    static readonly RelationQueryClrShape<PrincipalAccountRecord> PrincipalShape = Clr.Shape<PrincipalAccountRecord>(
        IdentityDomainModel.ShapeGraphDocument,
        IdentityDomainModel.PrincipalAccountShape);
    static readonly RelationQueryClrShape<ScopeMembershipRecord> MembershipShape = Clr.Shape<ScopeMembershipRecord>(
        IdentityDomainModel.ShapeGraphDocument,
        IdentityDomainModel.ScopeMembershipShape);
    static readonly RelationQueryClrShape<IdentityScopeRecord> ScopeShape = Clr.Shape<IdentityScopeRecord>(
        IdentityDomainModel.ShapeGraphDocument,
        IdentityDomainModel.ScopeShape);
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
        match: principalId => principal =>
            principal.Status == PrincipalAccountStatus.Active && principal.Id == principalId.Value);

    static readonly ParameterizedRowsQuery PrincipalByEmailQuery = CreatePrincipalLookup(
        queryId: "cohesive.identity/principals/by-email/v1",
        queryName: "IdentityPrincipalByEmail",
        parameterId: "email",
        match: email => principal =>
            principal.Status == PrincipalAccountStatus.Active && principal.Email == email.Value);

    static readonly ParameterizedRowsQuery PrincipalBySubjectQuery = CreatePrincipalLookup(
        queryId: "cohesive.identity/principals/by-subject/v1",
        queryName: "IdentityPrincipalBySubject",
        parameterId: "subject",
        match: subject => principal =>
            principal.Status == PrincipalAccountStatus.Active && principal.Subject == subject.Value);

    static readonly ParameterizedRowsQuery PrincipalByClientIdQuery = CreatePrincipalLookup(
        queryId: "cohesive.identity/principals/by-client-id/v1",
        queryName: "IdentityPrincipalByClientId",
        parameterId: "clientId",
        match: clientId => principal =>
            principal.Status == PrincipalAccountStatus.Active && principal.ClientId == clientId.Value);

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
        Func<RelationQueryExpressionParameter<string>, Expression<Func<PrincipalAccountRecord, bool>>> match)
    {
        var query = RelationQuery.Expression(Clr);
        var parameter = query.Parameter<string>(parameterId);
        var principals = query.Source(PrincipalShape).Where(match(parameter)).Take(UniqueLookupLimit);
        return Build(query, queryId, queryName, principals, PrincipalFields, parameter.Id);
    }

    static ParameterizedRowsQuery CreateMembershipLookup()
    {
        var query = RelationQuery.Expression(Clr);
        var principalId = query.Parameter<string>("principalId");
        var memberships = query.Source(MembershipShape).Where(membership =>
            membership.Status == ScopeMembershipStatus.Active
            && membership.PrincipalId == principalId.Value);
        return Build(
            query,
            "cohesive.identity/memberships/active-by-principal/v1",
            "IdentityActiveMembershipsByPrincipal",
            memberships,
            MembershipFields,
            principalId.Id);
    }

    static ParameterizedRowsQuery CreateDefaultMembershipLookup()
    {
        var query = RelationQuery.Expression(Clr);
        var principalId = query.Parameter<string>("principalId");
        var scopeKind = query.Parameter<string>("scopeKind");
        var candidateScopeIds = query.Parameter<string[]>("candidateScopeIds");
        var memberships = query.Source(MembershipShape).Where(membership =>
                membership.Status == ScopeMembershipStatus.Active
                && membership.PrincipalId == principalId.Value
                && membership.ScopeKind == scopeKind.Value
                && membership.IsDefaultScope == true
                && candidateScopeIds.Value.Contains(membership.ScopeId))
            .Take(UniqueLookupLimit);
        return Build(
            query,
            "cohesive.identity/memberships/active-default-by-principal-and-kind/v1",
            "IdentityActiveDefaultMembershipByPrincipalAndKind",
            memberships,
            MembershipFields,
            principalId.Id,
            scopeKind.Id,
            candidateScopeIds.Id);
    }

    static ParameterizedRowsQuery CreateScopeLookup()
    {
        var query = RelationQuery.Expression(Clr);
        var scopeKind = query.Parameter<string>("scopeKind");
        var scopeId = query.Parameter<string>("scopeId");
        var scopes = query.Source(ScopeShape).Where(scope =>
                scope.Status == IdentityScopeStatus.Active
                && scope.Kind == scopeKind.Value
                && scope.Id == scopeId.Value)
            .Take(UniqueLookupLimit);
        return Build(
            query,
            "cohesive.identity/scopes/active-by-kind-and-id/v1",
            "IdentityActiveScopeByKindAndId",
            scopes,
            ScopeFields,
            scopeKind.Id,
            scopeId.Id);
    }

    static ParameterizedRowsQuery Build<TNode, TRow>(
        RelationQueryExpressionAuthoring query,
        string queryId,
        string queryName,
        RelationQueryExpressionBoundNode<TNode, TRow> branch,
        ImmutableArray<RelationQueryFieldReference> selectedFields,
        params QueryParameterId[] parameters)
        where TNode : LogicalQueryNode
        where TRow : notnull
    {
        var rows = query.Rows(branch, id: RowsResultIdentity);
        return new(
            RequireValid(query.BuildQuery(new(queryId), new(queryName), [rows])),
            [.. parameters],
            rows.Id,
            selectedFields);
    }

    static ImmutableArray<RelationQueryFieldReference> Fields(
        QualifiedShapeId shape,
        params string[] fieldNames) =>
        [.. fieldNames.Select(fieldName => new RelationQueryFieldReference(shape, FieldPath.FromField(fieldName)))];


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

    sealed class ParameterizedRowsQuery
    {
        readonly RelationQueryAuthoringResult<QueryDefinition> query;
        readonly ImmutableArray<QueryParameterId> parameters;
        readonly RelationQueryCompilationRequest compilation;

        public ParameterizedRowsQuery(
            RelationQueryAuthoringResult<QueryDefinition> query,
            ImmutableArray<QueryParameterId> parameters,
            QueryResultId rows,
            ImmutableArray<RelationQueryFieldReference> selectedFields)
        {
            this.query = query;
            this.parameters = parameters;
            Document = query.CreateDocument();
            compilation = new(
                Document,
                [IdentityDomainModel.ShapeGraphDocument],
                demand: RelationQueryCompilationDemand.ForQueryResults(
                [
                    QueryResultDemand.SelectedFields(rows, selectedFields)
                ]));
        }

        public RelationQueryDocument Document { get; }

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

            var builder = compilation.Evaluate(evaluationId);
            for (var index = 0; index < parameters.Length; index++)
            {
                builder.Set(
                    parameters[index],
                    values[index],
                    evidenceReference: "cohesive.identity/directory-parameter");
            }

            return builder.Build();
        }
    }
}
