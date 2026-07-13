using Cohesive.Api;
using Cohesive.Model;
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
            .Build()
    );
}
