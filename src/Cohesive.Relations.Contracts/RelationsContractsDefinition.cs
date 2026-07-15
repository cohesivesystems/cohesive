using Cohesive.Api;
using Cohesive.Model;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;
using CohesiveApi = Cohesive.Api.Api;

namespace Cohesive.Relations.Contracts;

/// <summary>
/// Code-generation roots for the frontend Cohesive relations contract package.
/// </summary>
public static class RelationsContractsDefinition
{
    /// <summary>
    /// API definition used only to expose relation semantic model roots to contract code-generation.
    /// </summary>
    [ApiDefinition]
    public static ApiDefinition Definition { get; } = ApiDefinition.From(
        CohesiveApi
            .Define("RelationsContracts")
            .Action("RelationQueryDocument")
            .Route("GET", "/relations/contracts/relation-query-document")
            .Returns<RelationQueryDocument>()
            .Build(),
        CohesiveApi
            .Define("RelationsContracts")
            .Action("QualifiedShapeId")
            .Route("GET", "/relations/contracts/qualified-shape-id")
            .Returns<QualifiedShapeId>()
            .Build(),
        CohesiveApi
            .Define("RelationsContracts")
            .Action("RelationshipCatalogDocument")
            .Route("GET", "/relations/contracts/relationship-catalog-document")
            .Returns<RelationshipCatalogDocument>()
            .Build(),
        CohesiveApi
            .Define("RelationsContracts")
            .Action("RelationDraftDocument")
            .Route("GET", "/relations/contracts/relation-draft-document")
            .Returns<RelationDraftDocument>()
            .Build(),
        CohesiveApi
            .Define("RelationsContracts")
            .Action("RelationQueryTargetCapabilityProfile")
            .Route("GET", "/relations/contracts/relation-query-target-capability-profile")
            .Returns<RelationQueryTargetCapabilityProfile>()
            .Build(),
        CohesiveApi
            .Define("RelationsContracts")
            .Action("RelationQueryRealizationReport")
            .Route("GET", "/relations/contracts/relation-query-realization-report")
            .Returns<RelationQueryRealizationReport>()
            .Build()
    );
}
