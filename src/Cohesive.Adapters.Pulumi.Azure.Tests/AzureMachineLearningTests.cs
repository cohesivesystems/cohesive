using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Pulumi.Azure;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi;
using Pulumi.Testing;
using ML = Pulumi.AzureNative.MachineLearningServices;
using Pulumi.AzureNative.Storage;
using Pulumi.AzureNative.KeyVault;
using Pulumi.AzureNative.ApplicationInsights;

namespace Cohesive.Adapters.Pulumi.Azure.Tests;

public sealed class AzureMachineLearningTests
{
    static readonly Guid Subscription = Guid.Parse("b6708815-d5b5-4070-af5b-675272a80b77");
    static readonly SourceReference Source = SourceReference.Create("test", "machine-learning");
    static readonly InfrastructureLifecycleAuthorityId Owner = new("pulumi/test/production");
    static readonly InfrastructureLifecycleAuthorityId SharedOwner = new("pulumi/shared/production");
    static readonly InfrastructureNodeId Workspace = new("resources/ml");
    static readonly InfrastructureNodeId Storage = new("resources/storage");
    static readonly InfrastructureNodeId Vault = new("resources/vault");
    static readonly InfrastructureNodeId Telemetry = new("resources/telemetry");
    static readonly InfrastructureNodeId Registry = new("resources/registry");
    const string Prefix = "/subscriptions/b6708815-d5b5-4070-af5b-675272a80b77/resourceGroups/test-rg/providers/";
    const string WorkspaceId = Prefix + "Microsoft.MachineLearningServices/workspaces/test-workspace";
    const string RegistryId = Prefix + "Microsoft.MachineLearningServices/registries/test-registry";
    static TestOptions Options(bool preview = false) => new() { ProjectName = "test", StackName = "production", IsPreview = preview };
    static AzureMachineLearningWorkspacePolicy Policy() => new()
    {
        Workspace = Workspace, Storage = Storage, Vault = Vault, Telemetry = Telemetry,
        LifecycleAuthority = Owner, SubscriptionId = Subscription, ResourceGroupName = "test-rg", SourceReferences = [Source]
    };
    static AzureMachineLearningRegistryPolicy RegistryPolicy(AzureMachineLearningRegistryMode mode) => new()
    {
        Mode = mode, Registry = mode == AzureMachineLearningRegistryMode.Disabled ? null : Registry,
        LifecycleAuthority = mode == AzureMachineLearningRegistryMode.Disabled ? null : mode == AzureMachineLearningRegistryMode.Referenced ? SharedOwner : Owner,
        SubscriptionId = Subscription, ResourceGroupName = mode == AzureMachineLearningRegistryMode.Disabled ? null : "test-rg",
        ReferenceStack = mode == AzureMachineLearningRegistryMode.Referenced ? "org/shared/production" : null,
        EnabledOutput = mode == AzureMachineLearningRegistryMode.Referenced ? "enabled" : null,
        NameOutput = mode == AzureMachineLearningRegistryMode.Referenced ? "name" : null, SourceReferences = [Source]
    };

    [Theory]
    [InlineData("Enabled")]
    [InlineData("Disabled")]
    public async Task Native_workspace_options_and_dependency_identity_are_preserved(string network)
    {
        var plan = Plan(); var mocks = new Mocks(); var checkedOptions = false;
        Assert.True(plan.IsComplete, string.Join(";", plan.Diagnostics));
        await Deployment.TestAsync(mocks, Options(), async () =>
        {
            var dependency = new ComponentResource("test:index:Dependency", "dependency");
            ComponentResource? parent = null;
            parent = new ComponentResource("test:index:Parent", "parent", new ComponentResourceOptions
            {
                ResourceTransformations = { args =>
                {
                    if (args.Resource is ML.Workspace)
                    {
                        Assert.Same(parent, args.Options.Parent); Assert.True(args.Options.Protect);
                        ((Output<ImmutableArray<Resource>>)args.Options.DependsOn).Apply(items =>
                        { Assert.Contains(dependency, items); checkedOptions = true; return items; });
                    }
                    return null;
                } }
            });
            var provider = new global::Pulumi.AzureNative.Provider("provider", new() { SubscriptionId = Subscription.ToString() });
            var (storage, vault, telemetry) = Supports();
            var args = WorkspaceArgs(network);
            Assert.Same(args, AzureMachineLearningBinding.ConfigureWorkspace(plan, Policy(), Subscription, args, storage, vault, telemetry));
            var workspace = new ML.Workspace("existing-workspace", args, new() { Parent = parent, Provider = provider, Protect = true, DependsOn = { dependency }, AdditionalSecretOutputs = { "storageAccount" } });
            var result = AzureMachineLearningBinding.AttachWorkspace(plan, Policy(), Subscription, workspace, storage, vault, telemetry);
            Assert.Same(workspace, result.Workspace); Assert.Same(plan, result.Deployment);
            Assert.True(await Output.IsSecretAsync(result.WorkspaceId));
            result.WorkspaceId.Apply(value => { Assert.Equal(WorkspaceId, value); return value; });
            result.WorkspaceName.Apply(value => { Assert.Equal("test-workspace", value); return value; });
        });
        Assert.True(checkedOptions);
        var native = Assert.Single(mocks.Resources, r => r.Type == "azure-native:machinelearningservices:Workspace");
        Assert.Contains("provider", native.Provider); Assert.Equal("existing-workspace", native.Name);
        Assert.Equal(network, native.Inputs["publicNetworkAccess"]); Assert.Equal("Identity", native.Inputs["systemDatastoresAuthMode"]);
        Assert.Equal("native friendly name", native.Inputs["friendlyName"]); Assert.Equal(false, native.Inputs["hbiWorkspace"]);
        Assert.Equal(Prefix + "Microsoft.Storage/storageAccounts/teststorage", native.Inputs["storageAccount"]);
        Assert.Equal(Prefix + "Microsoft.KeyVault/vaults/test-vault", native.Inputs["keyVault"]);
        Assert.Equal(Prefix + "Microsoft.Insights/components/test-insights", native.Inputs["applicationInsights"]);
        Assert.Equal(4, mocks.Resources.Count(r => r.Type?.StartsWith("azure-native:") == true));
    }

    [Theory]
    [InlineData("dependency")]
    [InlineData("owner")]
    [InlineData("alias")]
    [InlineData("physical")]
    [InlineData("subscription")]
    [InlineData("provenance")]
    [InlineData("scope")]
    [InlineData("identity")]
    public void Invalid_workspace_policy_is_rejected_before_registration(string failure)
    {
        var policy = failure switch
        {
            "subscription" => Policy() with { SubscriptionId = Guid.NewGuid() },
            "provenance" => Policy() with { SourceReferences = [] },
            "scope" => Policy() with { ResourceGroupName = "" },
            "identity" => Policy() with { Vault = Storage },
            _ => Policy()
        };
        var error = Assert.Throws<AzureMachineLearningValidationException>(() => AzureMachineLearningBinding.WorkspaceName(Plan(failure: failure), policy, Subscription));
        Assert.All(error.Diagnostics, d => Assert.NotNull(d.Evidence));
    }

    [Theory]
    [InlineData("workspace-name")]
    [InlineData("workspace-subscription")]
    [InlineData("workspace-group")]
    [InlineData("workspace-kind")]
    [InlineData("storage-name")]
    [InlineData("vault-name")]
    [InlineData("telemetry-name")]
    [InlineData("storage-link")]
    [InlineData("vault-link")]
    [InlineData("telemetry-link")]
    [InlineData("workspace-network")]
    [InlineData("workspace-auth")]
    [InlineData("workspace-identity")]
    public async Task Native_workspace_mismatches_fault_outputs(string mismatch)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Deployment.TestAsync(new Mocks(mismatch), Options(), () =>
        {
            var (storage, vault, telemetry) = Supports();
            var workspace = new ML.Workspace("workspace", AzureMachineLearningBinding.ConfigureWorkspace(Plan(), Policy(), Subscription, WorkspaceArgs(), storage, vault, telemetry));
            AzureMachineLearningBinding.AttachWorkspace(Plan(), Policy(), Subscription, workspace, storage, vault, telemetry).WorkspaceId.Apply(v => v);
        }));
        Assert.Contains(mismatch == "workspace-network" ? "explicit Enabled or Disabled" : "canonical association", error.ToString()); Assert.DoesNotContain("private-provider-marker", error.ToString());
    }

    [Theory]
    [InlineData("network")]
    [InlineData("identity")]
    [InlineData("auth")]
    [InlineData("group")]
    public async Task Invalid_explicit_native_inputs_cannot_register_workspace(string failure)
    {
        var mocks = new Mocks();
        await Assert.ThrowsAnyAsync<Exception>(() => Deployment.TestAsync(mocks, Options(), () =>
        {
            var (storage, vault, telemetry) = Supports(); var args = WorkspaceArgs();
            if (failure == "network") args.PublicNetworkAccess = "unexpected";
            if (failure == "identity") args.Identity = new ML.Inputs.ManagedServiceIdentityArgs { Type = "None" };
            if (failure == "auth") args.SystemDatastoresAuthMode = "AccessKey";
            if (failure == "group") args.ResourceGroupName = "other-rg";
            _ = new ML.Workspace("workspace", AzureMachineLearningBinding.ConfigureWorkspace(Plan(), Policy(), Subscription, args, storage, vault, telemetry));
        }));
        Assert.DoesNotContain(mocks.Resources, r => r.Type == "azure-native:machinelearningservices:Workspace");
    }

    [Fact]
    public async Task Missing_native_policy_is_rejected_and_unknown_preview_does_not_invent_identity()
    {
        var resolved = false;
        await Deployment.TestAsync(new Mocks("unknown"), Options(preview: true), () =>
        {
            var (storage, vault, telemetry) = Supports();
            Assert.Throws<ArgumentException>(() => AzureMachineLearningBinding.ConfigureWorkspace(Plan(), Policy(), Subscription, new(), storage, vault, telemetry));
            var workspace = new ML.Workspace("workspace", AzureMachineLearningBinding.ConfigureWorkspace(Plan(), Policy(), Subscription, WorkspaceArgs(), storage, vault, telemetry));
            AzureMachineLearningBinding.AttachWorkspace(Plan(), Policy(), Subscription, workspace, storage, vault, telemetry).WorkspaceId.Apply(v => { resolved = true; return v; });
        });
        Assert.False(resolved);
    }

    [Fact]
    public async Task Managed_registry_preserves_native_system_created_storage_and_acr()
    {
        var mode = AzureMachineLearningRegistryMode.Managed; var mocks = new Mocks();
        await Deployment.TestAsync(mocks, Options(), () =>
        {
            var args = new ML.RegistryArgs
            {
                ResourceGroupName = "test-rg", Location = "westus", PublicNetworkAccess = "Enabled",
                Identity = new ML.Inputs.ManagedServiceIdentityArgs { Type = "SystemAssigned" },
                RegionDetails = { new ML.Inputs.RegistryRegionArmDetailsArgs
                {
                    Location = "westus", AcrDetails = { new ML.Inputs.AcrDetailsArgs { SystemCreatedAcrAccount = new ML.Inputs.SystemCreatedAcrAccountArgs { AcrAccountName = "explicitacr", AcrAccountSku = "Premium" } } },
                    StorageAccountDetails = { new ML.Inputs.StorageAccountDetailsArgs { SystemCreatedStorageAccount = new ML.Inputs.SystemCreatedStorageAccountArgs { StorageAccountName = "explicitstorage", StorageAccountType = "Standard_LRS", AllowBlobPublicAccess = false, StorageAccountHnsEnabled = false } } }
                } }
            };
            Assert.Same(args, AzureMachineLearningRegistryBinding.ConfigureManaged(Plan(mode), RegistryPolicy(mode), Subscription, args));
            var registry = new ML.Registry("existing-registry", args);
            var result = AzureMachineLearningRegistryBinding.AttachManaged(Plan(mode), RegistryPolicy(mode), Subscription, registry);
            Assert.Same(registry, result.Registry); Assert.Null(result.Reference);
            result.RegistryId!.Apply(v => { Assert.Equal(RegistryId, v); return v; });
            result.Enabled.Apply(v => { Assert.True(v); return v; }); result.Name.Apply(v => { Assert.Equal("test-registry", v); return v; });
        });
        var native = Assert.Single(mocks.Resources, r => r.Type?.StartsWith("azure-native:") == true);
        Assert.Equal("existing-registry", native.Name);
        var json = JsonSerializer.Serialize(native.Inputs["regionDetails"]);
        Assert.Contains("explicitacr", json); Assert.Contains("Premium", json); Assert.Contains("explicitstorage", json); Assert.Contains("Standard_LRS", json);
        Assert.Contains("\"allowBlobPublicAccess\":false", json); Assert.Contains("\"storageAccountHnsEnabled\":false", json);
    }

    [Fact]
    public async Task Disabled_registry_registers_nothing_and_cannot_use_managed_path()
    {
        var mocks = new Mocks(); var mode = AzureMachineLearningRegistryMode.Disabled;
        await Deployment.TestAsync(mocks, Options(), () =>
        {
            var result = AzureMachineLearningRegistryBinding.Disabled(Plan(), RegistryPolicy(mode), Subscription);
            Assert.Null(result.Registry); Assert.Null(result.Reference); Assert.Null(result.RegistryId);
            result.Enabled.Apply(v => { Assert.False(v); return v; }); result.Name.Apply(v => { Assert.Empty(v); return v; });
            Assert.Throws<ArgumentException>(() => AzureMachineLearningRegistryBinding.ConfigureManaged(Plan(), RegistryPolicy(mode), Subscription, new()));
        });
        Assert.DoesNotContain(mocks.Resources, r => r.Type?.StartsWith("pulumi:pulumi:") != true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Shared_reference_is_strict_secret_and_never_locally_managed(bool enabled)
    {
        var mode = AzureMachineLearningRegistryMode.Referenced; var mocks = new Mocks(sharedEnabled: enabled);
        var plan = Plan(mode); Assert.True(plan.IsComplete, string.Join(";", plan.Diagnostics));
        await Deployment.TestAsync(mocks, Options(), async () =>
        {
            var reference = new StackReference("org/shared/production");
            var result = AzureMachineLearningRegistryBinding.AttachReference(plan, RegistryPolicy(mode), Subscription, reference);
            Assert.Same(reference, result.Reference); Assert.Null(result.Registry); Assert.Null(result.RegistryId);
            Assert.True(await Output.IsSecretAsync(result.Name));
            result.Enabled.Apply(v => { Assert.Equal(enabled, v); return v; });
            result.Name.Apply(v => { Assert.Equal(enabled ? "test-registry" : "", v); return v; });
            Assert.Throws<ArgumentException>(() => AzureMachineLearningRegistryBinding.ConfigureManaged(plan, RegistryPolicy(mode), Subscription, new()));
        });
        Assert.DoesNotContain(mocks.Resources, r => r.Type?.StartsWith("azure-native:") == true);
        Assert.Single(mocks.Resources, r => r.Type == "pulumi:pulumi:StackReference");
    }

    [Theory]
    [InlineData("missing-enabled")]
    [InlineData("missing-name")]
    [InlineData("string-enabled")]
    [InlineData("wrong-name")]
    [InlineData("disabled-name")]
    [InlineData("wrong-stack")]
    public async Task Shared_output_contract_fails_closed_without_values(string mismatch)
    {
        var mode = AzureMachineLearningRegistryMode.Referenced;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Deployment.TestAsync(new Mocks(mismatch), Options(), () =>
        {
            AzureMachineLearningRegistryBinding.AttachReference(Plan(mode), RegistryPolicy(mode), Subscription, new StackReference("org/shared/production")).Name.Apply(v => v);
        }));
        Assert.Contains("Shared ML registry", error.ToString()); Assert.DoesNotContain("private-provider-marker", error.ToString());
    }

    [Theory]
    [InlineData("registry-name")]
    [InlineData("registry-subscription")]
    [InlineData("registry-group")]
    [InlineData("registry-kind")]
    [InlineData("registry-network")]
    [InlineData("registry-identity")]
    public async Task Managed_registry_native_mismatches_fault_availability(string mismatch)
    {
        var mode = AzureMachineLearningRegistryMode.Managed;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Deployment.TestAsync(new Mocks(mismatch), Options(), () =>
        {
            var registry = new ML.Registry("registry", AzureMachineLearningRegistryBinding.ConfigureManaged(Plan(mode), RegistryPolicy(mode), Subscription,
                new() { ResourceGroupName = "test-rg", PublicNetworkAccess = "Enabled", Identity = new ML.Inputs.ManagedServiceIdentityArgs { Type = "SystemAssigned" } }));
            AzureMachineLearningRegistryBinding.AttachManaged(Plan(mode), RegistryPolicy(mode), Subscription, registry).Enabled.Apply(v => v);
        }));
        Assert.Contains(mismatch == "registry-network" ? "explicit Enabled or Disabled" : "canonical association", error.ToString());
        Assert.DoesNotContain("private-provider-marker", error.ToString());
    }

    [Fact]
    public async Task Unknown_reference_and_foreign_native_stack_cannot_become_validated_outputs()
    {
        var mode = AzureMachineLearningRegistryMode.Referenced; var resolved = false;
        await Deployment.TestAsync(new Mocks("unknown-reference"), Options(preview: true), () =>
        {
            AzureMachineLearningRegistryBinding.AttachReference(Plan(mode), RegistryPolicy(mode), Subscription,
                new StackReference("org/shared/production")).Name.Apply(v => { resolved = true; return v; });
        });
        Assert.False(resolved);
        var options = Options(); options.ProjectName = "other-project";
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Deployment.TestAsync(new Mocks(), options, () =>
        {
            var (storage, vault, telemetry) = Supports();
            _ = new ML.Workspace("workspace", AzureMachineLearningBinding.ConfigureWorkspace(Plan(), Policy(), Subscription, WorkspaceArgs(), storage, vault, telemetry));
        }));
        Assert.Contains("owner", error.ToString());
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("disabled-owner")]
    [InlineData("owner")]
    [InlineData("reference")]
    [InlineData("keys")]
    [InlineData("lifecycle")]
    [InlineData("alias")]
    public void Invalid_registry_ownership_and_reference_policy_is_rejected(string failure)
    {
        var mode = failure == "disabled-owner" ? AzureMachineLearningRegistryMode.Disabled : AzureMachineLearningRegistryMode.Referenced;
        var policy = RegistryPolicy(mode);
        policy = failure switch
        {
            "mode" => policy with { Mode = AzureMachineLearningRegistryMode.Unspecified },
            "disabled-owner" => policy with { LifecycleAuthority = Owner },
            "owner" => policy with { LifecycleAuthority = Owner },
            "reference" => policy with { ReferenceStack = "org/other/production" },
            "keys" => policy with { NameOutput = "enabled" }, _ => policy
        };
        Assert.NotEmpty(AzureMachineLearningRegistryBinding.Validate(Plan(failure == "lifecycle" ? AzureMachineLearningRegistryMode.Managed : mode, failure: failure == "alias" ? "registry-alias" : null), policy, Subscription));
    }

    [Fact]
    public void Portable_policies_and_cancellation_preserve_the_pre_registration_boundary()
    {
        var options = StrictDocumentJson.CreateOptions();
        var json = JsonSerializer.Serialize(Policy(), options);
        var restored = JsonSerializer.Deserialize<AzureMachineLearningWorkspacePolicy>(json, options)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored, options));
        Assert.Empty(AzureMachineLearningBinding.Validate(Plan(), restored, Subscription));
        foreach (var mode in new[] { AzureMachineLearningRegistryMode.Disabled, AzureMachineLearningRegistryMode.Managed, AzureMachineLearningRegistryMode.Referenced })
        {
            var encoded = JsonSerializer.Serialize(RegistryPolicy(mode), options);
            var decoded = JsonSerializer.Deserialize<AzureMachineLearningRegistryPolicy>(encoded, options)!;
            Assert.Equal(encoded, JsonSerializer.Serialize(decoded, options));
            Assert.Empty(AzureMachineLearningRegistryBinding.Validate(Plan(mode), decoded, Subscription));
        }
        Assert.Throws<OperationCanceledException>(() => AzureMachineLearningBinding.AttachWorkspace(Plan(), Policy(), Subscription, null!, null!, null!, null!, new(true)));
        Assert.Throws<OperationCanceledException>(() => AzureMachineLearningRegistryBinding.AttachManaged(Plan(), RegistryPolicy(AzureMachineLearningRegistryMode.Managed), Subscription, null!, new(true)));
        Assert.Throws<OperationCanceledException>(() => AzureMachineLearningRegistryBinding.AttachReference(Plan(), RegistryPolicy(AzureMachineLearningRegistryMode.Referenced), Subscription, null!, new(true)));
    }

    static (StorageAccount, Vault, Component) Supports() =>
        (new StorageAccount("storage", new() { AccountName = "teststorage", ResourceGroupName = "test-rg", Kind = "StorageV2", Sku = new global::Pulumi.AzureNative.Storage.Inputs.SkuArgs { Name = "Standard_LRS" } }),
         new Vault("vault", new() { VaultName = "test-vault", ResourceGroupName = "test-rg", Properties = new global::Pulumi.AzureNative.KeyVault.Inputs.VaultPropertiesArgs { TenantId = Subscription.ToString(), Sku = new global::Pulumi.AzureNative.KeyVault.Inputs.SkuArgs { Name = global::Pulumi.AzureNative.KeyVault.SkuName.Standard, Family = "A" } } }),
         new Component("telemetry", new() { ResourceName = "test-insights", ResourceGroupName = "test-rg", ApplicationType = "web", Kind = "web" }));
    static ML.WorkspaceArgs WorkspaceArgs(string network = "Enabled") => new()
    {
        ResourceGroupName = "test-rg", Location = "westus", PublicNetworkAccess = network, SystemDatastoresAuthMode = "Identity",
        Identity = new ML.Inputs.ManagedServiceIdentityArgs { Type = "SystemAssigned" }, FriendlyName = "native friendly name", HbiWorkspace = false,
        Tags = { ["policy"] = "native" }
    };

    static InfrastructureTargetDeploymentPlan Plan(AzureMachineLearningRegistryMode mode = AzureMachineLearningRegistryMode.Disabled, string? failure = null)
    {
        var nodes = new List<(InfrastructureNodeId Id, string Facility, string Physical)>
        {
            (Workspace, AzureMachineLearningBinding.WorkspaceFacility, "azure/machine-learning/workspaces/test-workspace"),
            (Storage, "azure/blob-storage", "azure/storage/accounts/teststorage/blob-services/default/containers/data"),
            (Vault, "azure/key-vault", failure == "physical" ? "wrong" : "azure/key-vault/vaults/test-vault"),
            (Telemetry, "azure/application-insights", "azure/application-insights/components/test-insights")
        };
        if (mode != AzureMachineLearningRegistryMode.Disabled) nodes.Add((Registry, AzureMachineLearningBinding.RegistryFacility, "azure/machine-learning/registries/test-registry"));
        if (failure == "alias") nodes.Add((new("resources/alias"), "azure/key-vault", "azure/key-vault/vaults/test-vault"));
        if (failure == "registry-alias") nodes.Add((new("resources/alias"), AzureMachineLearningBinding.RegistryFacility, "azure/machine-learning/registries/test-registry"));
        var semantic = Infrastructure.Define(new("test/ml"), new("1"), new("test/bindings/v1"), infra =>
        {
            foreach (var node in nodes)
            {
                var resource = infra.Resource(node.Id).Requires(new(node.Facility + "/capability"));
                if (node.Id == Registry && mode == AzureMachineLearningRegistryMode.Referenced) resource.External();
                else resource.Persistent();
                if (node.Id == Workspace && failure != "dependency")
                    foreach (var support in new[] { Storage, Vault, Telemetry }) resource.RequiresReady(support);
            }
        });
        var facilities = InfrastructureTargetFacilities.Define(new("test/ml/facilities"), new("test/ml/capabilities"), new(AzureMachineLearningBinding.Target), new("test/production"),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion], facility =>
            {
                foreach (var name in nodes.Select(n => n.Facility).Distinct())
                    facility.Resource(new(name)).Provides(new(new(name + "/evidence"), new(name + "/capability"), CapabilityRealizationKind.Native, sourceReferences: [Source]));
            });
        var manifest = InfrastructureTargetDeployments.Define(new("test/ml/deployment"), semantic.Definition, facilities, deployment =>
        {
            foreach (var node in nodes)
                if (node.Id == Registry && mode == AzureMachineLearningRegistryMode.Referenced)
                    deployment.Resource(node.Id, new(node.Facility), new(node.Physical), SharedOwner, [Source]);
                else deployment.Resource(node.Id, new(node.Facility), new(node.Physical), failure == "owner" && node.Id == Vault ? SharedOwner : Owner, [Source]);
        });
        return InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
    }

    sealed class Mocks(string? mismatch = null, bool sharedEnabled = true) : IMocks
    {
        public ConcurrentBag<MockResourceArgs> Resources { get; } = [];
        public Task<object> CallAsync(MockCallArgs args) => throw new InvalidOperationException("No provider invokes are permitted.");
        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            Resources.Add(args); var state = args.Inputs.ToDictionary();
            if (args.Type == "pulumi:pulumi:StackReference")
            {
                var outputs = new Dictionary<string, object> { ["enabled"] = sharedEnabled, ["name"] = sharedEnabled ? "test-registry" : "" };
                if (mismatch == "missing-enabled") outputs.Remove("enabled");
                if (mismatch == "missing-name") outputs.Remove("name");
                if (mismatch == "string-enabled") outputs["enabled"] = "true";
                if (mismatch == "wrong-name") outputs["name"] = "private-provider-marker";
                if (mismatch == "disabled-name") outputs["enabled"] = false;
                if (mismatch == "wrong-stack") state["name"] = "org/other/production";
                if (mismatch != "unknown-reference") state["outputs"] = outputs; state["secretOutputNames"] = new[] { "name" };
                return Task.FromResult<(string?, object)>((args.Name, state));
            }
            var (kind, property, suffix) = args.Type switch
            {
                "azure-native:machinelearningservices:Workspace" => ("workspace", "workspaceName", "Microsoft.MachineLearningServices/workspaces/test-workspace"),
                "azure-native:machinelearningservices:Registry" => ("registry", "registryName", "Microsoft.MachineLearningServices/registries/test-registry"),
                "azure-native:storage:StorageAccount" => ("storage", "accountName", "Microsoft.Storage/storageAccounts/teststorage"),
                "azure-native:keyvault:Vault" => ("vault", "vaultName", "Microsoft.KeyVault/vaults/test-vault"),
                "azure-native:applicationinsights:Component" => ("telemetry", "resourceName", "Microsoft.Insights/components/test-insights"),
                _ => ("other", "", "")
            };
            var id = kind == "other" ? args.Name + "-id" : Prefix + suffix;
            if (kind != "other") state["name"] = mismatch == kind + "-name" ? "private-provider-marker" : args.Inputs[property];
            if (mismatch == kind + "-subscription") id = id.Replace(Subscription.ToString(), Guid.NewGuid().ToString());
            if (mismatch == kind + "-group") id = id.Replace("test-rg", "other-rg");
            if (mismatch == kind + "-kind") id = id.Replace("/providers/", "/wrong/");
            if (kind == "registry")
            {
                if (mismatch == "registry-network") state["publicNetworkAccess"] = "unexpected";
                if (mismatch == "registry-identity") state["identity"] = new Dictionary<string, object> { ["type"] = "None" };
            }
            if (kind == "workspace")
            {
                state["storageAccount"] = Output.CreateSecret((string)state["storageAccount"]);
                if (mismatch == "unknown") state.Remove("storageAccount");
                if (mismatch == "storage-link") state["storageAccount"] = "private-provider-marker";
                if (mismatch == "vault-link") state["keyVault"] = "private-provider-marker";
                if (mismatch == "telemetry-link") state["applicationInsights"] = "private-provider-marker";
                if (mismatch == "workspace-network") state["publicNetworkAccess"] = "unexpected";
                if (mismatch == "workspace-auth") state["systemDatastoresAuthMode"] = "AccessKey";
                if (mismatch == "workspace-identity") state["identity"] = new Dictionary<string, object> { ["type"] = "None" };
            }
            return Task.FromResult<(string?, object)>((id, state));
        }
    }
}
