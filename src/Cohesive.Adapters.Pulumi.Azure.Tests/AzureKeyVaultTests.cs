using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Adapters.Pulumi.Azure;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.KeyVault;
using Pulumi.AzureNative.KeyVault.Inputs;
using Pulumi.Testing;

namespace Cohesive.Adapters.Pulumi.Azure.Tests;

public sealed class AzureKeyVaultTests
{
    static readonly Guid Subscription = Guid.Parse("b6708815-d5b5-4070-af5b-675272a80b77");
    static readonly Guid Tenant = Guid.Parse("adc0f122-9477-47b8-b08a-2b2f1b6d0749");
    static readonly SourceReference Source = SourceReference.Create("test", "key-vault-policy");
    static readonly InfrastructureNodeId Worker = new("workloads/worker");
    static readonly InfrastructureNodeId Secrets = new("resources/secrets");
    static readonly InfrastructureBindingId Binding = new("bindings/worker/secrets");
    static readonly InfrastructureBindingContractId Contract = new("contracts/secret-read");
    static readonly InfrastructureLifecycleAuthorityId Authority = new("pulumi/test/production");
    const string Physical = "azure/key-vault/vaults/test-vault";
    const string VaultId = "/subscriptions/b6708815-d5b5-4070-af5b-675272a80b77/resourceGroups/test-rg/providers/Microsoft.KeyVault/vaults/test-vault";
    const string ActualUri = "https://actual-vault.vault.usgovcloudapi.net/";

    static AzureKeyVaultPolicy Policy() => new()
    {
        Resource = Secrets, SecretReadContract = Contract, LifecycleAuthority = Authority,
        SubscriptionId = Subscription, TenantId = Tenant, SourceReferences = [Source],
        Access = [new(Binding, AzureKeyVaultAccessAction.AssignSecretsUser, "Approved workload reader.", [Source])]

    };

    [Theory]
    [InlineData("Enabled", 7)]
    [InlineData("Disabled", 90)]
    public async Task Preserves_vault_identity_policy_dependencies_and_exact_explicit_grant(string network, int retention)
    {
        var plan = Plan();
        var policy = Policy();
        Assert.True(plan.IsComplete);
        var mocks = new Mocks();
        var checkedDependencies = false;
        await Deployment.TestAsync(mocks, new TestOptions { IsPreview = false }, async () =>
        {
            var group = new ComponentResource("test:index:Group", "existing-group");
            ComponentResource? parent = null;
            parent = new ComponentResource("test:index:Parent", "existing-parent", new ComponentResourceOptions
            {
                ResourceTransformations = { args =>
                {
                    if (args.Resource is Vault)
                    {
                        Assert.Same(parent, args.Options.Parent);
                        ((Output<ImmutableArray<Resource>>)args.Options.DependsOn).Apply(dependencies =>
                        { Assert.Contains(group, dependencies); checkedDependencies = true; return dependencies; });
                    }
                    return null;
                } }
            });
            var provider = new global::Pulumi.AzureNative.Provider("existing-provider", new()
            { SubscriptionId = Subscription.ToString("D"), TenantId = Tenant.ToString("D") });
            var vault = NativeVault(plan, policy, network, retention, new() { Provider = provider, Parent = parent, DependsOn = { group } });
            var resources = AzureKeyVaultBinding.Attach(plan, policy, Subscription, Tenant, vault);
            Assert.Same(plan, resources.Deployment);
            Assert.Same(policy, resources.Policy);
            Assert.Equal(Binding, Assert.Single(resources.SecretBindings).Id);
            resources.VaultUri.Apply(uri => { Assert.Equal(ActualUri, uri); return uri; });
            Assert.False(await Output.IsSecretAsync(resources.VaultUri));
            var inputs = resources.AccessGrant(Binding, Subscription, Tenant, Output.CreateSecret("principal-id"),
                Guid.Parse("9da6bc9c-60e4-4e3c-99cf-2fe700670ded"));
            Assert.True(await Output.IsSecretAsync((Output<string>)inputs.PrincipalId));
            _ = new RoleAssignment("existing-reader-grant", inputs, new() { Parent = parent, Provider = provider });
            Assert.Throws<ArgumentException>(() => resources.AccessGrant(Binding, Guid.NewGuid(), Tenant, "principal", Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => resources.AccessGrant(Binding, Subscription, Guid.NewGuid(), "principal", Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => resources.AccessGrant(new("unknown"), Subscription, Tenant, "principal", Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => resources.AccessGrant(Binding, Subscription, Tenant, "principal", Guid.Empty));
            Assert.Throws<ArgumentNullException>(() => resources.AccessGrant(Binding, Subscription, Tenant, null!, Guid.NewGuid()));
        });
        Assert.True(checkedDependencies);
        var vault = Assert.Single(mocks.Resources, r => r.Type == "azure-native:keyvault:Vault");
        Assert.Equal("existing-vault", vault.Name);
        Assert.Equal("test-vault", vault.Inputs["vaultName"]);
        Assert.Equal("test-rg", vault.Inputs["resourceGroupName"]);
        Assert.Contains("existing-provider", vault.Provider);
        var properties = Assert.IsAssignableFrom<ImmutableDictionary<string, object>>(vault.Inputs["properties"]);
        Assert.Equal(Tenant.ToString("D"), properties["tenantId"]);
        Assert.Equal(true, properties["enableRbacAuthorization"]);
        Assert.Equal(true, properties["enableSoftDelete"]);
        Assert.Equal(retention, Convert.ToInt32(properties["softDeleteRetentionInDays"]));
        Assert.Equal(network, properties["publicNetworkAccess"]);
        foreach (var property in new[] { "enabledForDeployment", "enabledForDiskEncryption", "enabledForTemplateDeployment" })
            Assert.Equal(false, properties[property]);
        Assert.DoesNotContain("accessPolicies", properties.Keys);
        Assert.DoesNotContain("enablePurgeProtection", properties.Keys);
        var sku = Assert.IsAssignableFrom<ImmutableDictionary<string, object>>(properties["sku"]);
        Assert.Equal("standard", sku["name"]);
        Assert.Equal("A", sku["family"]);
        var grant = Assert.Single(mocks.Resources, r => r.Type == "azure-native:authorization:RoleAssignment");
        Assert.Equal(VaultId, grant.Inputs["scope"]);
        Assert.Equal($"/subscriptions/{Subscription:D}/providers/Microsoft.Authorization/roleDefinitions/{AzureKeyVaultBinding.SecretsUserRole}", grant.Inputs["roleDefinitionId"]);
        Assert.Equal("9da6bc9c-60e4-4e3c-99cf-2fe700670ded", grant.Inputs["roleAssignmentName"]);
        Assert.Equal(vault.Provider, grant.Provider);
        Assert.DoesNotContain(mocks.Resources, r => r.Type?.Contains(":Secret", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Provider_secret_classification_survives_URI_projection()
    {
        await Deployment.TestAsync(new Mocks(secretProperties: true), new TestOptions(), async () =>
        {
            var resources = AzureKeyVaultBinding.Attach(Plan(), Policy(), Subscription, Tenant, NativeVault(Plan(), Policy()));
            Assert.True(await Output.IsSecretAsync(resources.VaultUri));
        });
    }

    [Theory]
    [InlineData("subscription")]
    [InlineData("tenant")]
    [InlineData("lifecycle")]
    [InlineData("provenance")]
    [InlineData("secret-contract")]
    [InlineData("binding")]
    [InlineData("physical-identity")]
    [InlineData("target")]
    [InlineData("incomplete")]
    [InlineData("alias")]
    [InlineData("access")]
    [InlineData("access-evidence")]
    public async Task Invalid_semantic_selection_fails_before_native_construction(string failure)
    {
        var policy = failure switch
        {
            "subscription" => Policy() with { SubscriptionId = Guid.NewGuid() },
            "tenant" => Policy() with { TenantId = Guid.NewGuid() },
            "lifecycle" => Policy() with { LifecycleAuthority = new("pulumi/other/stack") },
            "provenance" => Policy() with { SourceReferences = [] },
            "secret-contract" => Policy() with { SecretReadContract = default },
            "binding" => Policy() with { SecretReadContract = new("unsupported/contract") },
            "access" => Policy() with { Access = [] },
            "access-evidence" => Policy() with { Access = [new(Binding, AzureKeyVaultAccessAction.AssignSecretsUser, "", [])] },
            _ => Policy()
        };
        var plan = Plan(physical: failure == "physical-identity" ? "azure/key-vault/wrong" : Physical,
            target: failure == "target" ? "other-target" : AzureKeyVaultBinding.Target,
            incomplete: failure == "incomplete", alias: failure == "alias");
        var mocks = new Mocks();
        await Deployment.TestAsync(mocks, new TestOptions(), () =>
        {
            var error = Assert.Throws<AzureKeyVaultValidationException>(() =>
                AzureKeyVaultBinding.VaultName(plan, policy, Subscription, Tenant));
            Assert.Contains(error.Diagnostics, d => d.Code == "azure.key-vault." + failure);
            Assert.All(error.Diagnostics, d => Assert.NotNull(d.Evidence));
        });
        Assert.DoesNotContain(mocks.Resources, r => r.Type?.StartsWith("pulumi:pulumi:") != true);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("1-vault")]
    [InlineData("vault-")]
    [InlineData("bad--vault")]
    [InlineData("a-name-that-is-too-long-for-a-vault")]
    public void Invalid_physical_names_are_rejected(string name) => Assert.Contains(
        AzureKeyVaultBinding.Validate(Plan(physical: "azure/key-vault/vaults/" + name), Policy(), Subscription, Tenant),
        d => d.Code == "azure.key-vault.physical-identity");

    [Fact]
    public void Invalid_or_ambiguous_access_decisions_cannot_silently_grant()
    {
        var decision = Policy().Access[0];
        ImmutableArray<AzureKeyVaultBindingAccess>[] invalid = [default, [null!], [decision, decision],
            [decision with { Binding = new("unknown") }], [decision with { Action = default }],
            [decision with { Action = (AzureKeyVaultAccessAction)999 }]];
        foreach (var access in invalid)
            Assert.Contains(AzureKeyVaultBinding.Validate(Plan(), Policy() with { Access = access }, Subscription, Tenant),
                d => d.Code == "azure.key-vault.access");
        Assert.Contains(AzureKeyVaultBinding.Validate(Plan(), Policy(), Subscription, Guid.Empty), d => d.Code == "azure.key-vault.tenant");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deliberate_no_grant_and_nonparticipation_never_authorize_access(bool nonparticipating)
    {
        var policy = Policy() with { Access = nonparticipating ? [] :
            [new(Binding, AzureKeyVaultAccessAction.NoManagedGrant, "Preserve existing absence; reconcile access separately.", [Source])] };
        var plan = Plan(nonparticipating: nonparticipating);
        var mocks = new Mocks();
        await Deployment.TestAsync(mocks, new TestOptions(), () =>
        {
            var resources = AzureKeyVaultBinding.Attach(plan, policy, Subscription, Tenant, NativeVault(plan, policy));
            Assert.Equal(nonparticipating ? 0 : 1, resources.SecretBindings.Length);
            Assert.Throws<ArgumentException>(() => resources.AccessGrant(Binding, Subscription, Tenant, "principal", Guid.NewGuid()));
        });
        Assert.Single(mocks.Resources, r => r.Type == "azure-native:keyvault:Vault");
        Assert.DoesNotContain(mocks.Resources, r => r.Type == "azure-native:authorization:RoleAssignment");
        if (nonparticipating)
            Assert.Contains(AzureKeyVaultBinding.Validate(plan, Policy(), Subscription, Tenant), d => d.Code == "azure.key-vault.access");
    }

    [Fact]
    public void External_ownership_and_cancellation_are_rejected()
    {
        Assert.Contains(AzureKeyVaultBinding.Validate(Plan(external: true), Policy(), Subscription, Tenant),
            d => d.Code == "azure.key-vault.lifecycle");
        Assert.Throws<OperationCanceledException>(() => AzureKeyVaultBinding.Attach(Plan(), Policy(), Subscription, Tenant, null!,
            cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public void Strict_policy_roundtrip_rejects_omitted_actions_and_unsupported_secret_values()
    {
        var options = StrictDocumentJson.CreateOptions();
        var json = JsonSerializer.Serialize(Policy(), options);
        var restored = JsonSerializer.Deserialize<AzureKeyVaultPolicy>(json, options)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored, options));
        Assert.Empty(AzureKeyVaultBinding.Validate(Plan(), restored, Subscription, Tenant));
        var omitted = JsonNode.Parse(json)!.AsObject();
        omitted["access"]![0]!.AsObject().Remove("action");
        var noAction = JsonSerializer.Deserialize<AzureKeyVaultPolicy>(omitted.ToJsonString(), options)!;
        Assert.Contains(AzureKeyVaultBinding.Validate(Plan(), noAction, Subscription, Tenant), d => d.Code == "azure.key-vault.access");
        omitted["secretValue"] = "must-not-be-consumed";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AzureKeyVaultPolicy>(omitted.ToJsonString(), options));
        var diagnostics = AzureKeyVaultBinding.Validate(Plan(), Policy() with
        { Access = [new(Binding, AzureKeyVaultAccessAction.Unspecified, "sensitive-rationale-marker", [Source])] }, Subscription, Tenant);
        Assert.DoesNotContain("sensitive-rationale-marker", JsonSerializer.Serialize(diagnostics, options));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("subscription")]
    [InlineData("tenant")]
    [InlineData("id-name")]
    [InlineData("id-kind")]
    [InlineData("rbac")]
    public async Task Resolved_native_identity_and_grant_mode_must_match_the_association(string mismatch)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Deployment.TestAsync(new Mocks(mismatch: mismatch),
            new TestOptions { IsPreview = false }, () =>
            {
                var resources = AzureKeyVaultBinding.Attach(Plan(), Policy(), Subscription, Tenant, NativeVault(Plan(), Policy()));
                _ = new RoleAssignment("reader", resources.AccessGrant(Binding, Subscription, Tenant, "principal", Guid.NewGuid()));
            }));
        Assert.Contains(mismatch == "rbac" ? "RBAC authorization" : "canonical association", error.ToString());
    }

    [Fact]
    public async Task Native_configuration_is_unrestricted_when_no_managed_RBAC_grant_is_requested()
    {
        var policy = Policy() with { Access = [new(Binding, AzureKeyVaultAccessAction.NoManagedGrant,
            "Access-policy management is retained by the native host.", [Source])] };
        var mocks = new Mocks();
        await Deployment.TestAsync(mocks, new TestOptions { IsPreview = false }, () =>
        {
            var vault = new Vault("native-vault", new()
            {
                VaultName = AzureKeyVaultBinding.VaultName(Plan(), policy, Subscription, Tenant),
                ResourceGroupName = "native-group", Location = "westus",
                Properties = new VaultPropertiesArgs
                {
                    TenantId = Tenant.ToString("D"), EnableRbacAuthorization = false,
                    EnableSoftDelete = true, EnablePurgeProtection = true, SoftDeleteRetentionInDays = 30,
                    PublicNetworkAccess = "Disabled", Sku = new SkuArgs { Family = "A", Name = SkuName.Premium },
                    AccessPolicies = []
                }
            }, new() { Protect = true });
            var resources = AzureKeyVaultBinding.Attach(Plan(), policy, Subscription, Tenant, vault);
            Assert.Same(vault, resources.Vault);
            resources.VaultId.Apply(id => { Assert.Equal(VaultId, id); return id; });
        });
        var resource = Assert.Single(mocks.Resources, r => r.Type == "azure-native:keyvault:Vault");
        Assert.Equal("native-group", resource.Inputs["resourceGroupName"]);
        var properties = Assert.IsAssignableFrom<ImmutableDictionary<string, object>>(resource.Inputs["properties"]);
        Assert.Equal(true, properties["enablePurgeProtection"]);
        Assert.Equal(false, properties["enableRbacAuthorization"]);
        Assert.Equal("premium", ((ImmutableDictionary<string, object>)properties["sku"])["name"]);
        Assert.DoesNotContain(mocks.Resources, r => r.Type == "azure-native:authorization:RoleAssignment");
    }

    static Vault NativeVault(InfrastructureTargetDeploymentPlan plan, AzureKeyVaultPolicy policy,
        string network = "Enabled", int retention = 7, CustomResourceOptions? options = null) => new("existing-vault", new()
    {
        VaultName = AzureKeyVaultBinding.VaultName(plan, policy, Subscription, Tenant),
        ResourceGroupName = "test-rg", Location = "westus",
        Properties = new VaultPropertiesArgs
        {
            TenantId = Tenant.ToString("D"), EnableRbacAuthorization = true, EnableSoftDelete = true,
            SoftDeleteRetentionInDays = retention, PublicNetworkAccess = network,
            EnabledForDeployment = false, EnabledForDiskEncryption = false, EnabledForTemplateDeployment = false,
            Sku = new SkuArgs { Family = "A", Name = SkuName.Standard }
        }
    }, options);

    static InfrastructureTargetDeploymentPlan Plan(string physical = Physical,
        string target = AzureKeyVaultBinding.Target, bool incomplete = false,
        bool nonparticipating = false, bool external = false, bool alias = false)
    {
        InfrastructureCapabilityId execution = new("test/execution");
        InfrastructureCapabilityId retrieval = new("test/secret-read");
        var semantic = Infrastructure.Define(new("test/vault"), new("1"), new("test/bindings/v1"), infra =>
        {
            var contract = infra.Contract(Contract, new("test/rule")).Requires(retrieval).SourcedFrom(Source.Value);
            var worker = infra.Workload(Worker).Requires(execution);
            if (incomplete) worker.Requires(new("test/unsupported"));
            var vault = infra.Resource(Secrets).Requires(retrieval);
            if (external) vault.External(); else vault.Persistent();
            if (alias) infra.Resource(new("resources/other")).Persistent().Requires(retrieval);
            infra.Bind(Binding, worker).To(vault).As(contract);
        });
        var facilities = InfrastructureTargetFacilities.Define(new("test/facilities/v1"), new("test/capabilities/v1"),
            new(target), new("test/production"), [InfrastructureDefinitionDocument.CurrentSchemaVersion], facility =>
            {
                facility.Workload(new("test/worker")).Provides(new(new("test/execution/evidence"), execution,
                    CapabilityRealizationKind.Native, sourceReferences: [Source]));
                facility.Resource(new(AzureKeyVaultBinding.Facility)).Provides(new(new("test/secrets/evidence"), retrieval,
                    CapabilityRealizationKind.Native, sourceReferences: [Source]));
            });
        var manifest = InfrastructureTargetDeployments.Define(new("test/deployment/v1"), semantic.Definition, facilities,
            deployment =>
            {
                if (nonparticipating) deployment.NonParticipatingWorkload(Worker, "No reader in this environment.", [Source.Value]);
                else deployment.Workload(Worker, new("test/worker"), new("test/workers/worker"), [Source]);
                deployment.Resource(Secrets, new(AzureKeyVaultBinding.Facility), new(physical), Authority, [Source]);
                if (alias) deployment.Resource(new("resources/other"), new(AzureKeyVaultBinding.Facility),
                    new(physical.ToUpperInvariant()), Authority, [Source]);
            });
        return InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
    }

    sealed class Mocks(bool secretProperties = false, string? mismatch = null) : IMocks
    {
        public ConcurrentBag<MockResourceArgs> Resources { get; } = [];
        public Task<object> CallAsync(MockCallArgs args) => throw new InvalidOperationException("Vault construction must never invoke a provider or retrieve secrets.");
        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            Resources.Add(args);
            var state = args.Inputs.ToDictionary();
            if (args.Type == "azure-native:keyvault:Vault")
            {
                state["name"] = mismatch == "name" ? "wrong-vault" : args.Inputs["vaultName"];
                var properties = ((ImmutableDictionary<string, object>)args.Inputs["properties"]).ToDictionary();
                properties["vaultUri"] = ActualUri;
                if (mismatch == "tenant") properties["tenantId"] = Guid.NewGuid().ToString("D");
                if (mismatch == "rbac") properties["enableRbacAuthorization"] = false;
                state["properties"] = secretProperties ? Output.CreateSecret(properties) : properties;
            }
            var id = mismatch switch
            {
                "subscription" => VaultId.Replace(Subscription.ToString("D"), Guid.NewGuid().ToString("D")),
                "id-name" => VaultId.Replace("test-vault", "wrong-vault"),
                "id-kind" => VaultId.Replace("Microsoft.KeyVault", "Other.Provider"),
                _ => VaultId
            };
            return Task.FromResult<(string?, object)>((args.Type == "azure-native:keyvault:Vault" ? id : args.Name + "-id", state));
        }
    }
}
