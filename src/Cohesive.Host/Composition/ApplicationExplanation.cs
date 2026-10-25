using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Relations.Serialization;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Host.Composition;

/// <summary>Reusable inspection of the existing canonical artifacts and offline admission evidence.</summary>
/// <remarks>This report is not an executable system IR or a new fingerprint authority.</remarks>
public static class ApplicationExplanation
{
    /// <summary>Explains an activated application and its compiled native association without starting a host.</summary>
    /// <param name="application">Same offline application instance used to declare infrastructure.</param>
    /// <param name="infrastructure">Canonical infrastructure authoring result.</param>
    /// <param name="deployment">Compiled association, not a live deployment.</param>
    /// <param name="environmentName">Explicit target environment; not inferred from ambient configuration.</param>
    /// <param name="deploymentSource">Explicit native deployment source attribution.</param>
    /// <exception cref="ArgumentNullException">A required input is null.</exception>
    /// <exception cref="ArgumentException">Deployment is incomplete, attribution is invalid, or the association is foreign.</exception>
    /// <exception cref="ServiceBindingValidationException">Storage admission fails.</exception>
    /// <returns>JSON report containing exact canonical references and the original deployment artifact.</returns>
    public static JsonObject Create(ApplicationModel application, InfrastructureAuthoringResult infrastructure,
        InfrastructureTargetDeploymentPlan deployment, string environmentName, SourceReference deploymentSource)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(infrastructure);
        ArgumentNullException.ThrowIfNull(deployment);
        // Reject incomplete compilation before storage admission or report projection.
        var artifact = InfrastructureDeploymentArtifact.Create(deployment, environmentName, deploymentSource);
        if (deployment.Manifest.Definition != infrastructure.Definition.ToReference())
            throw new ArgumentException("Association belongs to a different infrastructure definition.", nameof(deployment));
        var plans = application.Components<IServiceImplementationModel>().Select(model => model.Plan).ToArray();
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
