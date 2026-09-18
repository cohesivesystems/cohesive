using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi;
using Pulumi.AzureNative.Storage;
using Pulumi.AzureNative.Storage.Inputs;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Constructs one shared account and its complete exact canonical blob-container placements.</summary>
public static class AzureBlobStorageConstruction
{
    /// <summary>Exact supported Azure Native target.</summary>
    public const string Target = AzureDurableTaskConstruction.Target;
    /// <summary>Supported private durable blob-container facility.</summary>
    public const string Facility = "azure/blob-storage";
    /// <summary>Azure Storage Blob Data Contributor; never grants queue or table permissions.</summary>
    public const string DataContributorRole = "ba92f5b4-2d11-453d-a403-e96b0029c9fe";

    /// <summary>Validates complete account membership, ownership and explicit binding scopes without I/O.</summary>
    /// <param name="deployment">Exact compiled plan and semantic authority.</param>
    /// <param name="policy">One attributable account policy and all canonical child declarations.</param>
    /// <param name="subscriptionId">Host provider's configured subscription, matched to policy.</param>
    /// <returns>Ordered attributable diagnostics, empty for supported input.</returns>
    /// <exception cref="ArgumentNullException">Deployment or policy is null.</exception>
    public static ImmutableArray<DocumentValidationDiagnostic> Validate(InfrastructureTargetDeploymentPlan deployment,
        AzureBlobStoragePolicy policy, Guid subscriptionId)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(policy);
        var errors = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        void Error(string code, string message) => errors.Add(new("azure.blob-storage." + code, DiagnosticSeverity.Error,
            message, SchemaLocation: policy.AccountOwner.Value,
            Evidence: new(stage: "pulumi-azure-construction", subject: policy.AccountOwner.Value ?? "unset-owner",
                sourceReferences: [deployment.Manifest.Fingerprint.Value,
                    .. policy.SourceReferences.IsDefault ? [] : policy.SourceReferences.Select(s => s.Value)])));
        AzureConstructionPolicy.ValidateDeployment(deployment, Target, policy.SubscriptionId, subscriptionId,
            policy.SourceReferences, errors, Error);
        if (!AzureConstructionPolicy.ValidResourceGroup(policy.ResourceGroupName) || string.IsNullOrWhiteSpace(policy.Location))
            Error("location", "Supply an explicit valid resource group and region.");
        if (string.IsNullOrWhiteSpace(policy.AccountName)) Error("logical-name", "Supply the preserved or explicitly chosen account logical name.");
        if (!AzureConstructionPolicy.ValidTags(policy.Tags)) Error("tags", "Supply valid non-secret Azure account tags.");
        if (string.IsNullOrWhiteSpace(policy.BlobContract.Value)) Error("binding", "Select the canonical blob read/write contract explicitly.");
        var owner = AzureConstructionPolicy.SelectManagedResource(deployment, policy.AccountOwner, Facility,
            policy.LifecycleAuthority, Target, Error);
        var ownerPhysical = ParsePhysical(owner?.PhysicalResource.Value ?? "");
        if (!ownerPhysical.Success) Error("physical-identity", "Expected azure/storage/accounts/<account>/blob-services/default/containers/<container> with valid Azure names.");

        var containers = policy.Containers.IsDefault ? [] : policy.Containers;
        if (containers.IsEmpty || containers.Any(c => c is null) || containers.Count(c => c?.Resource == policy.AccountOwner) != 1)
            Error("containers", "Supply the complete non-empty account topology, including its construction owner exactly once.");
        var resources = new HashSet<InfrastructureNodeId>();
        var physicalNames = new HashSet<string>(StringComparer.Ordinal);
        var logicalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in containers.Where(c => c is not null))
        {
            if (!resources.Add(container.Resource) || string.IsNullOrWhiteSpace(container.LogicalName) || !logicalNames.Add(container.LogicalName))
                Error("containers", "Canonical resources and Pulumi container logical names must be unique and non-empty.");
            var resource = AzureConstructionPolicy.SelectManagedResource(deployment, container.Resource, Facility,
                policy.LifecycleAuthority, Target, Error);
            var physical = ParsePhysical(resource?.PhysicalResource.Value ?? "");
            if (!physical.Success) Error("physical-identity", "Only exact supported blob-container placements are accepted.");
            else if (!ownerPhysical.Success || physical.Groups[1].Value != ownerPhysical.Groups[1].Value || !physicalNames.Add(physical.Groups[2].Value))
                Error("account", "All children must share the owner's account with no physical container aliases.");
        }
        if (ownerPhysical.Success)
        {
            var prefix = $"azure/storage/accounts/{ownerPhysical.Groups[1].Value}";
            foreach (var resource in deployment.Manifest.Resources.Where(r =>
                r.PhysicalResource.Value.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                r.PhysicalResource.Value.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)))
            {
                if (!resources.Contains(resource.Resource))
                    Error("account", $"Account member '{resource.Resource.Value}' is omitted; select one complete account construction owner.");
                if (resource.Facility.Value != Facility || !ParsePhysical(resource.PhysicalResource.Value).Success)
                    Error("facility", "This account slice supports blob containers only; account, queue/table or other shared placements require an explicit extension.");
            }
        }
        var bindings = Bindings(deployment, policy).ToArray();
        foreach (var binding in bindings)
            if (!resources.Contains(binding.Target) || binding.Contract != policy.BlobContract ||
                !deployment.Manifest.Workloads.Any(w => w.Workload == binding.Source) && deployment.Realization?.FindNonParticipation(binding.Source) is null)
                Error("binding", "Only incoming blob-contract bindings from deployed or explicitly non-participating workloads are supported; queue/table bindings are not blob access.");
        var active = bindings.Where(b => deployment.Manifest.Workloads.Any(w => w.Workload == b.Source)).ToArray();
        var access = policy.Access.IsDefault ? [] : policy.Access;
        if (policy.Access.IsDefault || access.Any(a => a is null) ||
            active.Any(b => access.Count(a => a?.Binding == b.Id) != 1) ||
            access.Any(a => a is not null && (!active.Any(b => b.Id == a.Binding) || a.Scope is not (AzureBlobScope.Account or AzureBlobScope.Container))))
            Error("access", "Declare exactly one explicit account/container scope per participating binding and none for absent consumers.");
        return DocumentValidationDiagnostics.Normalize(errors.ToImmutable());
    }

    /// <summary>Registers one account and every canonical child through the host's existing provider and parent.</summary>
    /// <param name="deployment">Exact plan; verify any Aspire handoff first.</param>
    /// <param name="policy">Single account policy, validated before registration.</param>
    /// <param name="subscriptionId">Actual host Azure Native subscription; the adapter cannot attest external provider configuration.</param>
    /// <param name="provider">Existing provider, or null to retain the host default. No provider is created.</param>
    /// <param name="parent">Existing common parent, or null for root resources.</param>
    /// <param name="resourceGroupDependency">Existing program resource group for creation/deletion ordering.</param>
    /// <param name="cancellationToken">Checked before registration; Pulumi owns cancellation after it starts.</param>
    /// <returns>Account, exact containers, credential-free endpoint outputs and scoped grant inputs.</returns>
    /// <exception cref="AzureBlobStorageValidationException">Unsupported plan or policy; no resources registered.</exception>
    /// <exception cref="OperationCanceledException">Canceled before construction.</exception>
    /// <exception cref="ArgumentNullException">Deployment or policy is null.</exception>
    public static AzureBlobStorageResources Register(InfrastructureTargetDeploymentPlan deployment, AzureBlobStoragePolicy policy,
        Guid subscriptionId, global::Pulumi.AzureNative.Provider? provider = null, Resource? parent = null,
        Resource? resourceGroupDependency = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = Validate(deployment, policy, subscriptionId);
        if (!diagnostics.IsEmpty) throw new AzureBlobStorageValidationException(diagnostics);
        var accountName = ParsePhysical(deployment.Manifest.FindResource(policy.AccountOwner).PhysicalResource.Value).Groups[1].Value;
        cancellationToken.ThrowIfCancellationRequested();
        var account = new StorageAccount(policy.AccountName, new()
        {
            AccountName = accountName, ResourceGroupName = policy.ResourceGroupName, Location = policy.Location,
            Kind = Kind.StorageV2, Sku = new SkuArgs { Name = SkuName.Standard_LRS },
            AllowBlobPublicAccess = false, EnableHttpsTrafficOnly = true, MinimumTlsVersion = MinimumTlsVersion.TLS1_2,
            Tags = policy.Tags.ToDictionary(t => t.Key, t => t.Value)
        }, new() { Provider = provider, Parent = parent, DependsOn = resourceGroupDependency is null ? [] : [resourceGroupDependency] });
        var containers = ImmutableDictionary.CreateBuilder<InfrastructureNodeId, BlobContainer>();
        foreach (var container in policy.Containers.OrderBy(c => c.Resource.Value, StringComparer.Ordinal))
        {
            var name = ParsePhysical(deployment.Manifest.FindResource(container.Resource).PhysicalResource.Value).Groups[2].Value;
            containers.Add(container.Resource, new(container.LogicalName, new()
            {
                AccountName = account.Name, ContainerName = name, ResourceGroupName = policy.ResourceGroupName,
                PublicAccess = PublicAccess.None
            }, new() { Provider = provider, Parent = parent }));
        }
        return new(deployment, policy, account, containers.ToImmutable(),
            [.. Bindings(deployment, policy).Where(b => deployment.Manifest.Workloads.Any(w => w.Workload == b.Source))
                .OrderBy(b => b.Id.Value, StringComparer.Ordinal)]);
    }

    internal static IEnumerable<InfrastructureBindingDefinition> Bindings(InfrastructureTargetDeploymentPlan deployment, AzureBlobStoragePolicy policy)
    {
        var selected = (policy.Containers.IsDefault ? [] : policy.Containers).Where(c => c is not null).Select(c => c.Resource).ToHashSet();
        return deployment.FacilityPlan.Definition.Definition.Bindings.Where(b => selected.Contains(b.Target) || selected.Contains(b.Source));
    }

    internal static Match ParsePhysical(string value) => Regex.Match(value,
        @"\Aazure/storage/accounts/([a-z0-9]{3,24})/blob-services/default/containers/([a-z0-9](?:[a-z0-9]|-(?!-)){1,61}[a-z0-9])\z");
}

/// <summary>Ordered attributable construction diagnostics, raised before any Pulumi registration.</summary>
public sealed class AzureBlobStorageValidationException : ArgumentException
{
    internal AzureBlobStorageValidationException(ImmutableArray<DocumentValidationDiagnostic> diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Code}: {d.Message}"))) => Diagnostics = diagnostics;
    /// <summary>Exact plan/policy violations.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}
