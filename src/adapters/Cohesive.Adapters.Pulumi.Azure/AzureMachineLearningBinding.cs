using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi;
using ML = Pulumi.AzureNative.MachineLearningServices;
using Pulumi.AzureNative.Storage;
using Pulumi.AzureNative.KeyVault;
using Pulumi.AzureNative.ApplicationInsights;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Checks canonical ML ownership and associates native SDK resources without registering or invoking providers.</summary>
public static class AzureMachineLearningBinding
{
    /// <summary>Supported exact Azure Native target.</summary>
    public const string Target = "pulumi-azure-native/3.16.0";
    /// <summary>Canonical workspace facility.</summary>
    public const string WorkspaceFacility = "azure/machine-learning";
    /// <summary>Canonical registry facility; lifecycle distinguishes managed resources from references.</summary>
    public const string RegistryFacility = "azure/machine-learning-registry";

    /// <summary>Pure validation of exact workspace ownership and canonical supporting readiness dependencies.</summary>
    /// <exception cref="ArgumentNullException">Deployment or policy is null.</exception>
    /// <param name="deployment">Exact compiled canonical deployment; retained rather than copied.</param>
    /// <param name="policy">Immutable, attributed canonical association and explicit lifecycle selection.</param>
    /// <param name="subscriptionId">Subscription configured on the native provider; must match the policy.</param>
    /// <returns>Normalized attributable errors, or an empty array for a supported association.</returns>
    public static ImmutableArray<DocumentValidationDiagnostic> Validate(InfrastructureTargetDeploymentPlan deployment,
        AzureMachineLearningWorkspacePolicy policy, Guid subscriptionId)
    {
        ArgumentNullException.ThrowIfNull(deployment); ArgumentNullException.ThrowIfNull(policy);
        var errors = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        void Error(string code, string message) => errors.Add(Diagnostic(deployment, policy.SourceReferences, policy.Workspace.Value, code, message));
        AzureConstructionPolicy.ValidateDeployment(deployment, Target, policy.SubscriptionId, subscriptionId, policy.SourceReferences, errors, Error);
        if (!ValidAuthority(policy.LifecycleAuthority)) Error("authority", "Select an exact pulumi/project/stack owner.");
        if (!AzureConstructionPolicy.ValidResourceGroup(policy.ResourceGroupName)) Error("resource-group", "Select an explicit valid resource group.");
        Check(policy.Workspace, WorkspaceFacility, "azure/machine-learning/workspaces/");
        Check(policy.Storage, "azure/blob-storage", null);
        Check(policy.Vault, "azure/key-vault", "azure/key-vault/vaults/");
        Check(policy.Telemetry, "azure/application-insights", "azure/application-insights/components/");
        if (new[] { policy.Workspace, policy.Storage, policy.Vault, policy.Telemetry }.Distinct().Count() != 4)
            Error("identity", "Workspace and supporting resources require distinct canonical identities.");
        foreach (var dependency in new[] { policy.Storage, policy.Vault, policy.Telemetry })
            if (!deployment.FacilityPlan.Definition.Definition.ReadinessDependencies.Any(d => d.Subject == policy.Workspace && d.Dependency == dependency))
                Error("dependency", "Declare each workspace supporting dependency canonically.");
        return DocumentValidationDiagnostics.Normalize(errors.ToImmutable());

        void Check(InfrastructureNodeId id, string facility, string? prefix)
        {
            var resource = AzureConstructionPolicy.SelectManagedResource(deployment, id, facility, policy.LifecycleAuthority, Target, Error);
            if (resource is null) return;
            if (prefix is null ? !AzureBlobStorageConstruction.ParsePhysical(resource.PhysicalResource.Value).Success : Name(resource.PhysicalResource.Value, prefix) is null)
                Error("physical-identity", "Select an exact physical identity for the workspace and each supporting resource.");
            if (deployment.Manifest.Resources.Any(r => r.Resource != id && string.Equals(r.PhysicalResource.Value, resource.PhysicalResource.Value, StringComparison.OrdinalIgnoreCase)))
                Error("alias", "Multiple canonical resources claim one physical identity.");
        }
    }

    /// <summary>Returns the canonical native workspace name after pure validation.</summary>
    /// <exception cref="AzureMachineLearningValidationException">The canonical association is invalid.</exception>
    /// <param name="deployment">Exact compiled canonical deployment; retained rather than copied.</param>
    /// <param name="policy">Immutable, attributed canonical association and explicit lifecycle selection.</param>
    /// <param name="subscriptionId">Subscription configured on the native provider; must match the policy.</param>
    /// <returns>Canonical physical workspace name.</returns>
    public static string WorkspaceName(InfrastructureTargetDeploymentPlan deployment, AzureMachineLearningWorkspacePolicy policy, Guid subscriptionId)
    {
        Require(Validate(deployment, policy, subscriptionId));
        return Name(deployment.Manifest.FindResource(policy.Workspace).PhysicalResource.Value, "azure/machine-learning/workspaces/")!;
    }

    /// <summary>Fills canonical name and checked dependency IDs into caller-owned native arguments; preserves other SDK options.</summary>
    /// <remarks>Requires explicit native network, system-assigned identity and identity-based datastore configuration. Resolved invalid inputs fault before provider registration; unknown inputs stay unknown. Call before constructing the native workspace.</remarks>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Required native configuration is absent.</exception>
    /// <exception cref="AzureMachineLearningValidationException">Canonical association is invalid.</exception>
    /// <param name="deployment">Exact compiled canonical deployment; retained rather than copied.</param>
    /// <param name="policy">Immutable, attributed canonical association and explicit lifecycle selection.</param>
    /// <param name="subscriptionId">Subscription configured on the native provider; must match the policy.</param>
    /// <param name="args">Caller-owned native SDK arguments mutated in place; call before native construction.</param>
    /// <param name="storage">Existing native storage account owning the declared Blob container.</param>
    /// <param name="vault">Existing native Key Vault in the declared scope and lifecycle owner.</param>
    /// <param name="telemetry">Existing native Application Insights component in the declared scope and owner.</param>
    /// <returns>The same native argument object with checked canonical inputs.</returns>
    public static ML.WorkspaceArgs ConfigureWorkspace(InfrastructureTargetDeploymentPlan deployment, AzureMachineLearningWorkspacePolicy policy,
        Guid subscriptionId, ML.WorkspaceArgs args, StorageAccount storage, Vault vault, Component telemetry)
    {
        ArgumentNullException.ThrowIfNull(args);
        var name = WorkspaceName(deployment, policy, subscriptionId);
        if (args.PublicNetworkAccess is null || args.SystemDatastoresAuthMode is null || args.Identity is null || args.ResourceGroupName is null)
            throw new ArgumentException("Explicit network, managed identity, datastore authentication and resource group inputs are required.", nameof(args));
        args.PublicNetworkAccess = ((Output<Union<string, ML.PublicNetworkAccessType>>)args.PublicNetworkAccess).Apply(value => Network(value.Match(v => v, v => v.ToString())));
        args.SystemDatastoresAuthMode = ((Output<Union<string, ML.SystemDatastoresAuthMode>>)args.SystemDatastoresAuthMode).Apply(value =>
            value.Match(v => v, v => v.ToString()) == "Identity" ? "Identity" : throw new InvalidOperationException("ML workspace datastores must use explicit identity authentication."));
        args.Identity = Identity(args.Identity);
        args.ResourceGroupName = ((Output<string>)args.ResourceGroupName).Apply(value => string.Equals(value, policy.ResourceGroupName, StringComparison.OrdinalIgnoreCase)
            ? value : throw new InvalidOperationException("Native workspace resource group differs from the canonical scope."));
        var dependencies = Dependencies(deployment, policy, storage, vault, telemetry);
        args.WorkspaceName = name; args.StorageAccount = dependencies.Storage; args.KeyVault = dependencies.Vault; args.ApplicationInsights = dependencies.Telemetry;
        return args;
    }

    /// <summary>Associates the native workspace with its checked supporting resources and returns stable dependency-preserving outputs.</summary>
    /// <remarks>Native output mismatches fault with InvalidOperationException. The raw resources remain escape hatches. No rollback or provider call occurs here.</remarks>
    /// <exception cref="ArgumentNullException">A native resource is null.</exception>
    /// <exception cref="AzureMachineLearningValidationException">The association is invalid; resources may already be registered.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before attachment.</exception>
    /// <param name="deployment">Exact compiled canonical deployment; retained rather than copied.</param>
    /// <param name="policy">Immutable, attributed canonical association and explicit lifecycle selection.</param>
    /// <param name="subscriptionId">Subscription configured on the native provider; must match the policy.</param>
    /// <param name="workspace">Caller-created native workspace; attachment neither registers nor rolls it back.</param>
    /// <param name="storage">Existing native storage account owning the declared Blob container.</param>
    /// <param name="vault">Existing native Key Vault in the declared scope and lifecycle owner.</param>
    /// <param name="telemetry">Existing native Application Insights component in the declared scope and owner.</param>
    /// <param name="cancellationToken">Checked before attachment; does not cancel native registration already in progress.</param>
    /// <returns>Original native resources and lazy, checked dependency-preserving outputs.</returns>
    public static AzureMachineLearningWorkspaceResources AttachWorkspace(InfrastructureTargetDeploymentPlan deployment,
        AzureMachineLearningWorkspacePolicy policy, Guid subscriptionId, ML.Workspace workspace, StorageAccount storage,
        Vault vault, Component telemetry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(workspace);
        var name = WorkspaceName(deployment, policy, subscriptionId);
        var dependencies = Dependencies(deployment, policy, storage, vault, telemetry);
        var id = Output.All<string>(workspace.Id, workspace.Name, workspace.Urn, workspace.StorageAccount.Apply(v => v ?? ""), workspace.KeyVault.Apply(v => v ?? ""),
            workspace.ApplicationInsights.Apply(v => v ?? ""), dependencies.Storage, dependencies.Vault, dependencies.Telemetry, workspace.PublicNetworkAccess.Apply(v => v ?? ""),
            workspace.SystemDatastoresAuthMode.Apply(v => v ?? ""), workspace.Identity.Apply(i => i?.Type ?? "")).Apply(values =>
        {
            if (!AzureConstructionPolicy.ValidResourceIdentity(values[1], values[0], name, policy.SubscriptionId, "Microsoft.MachineLearningServices", "workspaces", policy.ResourceGroupName)
                || !AzureConstructionPolicy.ValidPulumiUrn(values[2], policy.LifecycleAuthority, "azure-native:machinelearningservices:Workspace")
                || !Equal(values[3], values[6]) || !Equal(values[4], values[7]) || !Equal(values[5], values[8])
                || values[10] != "Identity" || values[11] != "SystemAssigned")
                throw new InvalidOperationException("Native ML workspace identity, owner, dependencies or identity authentication differs from its canonical association.");
            Network(values[9]);
            return values[0];
        });
        return new(deployment, policy, workspace, id);
    }

    internal static bool Equal(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    internal static string Network(string? value) => value is "Enabled" or "Disabled" ? value : throw new InvalidOperationException("Select explicit Enabled or Disabled ML public network access.");
    internal static Output<ML.Inputs.ManagedServiceIdentityArgs> Identity(Input<ML.Inputs.ManagedServiceIdentityArgs> identity) =>
        ((Output<ML.Inputs.ManagedServiceIdentityArgs>)identity).Apply(value =>
        {
            if (value?.Type is null) throw new InvalidOperationException("Select an explicit system-assigned ML identity.");
            // The SDK's optional-map getter materializes an empty map. Preserve its absent state
            // by validating the original native object without reading unrelated optional fields.
            value.Type = ((Output<Union<string, ML.ManagedServiceIdentityType>>)value.Type).Apply(type =>
                type.Match(v => v, v => v.ToString()) == "SystemAssigned" ? "SystemAssigned" : throw new InvalidOperationException("This association requires a system-assigned ML identity."));
            return value;
        });

    static (Output<string> Storage, Output<string> Vault, Output<string> Telemetry) Dependencies(InfrastructureTargetDeploymentPlan deployment,
        AzureMachineLearningWorkspacePolicy policy, StorageAccount storage, Vault vault, Component telemetry)
    {
        ArgumentNullException.ThrowIfNull(storage); ArgumentNullException.ThrowIfNull(vault); ArgumentNullException.ThrowIfNull(telemetry);
        var storageName = AzureBlobStorageConstruction.ParsePhysical(deployment.Manifest.FindResource(policy.Storage).PhysicalResource.Value).Groups[1].Value;
        var vaultName = Name(deployment.Manifest.FindResource(policy.Vault).PhysicalResource.Value, "azure/key-vault/vaults/")!;
        var telemetryName = Name(deployment.Manifest.FindResource(policy.Telemetry).PhysicalResource.Value, "azure/application-insights/components/")!;
        return (Check(storage, storage.Name, storageName, "Microsoft.Storage", "storageAccounts", "azure-native:storage:StorageAccount"),
            Check(vault, vault.Name, vaultName, "Microsoft.KeyVault", "vaults", "azure-native:keyvault:Vault"),
            Check(telemetry, telemetry.Name, telemetryName, "Microsoft.Insights", "components", "azure-native:applicationinsights:Component"));
        Output<string> Check(CustomResource resource, Output<string> name, string expected, string provider, string kind, string type) =>
            Output.Tuple(name, resource.Id, resource.Urn).Apply(value =>
                AzureConstructionPolicy.ValidResourceIdentity(value.Item1, value.Item2, expected, policy.SubscriptionId, provider, kind, policy.ResourceGroupName)
                && AzureConstructionPolicy.ValidPulumiUrn(value.Item3, policy.LifecycleAuthority, type) ? value.Item2 :
                throw new InvalidOperationException("Native ML supporting resource identity or owner differs from the canonical association."));
    }

    internal static bool ValidAuthority(InfrastructureLifecycleAuthorityId authority)
    {
        var parts = authority.Value?.Split('/') ?? [];
        return parts.Length == 3 && parts[0] == "pulumi" && parts.All(p => !string.IsNullOrWhiteSpace(p));
    }
    internal static string? Name(string physical, string prefix)
    {
        var match = Regex.Match(physical, "\\A" + Regex.Escape(prefix) + @"([^/\s]+)\z");
        return match.Success ? match.Groups[1].Value : null;
    }
    internal static DocumentValidationDiagnostic Diagnostic(InfrastructureTargetDeploymentPlan deployment, ImmutableArray<SourceReference> sources,
        string? subject, string code, string message) => new("azure.machine-learning." + code, DiagnosticSeverity.Error, message, SchemaLocation: subject,
            Evidence: new(stage: "pulumi-azure-binding", subject: subject ?? "registry-selection", sourceReferences:
                [deployment.Manifest.Fingerprint.Value, .. sources.IsDefault ? [] : sources.Select(s => s.Value)]));
    internal static void Require(ImmutableArray<DocumentValidationDiagnostic> errors)
    {
        if (!errors.IsEmpty) throw new AzureMachineLearningValidationException(errors);
    }
}

/// <summary>Attributable rejection of an ML association without resolved provider values.</summary>
public sealed class AzureMachineLearningValidationException : ArgumentException
{
    internal AzureMachineLearningValidationException(ImmutableArray<DocumentValidationDiagnostic> diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Code}: {d.Message}"))) => Diagnostics = diagnostics;
    /// <summary>Ordered canonical diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}
