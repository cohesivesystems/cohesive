using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Adapters.Pulumi.Azure;

// Shared Azure scope syntax; facility-specific topology and admission remain with each constructor.
internal static class AzureConstructionPolicy
{
    // Common proof/scope checks at the third facility. Target topology and admission stay facility-specific.
    internal static void ValidateDeployment(InfrastructureTargetDeploymentPlan deployment, string target,
        Guid declaredSubscription, Guid hostSubscription, ImmutableArray<SourceReference> sources,
        ImmutableArray<DocumentValidationDiagnostic>.Builder diagnostics, Action<string, string> error)
    {
        if (!deployment.IsComplete || deployment.Realization?.IsReadinessObligationComplete != true)
        {
            error("incomplete", "Compile a complete capability, physical-witness and readiness realization before construction.");
            diagnostics.AddRange(deployment.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        }
        if (deployment.Manifest.TargetFacilities.Profile.Target.Value != target)
            error("target", $"This adapter supports only target '{target}'.");
        if (hostSubscription == Guid.Empty || declaredSubscription != hostSubscription)
            error("subscription", "Match the explicit non-empty host and policy subscriptions.");
        if (sources.IsDefaultOrEmpty || sources.Any(s => string.IsNullOrWhiteSpace(s.Value)))
            error("provenance", "Supply non-empty source references attributing provider scope and policy.");
    }

    internal static bool ValidResourceGroup(string? name) => !string.IsNullOrWhiteSpace(name) &&
        Regex.IsMatch(name, @"\A[\p{L}\p{N}_().-]{1,90}\z") && !name.EndsWith('.');

    internal static bool ValidTags(ImmutableSortedDictionary<string, string>? tags) => tags is not null && tags.Count <= 50 &&
        tags.All(t => !string.IsNullOrWhiteSpace(t.Key) && t.Key.Length <= 512 && t.Value is not null && t.Value.Length <= 256 &&
            t.Key.IndexOfAny(['<', '>', '%', '&', '\\', '?', '/']) < 0) &&
        tags.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == tags.Count;

    internal static IEnumerable<InfrastructureBindingDefinition> Bindings(InfrastructureTargetDeploymentPlan deployment,
        InfrastructureNodeId resource) => deployment.FacilityPlan.Definition.Definition.Bindings
        .Where(b => b.Target == resource || b.Source == resource);

    internal static InfrastructureTargetResourceDeployment? SelectManagedResource(
        InfrastructureTargetDeploymentPlan deployment, InfrastructureNodeId resourceId, string facility,
        InfrastructureLifecycleAuthorityId authority, string target, Action<string, string> error)
    {
        var resource = deployment.Manifest.Resources.SingleOrDefault(r => r.Resource == resourceId);
        if (resource is null || resource.Facility.Value != facility)
        {
            error("facility", $"Select a canonical resource deployed by facility '{facility}'.");
            return null;
        }
        var lifecycle = deployment.Realization?.Lifecycle.Bindings.Where(b => b.Resource == resourceId).ToArray() ?? [];
        if (resource.Authority != authority || resource.ManagingInterpreter is not null || lifecycle.Length != 1 ||
            lifecycle[0].Disposition != InfrastructureLifecycleDisposition.Managed || lifecycle[0].Interpreter.Value != target ||
            lifecycle[0].Authority != authority || lifecycle[0].PhysicalResource != resource.PhysicalResource)
            error("lifecycle", "The selected resource must be managed exclusively by this target and the expected Pulumi state authority.");
        return resource;
    }
}
