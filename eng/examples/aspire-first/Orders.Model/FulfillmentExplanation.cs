using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.Host.Composition;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Relations.Serialization;
using Cohesive.Transitions.Authoring;

namespace AspireFirst.Orders;

/// <summary>Example presentation of the existing canonical artifacts and offline admission evidence.</summary>
/// <remarks>This report is not an executable system IR or a new fingerprint authority.</remarks>
public static class FulfillmentExplanation
{
    /// <summary>Explains an activated application and its compiled native association without starting a host.</summary>
    /// <param name="application">Same offline application instance used to declare infrastructure.</param>
    /// <param name="infrastructure">Canonical infrastructure authoring result.</param>
    /// <param name="deployment">Compiled association, not a live deployment.</param>
    /// <returns>JSON report containing exact canonical references and the original deployment artifact.</returns>
    public static JsonObject Create(ApplicationModel application, InfrastructureAuthoringResult infrastructure,
        InfrastructureTargetDeploymentPlan deployment)
    {
        if (deployment.Manifest.Definition != infrastructure.Definition.ToReference())
            throw new ArgumentException("Association belongs to a different infrastructure definition.", nameof(deployment));
        var plans = application.Components<ApiBindingModel>().Select(model => model.Plan).ToArray();
        var storage = new ServiceStorageComposition(application.Components<StorageModel>()
            .Select(model => (model.GetType().FullName!, model.RequireComposition<IServiceStorageComposition>())));
        storage.Validate(plans);
        var options = StrictDocumentJson.CreateOptions(PortableDocumentJsonFormatting.Indented);
        JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, options);
        var endpoints = plans.SelectMany(plan => plan.Implementations).OrderBy(item => item.Endpoint.Id.Value, StringComparer.Ordinal)
            .Select(item => new
            {
                item.Endpoint.Id, item.Endpoint.Kind,
                http = item.Endpoint.Http is { } http ? new { http.Method, http.Route } : null, item.Endpoint.ResultOf,
                response = item.Endpoint.PrimaryResult.Kind,
                execution = item.Execution ?? item.Endpoint.TransitionReference,
                repository = item.Repository,
                semanticReferences = item.Endpoint.SemanticReferences
            }).ToArray();
        var artifact = InfrastructureDeploymentArtifact.Create(deployment, "development",
            SourceReference.Create("example", infrastructure.Definition.Definition.Id.Value));
        return new()
        {
            ["scope"] = "Offline compilation and admission; no native IO or runtime readiness evidence.",
            ["entities"] = Node(application.Components<DomainModel>().SelectMany(model => model.Definition.Entities)
                .OrderBy(entity => entity.Name.Value, StringComparer.Ordinal)
                .Select(entity => new { entity.Name, observation = ValueContract.FromShape(entity.Shape) }).ToArray()),
            ["endpoints"] = Node(endpoints),
            ["storage"] = Node(storage.Explanation),
            ["queries"] = new JsonArray(plans.SelectMany(plan => plan.QueryDependencies.Queries)
                .DistinctBy(query => query.Reference)
                .OrderBy(query => query.Reference.DefinitionId.Value, StringComparer.Ordinal)
                .Select(query => (JsonNode?)JsonNode.Parse(RelationQueryJsonSerializer.Serialize(query.Request.DefinitionDocument))).ToArray()),
            ["executions"] = new JsonArray(application.Composition.Executions.Documents
                .OrderBy(document => document.Metadata.DefinitionId.Value, StringComparer.Ordinal)
                .Select(document => (JsonNode?)JsonNode.Parse(ExecutionDefinitionJsonSerializer.Serialize(document))).ToArray()),
            ["infrastructure"] = Node(infrastructure.Definition),
            ["deployment"] = JsonNode.Parse(artifact.ToJson())
        };
    }
}
