using Cohesive.Api.Execution.Services;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Storage;
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
    /// <returns>Immutable report containing exact canonical references and the original deployment artifact.</returns>
    public static ApplicationExplanationDocument Create(ApplicationModel application, InfrastructureAuthoringResult infrastructure,
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
        var compositions = application.Components<StorageModel>()
            .Select(model => (Name: model.GetType().FullName!, Composition: model.RequireComposition<IServiceStorageComposition>())).ToArray();
        var storage = new ServiceStorageComposition(compositions);
        storage.Validate(plans);
        return new(
            entities: [.. application.Components<DomainModel>().SelectMany(model => model.Definition.Entities)
                .OrderBy(entity => entity.Name.Value, StringComparer.Ordinal)],
            endpoints: [.. plans.SelectMany(plan => plan.Implementations)
                .OrderBy(item => item.Endpoint.Id.Value, StringComparer.Ordinal).Select(item => new ApplicationEndpointExplanation(item))],
            storage: storage.Explanation,
            history: [.. compositions.SelectMany(item => item.Composition.Bindings)
                .Distinct(ReferenceEqualityComparer.Instance).OfType<IStorageHistoryBinding>()
                .Select(binding => binding.HistoryDeclaration).OfType<StorageHistoryDeclaration>()
                .Distinct().OrderBy(history => history.Resource, StringComparer.Ordinal)
                .ThenBy(history => history.Authority).ThenBy(history => history.Reconstitution)],
            queries: [.. plans.SelectMany(plan => plan.QueryDependencies.Queries)
                .DistinctBy(query => query.Reference)
                .OrderBy(query => query.Reference.DefinitionId.Value, StringComparer.Ordinal)
                .Select(query => query.Request.DefinitionDocument)],
            executions: [.. application.Composition.Executions.Documents
                .OrderBy(document => document.Metadata.DefinitionId.Value, StringComparer.Ordinal)],
            infrastructure: infrastructure.Definition,
            deployment: artifact);
    }
}
