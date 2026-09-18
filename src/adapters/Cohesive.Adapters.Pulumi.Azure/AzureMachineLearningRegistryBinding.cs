using System.Collections.Immutable;
using Cohesive.Infra.Realization;
using Cohesive.Model.Serialization;
using Pulumi;
using ML = Pulumi.AzureNative.MachineLearningServices;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Explicit managed, referenced or disabled registry association; never constructs a registry or StackReference.</summary>
public static class AzureMachineLearningRegistryBinding
{
    const string Prefix = "azure/machine-learning/registries/";

    /// <summary>Pure validation of registry selection, exact lifecycle owner and the shared-output contract.</summary>
    /// <exception cref="ArgumentNullException">Deployment or policy is null.</exception>
    /// <param name="deployment">Exact compiled canonical deployment; retained rather than copied.</param>
    /// <param name="policy">Immutable, attributed canonical association and explicit lifecycle selection.</param>
    /// <param name="subscriptionId">Subscription configured on the native provider; must match the policy.</param>
    /// <returns>Normalized attributable errors, or an empty array for a supported association.</returns>
    public static ImmutableArray<DocumentValidationDiagnostic> Validate(InfrastructureTargetDeploymentPlan deployment,
        AzureMachineLearningRegistryPolicy policy, Guid subscriptionId)
    {
        ArgumentNullException.ThrowIfNull(deployment); ArgumentNullException.ThrowIfNull(policy);
        var errors = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        void Error(string code, string message) => errors.Add(AzureMachineLearningBinding.Diagnostic(deployment, policy.SourceReferences, policy.Registry?.Value, code, message));
        AzureConstructionPolicy.ValidateDeployment(deployment, AzureMachineLearningBinding.Target, policy.SubscriptionId, subscriptionId, policy.SourceReferences, errors, Error);
        if (policy.Mode is not (AzureMachineLearningRegistryMode.Disabled or AzureMachineLearningRegistryMode.Managed or AzureMachineLearningRegistryMode.Referenced))
            Error("registry-mode", "Explicitly select disabled, managed or referenced registry ownership.");
        if (policy.Mode == AzureMachineLearningRegistryMode.Disabled)
        {
            if (policy.Registry is not null || policy.LifecycleAuthority is not null || policy.ResourceGroupName is not null ||
                policy.ReferenceStack is not null || policy.EnabledOutput is not null || policy.NameOutput is not null)
                Error("registry-disabled", "A disabled registry selection cannot carry a resource, owner, scope or shared output contract.");
        }
        else
        {
            var resource = deployment.Manifest.Resources.SingleOrDefault(r => r.Resource == policy.Registry);
            if (resource is null || resource.Facility.Value != AzureMachineLearningBinding.RegistryFacility || AzureMachineLearningBinding.Name(resource.PhysicalResource.Value, Prefix) is null)
                Error("registry-identity", "Select a registry resource with physical identity azure/machine-learning/registries/<name>.");
            if (policy.LifecycleAuthority is not { } owner || !AzureMachineLearningBinding.ValidAuthority(owner))
                Error("registry-owner", "Select the exact registry pulumi/project/stack owner.");
            if (!AzureConstructionPolicy.ValidResourceGroup(policy.ResourceGroupName)) Error("registry-scope", "Select the registry's explicit resource group.");
            if (resource is not null)
            {
                var lifecycle = deployment.Realization?.Lifecycle.Bindings.Where(b => b.Resource == resource.Resource).ToArray() ?? [];
                var disposition = policy.Mode == AzureMachineLearningRegistryMode.Managed ? InfrastructureLifecycleDisposition.Managed : InfrastructureLifecycleDisposition.Referenced;
                if (resource.Authority != policy.LifecycleAuthority || lifecycle.Length != 1 || lifecycle[0].Authority != policy.LifecycleAuthority ||
                    lifecycle[0].PhysicalResource != resource.PhysicalResource || lifecycle[0].Interpreter.Value != AzureMachineLearningBinding.Target ||
                    lifecycle[0].Disposition != disposition ||
                    resource.ManagingInterpreter is not null)
                    Error("registry-lifecycle", "Registry ownership must match its explicit managed or referenced lifecycle; a shared reference is never locally managed.");
                if (deployment.Manifest.Resources.Any(r => r.Resource != resource.Resource && AzureMachineLearningBinding.Equal(r.PhysicalResource.Value, resource.PhysicalResource.Value)))
                    Error("registry-alias", "Multiple canonical resources claim the selected registry identity.");
            }
            if (policy.Mode == AzureMachineLearningRegistryMode.Referenced)
            {
                var stack = policy.ReferenceStack?.Split('/') ?? [];
                if (stack.Length != 3 || stack.Any(string.IsNullOrWhiteSpace) || policy.LifecycleAuthority?.Value != $"pulumi/{stack.ElementAtOrDefault(1)}/{stack.ElementAtOrDefault(2)}")
                    Error("registry-reference", "Select an exact organization/project/stack reference matching the declared foreign owner.");
                if (string.IsNullOrWhiteSpace(policy.EnabledOutput) || string.IsNullOrWhiteSpace(policy.NameOutput) || policy.EnabledOutput == policy.NameOutput)
                    Error("registry-outputs", "Select distinct explicit enabled and name output keys.");
            }
            else if (policy.ReferenceStack is not null || policy.EnabledOutput is not null || policy.NameOutput is not null)
                Error("registry-outputs", "Managed registry selection cannot also reference shared-stack outputs.");
        }
        return DocumentValidationDiagnostics.Normalize(errors.ToImmutable());
    }

    /// <summary>Fills the managed canonical name into native registry arguments, preserving native region/storage/ACR configuration.</summary>
    /// <remarks>Call before construction. Explicit network and system-assigned identity inputs are required. Disabled/referenced policies cannot enter this path.</remarks>
    /// <exception cref="ArgumentException">Mode or required native configuration is invalid.</exception>
    /// <exception cref="AzureMachineLearningValidationException">Canonical registry association is invalid.</exception>
    /// <param name="deployment">Exact compiled canonical deployment; retained rather than copied.</param>
    /// <param name="policy">Immutable, attributed canonical association and explicit lifecycle selection.</param>
    /// <param name="subscriptionId">Subscription configured on the native provider; must match the policy.</param>
    /// <param name="args">Caller-owned native SDK arguments mutated in place; call before native construction.</param>
    /// <returns>The same native argument object with checked canonical inputs.</returns>
    public static ML.RegistryArgs ConfigureManaged(InfrastructureTargetDeploymentPlan deployment, AzureMachineLearningRegistryPolicy policy,
        Guid subscriptionId, ML.RegistryArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RequireMode(deployment, policy, subscriptionId, AzureMachineLearningRegistryMode.Managed);
        if (args.PublicNetworkAccess is null || args.Identity is null || args.ResourceGroupName is null)
            throw new ArgumentException("Explicit registry network, identity and resource group inputs are required.", nameof(args));
        args.RegistryName = Name(deployment, policy);
        args.PublicNetworkAccess = ((Output<string>)args.PublicNetworkAccess).Apply(AzureMachineLearningBinding.Network);
        args.Identity = AzureMachineLearningBinding.Identity(args.Identity);
        args.ResourceGroupName = ((Output<string>)args.ResourceGroupName).Apply(value => AzureMachineLearningBinding.Equal(value, policy.ResourceGroupName!) ? value :
            throw new InvalidOperationException("Native registry resource group differs from the canonical scope."));
        return args;
    }

    /// <summary>Checks a caller-created managed registry and projects its availability and name; preserves native parent/provider identity.</summary>
    /// <exception cref="ArgumentException">Mode is not managed or the resource is null.</exception>
    /// <exception cref="AzureMachineLearningValidationException">Canonical association is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before association.</exception>
    /// <remarks>Resolved identity, owner, network or system-identity mismatches fault the checked outputs with InvalidOperationException.</remarks>
    /// <param name="deployment">Exact compiled canonical deployment; retained rather than copied.</param>
    /// <param name="policy">Immutable, attributed canonical association and explicit lifecycle selection.</param>
    /// <param name="subscriptionId">Subscription configured on the native provider; must match the policy.</param>
    /// <param name="registry">Caller-created native registry owned by this stack.</param>
    /// <param name="cancellationToken">Checked before attachment; does not cancel native registration already in progress.</param>
    /// <returns>Managed selection with the actual checked registry ID and availability outputs.</returns>
    public static AzureMachineLearningRegistryResources AttachManaged(InfrastructureTargetDeploymentPlan deployment,
        AzureMachineLearningRegistryPolicy policy, Guid subscriptionId, ML.Registry registry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(registry);
        RequireMode(deployment, policy, subscriptionId, AzureMachineLearningRegistryMode.Managed);
        var name = Name(deployment, policy);
        var id = Output.Tuple(registry.Id, registry.Name, registry.Urn, registry.PublicNetworkAccess, registry.Identity).Apply(value =>
        {
            if (!AzureConstructionPolicy.ValidResourceIdentity(value.Item2, value.Item1, name, policy.SubscriptionId, "Microsoft.MachineLearningServices", "registries", policy.ResourceGroupName)
                || !AzureConstructionPolicy.ValidPulumiUrn(value.Item3, policy.LifecycleAuthority!.Value, "azure-native:machinelearningservices:Registry") || value.Item5?.Type != "SystemAssigned")
                throw new InvalidOperationException("Native registry identity, owner or system-assigned identity differs from its canonical association.");
            AzureMachineLearningBinding.Network(value.Item4);
            return value.Item1;
        });
        return new(policy, registry, null, id, id.Apply(_ => true), id.Apply(_ => name));
    }

    /// <summary>Reads strictly typed outputs from the exact separately owned StackReference without constructing a registry.</summary>
    /// <remarks>Enabled must be Boolean, name must be string, disabled must have an empty name, and enabled must match the canonical name. Missing or contradictory values fault outputs without printing them. No fallback infers enablement. Unknown and secret outputs retain their Pulumi semantics. This proves a configuration reference, not live Azure existence or access.</remarks>
    /// <exception cref="ArgumentException">Mode is not referenced or the native reference is null.</exception>
    /// <exception cref="AzureMachineLearningValidationException">Canonical reference is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before association.</exception>
    /// <param name="deployment">Exact compiled canonical deployment; retained rather than copied.</param>
    /// <param name="policy">Immutable, attributed canonical association and explicit lifecycle selection.</param>
    /// <param name="subscriptionId">Subscription configured on the native provider; must match the policy.</param>
    /// <param name="reference">Caller-created native reference to the exact separately owned stack.</param>
    /// <param name="cancellationToken">Checked before attachment; does not cancel native registration already in progress.</param>
    /// <returns>Reference selection with classified availability outputs and no fabricated ARM ID.</returns>
    public static AzureMachineLearningRegistryResources AttachReference(InfrastructureTargetDeploymentPlan deployment,
        AzureMachineLearningRegistryPolicy policy, Guid subscriptionId, StackReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(reference);
        RequireMode(deployment, policy, subscriptionId, AzureMachineLearningRegistryMode.Referenced);
        var expected = Name(deployment, policy);
        var values = Output.Tuple(reference.Name, reference.GetOutput(policy.EnabledOutput!), reference.GetOutput(policy.NameOutput!)).Apply(value =>
        {
            if (value.Item1 != policy.ReferenceStack || value.Item2 is not bool enabled || value.Item3 is not string name ||
                (enabled ? !AzureMachineLearningBinding.Equal(name, expected) : name.Length != 0))
                throw new InvalidOperationException("Shared ML registry outputs are missing, contradictory, incorrectly typed or from a different declared stack.");
            return (Enabled: enabled, Name: name);
        });
        return new(policy, null, reference, null, values.Apply(v => v.Enabled), values.Apply(v => v.Name));
    }

    /// <summary>Materializes an explicit disabled selection with no native resource, reference, invoke or inferred enablement.</summary>
    /// <exception cref="ArgumentException">Mode is not disabled.</exception>
    /// <exception cref="AzureMachineLearningValidationException">The disabled policy is contradictory.</exception>
    /// <param name="deployment">Exact compiled canonical deployment; retained rather than copied.</param>
    /// <param name="policy">Immutable, attributed canonical association and explicit lifecycle selection.</param>
    /// <param name="subscriptionId">Subscription configured on the native provider; must match the policy.</param>
    /// <returns>Disabled selection with no native resources or references.</returns>
    public static AzureMachineLearningRegistryResources Disabled(InfrastructureTargetDeploymentPlan deployment,
        AzureMachineLearningRegistryPolicy policy, Guid subscriptionId)
    {
        RequireMode(deployment, policy, subscriptionId, AzureMachineLearningRegistryMode.Disabled);
        return new(policy, null, null, null, Output.Create(false), Output.Create(string.Empty));
    }

    static void RequireMode(InfrastructureTargetDeploymentPlan deployment, AzureMachineLearningRegistryPolicy policy, Guid subscriptionId, AzureMachineLearningRegistryMode mode)
    {
        AzureMachineLearningBinding.Require(Validate(deployment, policy, subscriptionId));
        if (policy.Mode != mode) throw new ArgumentException($"This operation requires explicit {mode} registry policy.", nameof(policy));
    }
    static string Name(InfrastructureTargetDeploymentPlan deployment, AzureMachineLearningRegistryPolicy policy) =>
        AzureMachineLearningBinding.Name(deployment.Manifest.FindResource(policy.Registry!.Value).PhysicalResource.Value, Prefix)!;
}
