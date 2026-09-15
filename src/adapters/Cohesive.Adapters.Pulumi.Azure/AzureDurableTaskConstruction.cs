using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative_durabletask_v20251101.DurableTask;
using Pulumi.AzureNative_durabletask_v20251101.DurableTask.Inputs;
using DurableProvider = Pulumi.AzureNative_durabletask_v20251101.Provider;

namespace Cohesive.Adapters.Pulumi.Azure;

/// <summary>Validates and constructs one exact Azure Durable Task facility inside an existing Pulumi program.</summary>
/// <remarks>No cloud reads, state store, or lifecycle executor are introduced. Validation precedes every registration.</remarks>
public static class AzureDurableTaskConstruction
{
    /// <summary>Exact target supported by this adapter release.</summary>
    public const string Target = "pulumi-azure-native/3.16.0";
    /// <summary>Target facility constructed by this adapter.</summary>
    public const string Facility = "azure/durable-task";
    /// <summary>Pinned service API schema, generated with Azure Native 3.19.0.</summary>
    public const string ApiVersion = "2025-11-01";
    /// <summary>Built-in Durable Task Data Contributor role; assigned only at task-hub scope.</summary>
    public const string DataContributorRole = "0ad04412-c4d5-4796-b79c-f76d14c8d402";

    /// <summary>Checks exact realization, lifecycle, worker bindings, provider scope, physical identity, and policy.</summary>
    /// <param name="deployment">Canonical compiled deployment; this remains the topology authority.</param>
    /// <param name="policy">Explicit attributable provider policy.</param>
    /// <param name="subscriptionId">Subscription selected by the hosting Pulumi program.</param>
    /// <returns>Deterministically ordered actionable errors, or an empty collection.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    public static ImmutableArray<DocumentValidationDiagnostic> Validate(
        InfrastructureTargetDeploymentPlan deployment, AzureDurableTaskPolicy policy, Guid subscriptionId)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(policy);
        var errors = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        void Error(string code, string message) => errors.Add(new(
            "azure.durable-task." + code, DiagnosticSeverity.Error, message,
            SchemaLocation: policy.Resource.Value,
            Evidence: new(stage: "pulumi-azure-construction", subject: policy.Resource.Value ?? "unset-resource",
                sourceReferences: [deployment.Manifest.Fingerprint.Value,
                    .. policy.SourceReferences.IsDefault ? [] : policy.SourceReferences.Select(s => s.Value)])));

        if (!deployment.IsComplete || deployment.Realization?.IsReadinessObligationComplete != true)
        {
            Error("incomplete", "Compile a complete capability, physical-witness, and readiness realization before construction.");
            errors.AddRange(deployment.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        }
        if (deployment.Manifest.TargetFacilities.Profile.Target.Value != Target)
            Error("target", $"This adapter supports only target '{Target}'.");
        if (subscriptionId == Guid.Empty || policy.SubscriptionId != subscriptionId)
            Error("subscription", "The program subscription must be non-empty and equal the explicit provider policy subscription.");
        if (policy.SourceReferences.IsDefaultOrEmpty || policy.SourceReferences.Any(s => string.IsNullOrWhiteSpace(s.Value)))
            Error("provenance", "Supply non-empty source references attributing provider scope and network policy.");
        if (string.IsNullOrWhiteSpace(policy.ResourceGroupName) ||
            !Regex.IsMatch(policy.ResourceGroupName, @"\A[\p{L}\p{N}_().-]{1,90}\z") || policy.ResourceGroupName.EndsWith('.'))
            Error("resource-group", "Supply an Azure resource-group name of 1–90 valid characters without a trailing period.");
        if (string.IsNullOrWhiteSpace(policy.Location))
            Error("location", "Supply an explicit Azure location.");
        if (new[] { policy.ProviderName, policy.SchedulerName, policy.TaskHubName }.Any(string.IsNullOrWhiteSpace))
            Error("logical-name", "Supply explicit Pulumi provider, scheduler, and task-hub logical names; preserve existing names during migration.");
        if (policy.IpAllowlist.IsDefault || policy.IpAllowlist.Any(rule => !ValidNetwork(rule)))
            Error("network", "Supply an explicit IPv4 address/CIDR allowlist (empty denies public access); IPv6 and private networking are unsupported by this slice.");

        var resource = deployment.Manifest.Resources.SingleOrDefault(r => r.Resource == policy.Resource);
        if (resource is null || resource.Facility.Value != Facility)
            Error("facility", $"Select a canonical resource deployed by facility '{Facility}'.");
        else
        {
            if (!ParsePhysical(resource.PhysicalResource.Value).Success)
                Error("physical-identity", "Expected 'azure/durable-task/schedulers/<scheduler>/task-hubs/<hub>' with valid Azure names.");
            var lifecycle = deployment.Realization?.Lifecycle.Bindings
                .Where(b => b.Resource == policy.Resource).ToArray() ?? [];
            if (resource.Authority != policy.LifecycleAuthority || resource.ManagingInterpreter is not null ||
                lifecycle.Length != 1 || lifecycle[0].Disposition != InfrastructureLifecycleDisposition.Managed ||
                lifecycle[0].Interpreter.Value != Target || lifecycle[0].Authority != policy.LifecycleAuthority ||
                lifecycle[0].PhysicalResource != resource.PhysicalResource)
                Error("lifecycle", "The selected resource must be managed exclusively by this target and the expected Pulumi state authority.");
            var schedulerName = ParsePhysical(resource.PhysicalResource.Value).Groups[1].Value;
            if (deployment.Manifest.Resources.Any(r => r.Resource != resource.Resource &&
                (r.PhysicalResource == resource.PhysicalResource || schedulerName.Length > 0 &&
                    string.Equals(ParsePhysical(r.PhysicalResource.Value).Groups[1].Value, schedulerName, StringComparison.OrdinalIgnoreCase))))
                Error("alias", "Multiple canonical resources share this scheduler; select an unambiguous construction owner first.");
        }
        if (string.IsNullOrWhiteSpace(policy.WorkerContract.Value))
            Error("worker-contract", "Select the canonical worker binding contract explicitly.");
        foreach (var binding in Bindings(deployment, policy))
        {
            if (binding.Target != policy.Resource || binding.Contract != policy.WorkerContract)
                Error("binding", $"Binding '{binding.Id.Value}' uses an unsupported direction or contract for the selected facility.");
            else if (!deployment.Manifest.Workloads.Any(w => w.Workload == binding.Source) &&
                     deployment.Realization?.FindNonParticipation(binding.Source) is null)
                Error("worker", $"Binding '{binding.Id.Value}' must originate at a deployed or explicitly non-participating workload.");
        }
        return DocumentValidationDiagnostics.Normalize(errors.ToImmutable());
    }

    /// <summary>Registers the validated generated provider, scheduler, and task hub with Pulumi.</summary>
    /// <param name="deployment">Exact compiled deployment; verify its Aspire handoff before calling.</param>
    /// <param name="policy">Explicit provider policy and existing logical names.</param>
    /// <param name="subscriptionId">Hosting program's subscription, checked against policy before registration.</param>
    /// <param name="parent">Existing resource parent, or null for existing root resources. No implicit parent is added.</param>
    /// <param name="cancellationToken">Checked before any registration. Pulumi owns cancellation once registration starts.</param>
    /// <returns>Resources, noncredential connection outputs, and canonical access-grant construction.</returns>
    /// <exception cref="AzureDurableTaskValidationException">Validation failed; nothing was registered.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before construction.</exception>
    public static AzureDurableTaskResources Register(InfrastructureTargetDeploymentPlan deployment,
        AzureDurableTaskPolicy policy, Guid subscriptionId, Resource? parent = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = Validate(deployment, policy, subscriptionId);
        if (!diagnostics.IsEmpty)
            throw new AzureDurableTaskValidationException(diagnostics);
        var physical = ParsePhysical(deployment.Manifest.Resources.Single(r => r.Resource == policy.Resource).PhysicalResource.Value);
        cancellationToken.ThrowIfCancellationRequested();
        var provider = new DurableProvider(policy.ProviderName, new() { SubscriptionId = subscriptionId.ToString("D") },
            new() { Parent = parent });
        var scheduler = new Scheduler(policy.SchedulerName, new()
        {
            ResourceGroupName = policy.ResourceGroupName,
            Location = policy.Location,
            SchedulerName = physical.Groups[1].Value,
            Properties = new SchedulerPropertiesArgs
            {
                Sku = new SchedulerSkuArgs { Name = "Consumption" },
                IpAllowlist = policy.IpAllowlist.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            }
        }, new() { Provider = provider, Parent = parent });
        var hub = new TaskHub(policy.TaskHubName, new()
        {
            ResourceGroupName = policy.ResourceGroupName,
            SchedulerName = scheduler.Name,
            TaskHubName = physical.Groups[2].Value
        }, new() { Provider = provider, Parent = parent });
        return new(deployment, policy, scheduler, hub,
            [.. Bindings(deployment, policy).Where(b => deployment.Manifest.Workloads.Any(w => w.Workload == b.Source))
                .OrderBy(b => b.Id.Value, StringComparer.Ordinal)]);
    }

    static IEnumerable<InfrastructureBindingDefinition> Bindings(InfrastructureTargetDeploymentPlan deployment,
        AzureDurableTaskPolicy policy) => deployment.FacilityPlan.Definition.Definition.Bindings
        .Where(b => b.Target == policy.Resource || b.Source == policy.Resource);

    static Match ParsePhysical(string value) => Regex.Match(value,
        @"\Aazure/durable-task/schedulers/([a-zA-Z0-9-]{3,64})/task-hubs/([a-zA-Z0-9-]{3,64})\z");

    static bool ValidNetwork(string rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return false;
        var parts = rule.Split('/');
        return parts.Length <= 2 && IPAddress.TryParse(parts[0], out var address) &&
            address.AddressFamily == AddressFamily.InterNetwork &&
            parts[0] == address.ToString() &&
            (parts.Length == 1 || int.TryParse(parts[1], out var prefix) && prefix is >= 0 and <= 32);
    }
}

/// <summary>Structured provider-construction rejection. No provider resources have been registered.</summary>
public sealed class AzureDurableTaskValidationException : ArgumentException
{
    internal AzureDurableTaskValidationException(ImmutableArray<DocumentValidationDiagnostic> diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Code}: {d.Message}"))) => Diagnostics = diagnostics;
    /// <summary>Ordered attributable diagnostics describing unsupported or mismatched input.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}

/// <summary>One constructed facility and its derived canonical worker binding outputs.</summary>
public sealed class AzureDurableTaskResources
{
    internal AzureDurableTaskResources(InfrastructureTargetDeploymentPlan deployment, AzureDurableTaskPolicy policy,
        Scheduler scheduler, TaskHub hub, ImmutableArray<InfrastructureBindingDefinition> bindings)
    {
        Deployment = deployment;
        Policy = policy;
        Scheduler = scheduler;
        TaskHub = hub;
        WorkerBindings = bindings;
        Endpoint = scheduler.Properties.Apply(p => p.Endpoint);
        ConnectionString = Output.Tuple(Endpoint, hub.Name).Apply(v =>
            $"Endpoint={v.Item1};TaskHub={v.Item2};Authentication=ManagedIdentity");
    }
    /// <summary>Exact original deployment and its provenance; never reconstructed as another topology.</summary>
    public InfrastructureTargetDeploymentPlan Deployment { get; }
    /// <summary>Explicit construction policy retained for inspection.</summary>
    public AzureDurableTaskPolicy Policy { get; }
    /// <summary>Registered scheduler using the pinned service schema.</summary>
    public CustomResource Scheduler { get; }
    /// <summary>Registered task hub; this resource's ID is the sole role-assignment scope.</summary>
    public CustomResource TaskHub { get; }
    /// <summary>Canonical participating worker bindings in ordinal identity order.</summary>
    public ImmutableArray<InfrastructureBindingDefinition> WorkerBindings { get; }
    /// <summary>Service endpoint without credentials. Pulumi secret propagation remains intact.</summary>
    public Output<string> Endpoint { get; }
    /// <summary>Managed-identity connection configuration without embedded credentials.</summary>
    public Output<string> ConnectionString { get; }

    /// <summary>Produces explicit hub-scoped role-assignment inputs for a participating canonical worker.</summary>
    /// <param name="binding">Canonical binding from <see cref="WorkerBindings"/>.</param>
    /// <param name="subscriptionId">Subscription of the caller's Azure Native role-assignment provider.</param>
    /// <param name="principalId">Managed identity principal of that binding's source workload.</param>
    /// <param name="assignmentId">Existing or explicitly chosen role-assignment GUID; preserves cloud identity during migration.</param>
    /// <returns>Role inputs; the caller retains its existing Pulumi role name, parent, and provider.</returns>
    /// <exception cref="ArgumentException">The binding is absent, the scope differs, or the assignment ID is empty.</exception>
    /// <exception cref="ArgumentNullException">The principal input is null.</exception>
    public RoleAssignmentArgs AccessGrant(InfrastructureBindingId binding, Guid subscriptionId,
        Input<string> principalId, Guid assignmentId)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        if (!WorkerBindings.Any(b => b.Id == binding))
            throw new ArgumentException("Select a participating canonical worker binding.", nameof(binding));
        if (subscriptionId != Policy.SubscriptionId)
            throw new ArgumentException("Cross-subscription access grants are not supported.", nameof(subscriptionId));
        if (assignmentId == Guid.Empty)
            throw new ArgumentException("Supply the existing or explicitly chosen assignment GUID.", nameof(assignmentId));
        return new()
        {
            Scope = TaskHub.Id,
            PrincipalId = principalId,
            PrincipalType = "ServicePrincipal",
            RoleAssignmentName = assignmentId.ToString("D"),
            RoleDefinitionId = $"/subscriptions/{subscriptionId:D}/providers/Microsoft.Authorization/roleDefinitions/{AzureDurableTaskConstruction.DataContributorRole}"
        };
    }
}
