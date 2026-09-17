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
        SubscriptionId = Subscription, TenantId = Tenant, ResourceGroupName = "test-rg", Location = "westus",
        VaultName = "existing-vault", AuthorizationMode = "Rbac", SoftDeleteRetentionInDays = 7,
        PublicNetworkAccess = "Enabled", SourceReferences = [Source],
        Access = [new(Binding, AzureKeyVaultAccessAction.AssignSecretsUser, "Approved workload reader.", [Source])],
        Tags = ImmutableSortedDictionary<string, string>.Empty.Add("environment", "test")
    };

    [Theory]
    [InlineData("Enabled", 7)]
    [InlineData("Disabled", 90)]
    public async Task Preserves_vault_identity_policy_dependencies_and_exact_explicit_grant(string network, int retention)
    {
        var plan = Plan();
        var policy = Policy() with { PublicNetworkAccess = network, SoftDeleteRetentionInDays = retention };
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
            var resources = AzureKeyVaultConstruction.Register(plan, policy, Subscription, Tenant, provider, parent, group);
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
        Assert.Equal($"/subscriptions/{Subscription:D}/providers/Microsoft.Authorization/roleDefinitions/{AzureKeyVaultConstruction.SecretsUserRole}", grant.Inputs["roleDefinitionId"]);
        Assert.Equal("9da6bc9c-60e4-4e3c-99cf-2fe700670ded", grant.Inputs["roleAssignmentName"]);
        Assert.Equal(vault.Provider, grant.Provider);
        Assert.DoesNotContain(mocks.Resources, r => r.Type?.Contains(":Secret", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Provider_secret_classification_survives_URI_projection()
    {
        await Deployment.TestAsync(new Mocks(secretProperties: true), new TestOptions(), async () =>
        {
            var resources = AzureKeyVaultConstruction.Register(Plan(), Policy(), Subscription, Tenant);
            Assert.True(await Output.IsSecretAsync(resources.VaultUri));
        });
    }

    [Theory]
    [InlineData("subscription")]
    [InlineData("tenant")]
    [InlineData("lifecycle")]
    [InlineData("resource-group")]
    [InlineData("location")]
    [InlineData("logical-name")]
    [InlineData("authorization")]
    [InlineData("retention")]
    [InlineData("network")]
    [InlineData("tags")]
    [InlineData("provenance")]
    [InlineData("secret-contract")]
    [InlineData("binding")]
    [InlineData("physical-identity")]
    [InlineData("target")]
    [InlineData("incomplete")]
    [InlineData("alias")]
    [InlineData("access")]
    [InlineData("access-evidence")]
    public async Task Invalid_inputs_fail_before_any_registration(string failure)
    {
        var policy = failure switch
        {
            "subscription" => Policy() with { SubscriptionId = Guid.NewGuid() },
            "tenant" => Policy() with { TenantId = Guid.NewGuid() },
            "lifecycle" => Policy() with { LifecycleAuthority = new("pulumi/other/stack") },
            "resource-group" => Policy() with { ResourceGroupName = "invalid/rg" },
            "location" => Policy() with { Location = " " },
            "logical-name" => Policy() with { VaultName = " " },
            "authorization" => Policy() with { AuthorizationMode = "AccessPolicies" },
            "retention" => Policy() with { SoftDeleteRetentionInDays = 91 },
            "network" => Policy() with { PublicNetworkAccess = "" },
            "tags" => Policy() with { Tags = ImmutableSortedDictionary<string, string>.Empty.Add("invalid/key", "value") },
            "provenance" => Policy() with { SourceReferences = [] },
            "secret-contract" => Policy() with { SecretReadContract = default },
            "binding" => Policy() with { SecretReadContract = new("unsupported/contract") },
            "access" => Policy() with { Access = [] },
            "access-evidence" => Policy() with { Access = [new(Binding, AzureKeyVaultAccessAction.AssignSecretsUser, "", [])] },
            _ => Policy()
        };
        var plan = Plan(physical: failure == "physical-identity" ? "azure/key-vault/wrong" : Physical,
            target: failure == "target" ? "other-target" : AzureKeyVaultConstruction.Target,
            incomplete: failure == "incomplete", alias: failure == "alias");
        var mocks = new Mocks();
        await Deployment.TestAsync(mocks, new TestOptions(), () =>
        {
            var error = Assert.Throws<AzureKeyVaultValidationException>(() =>
                AzureKeyVaultConstruction.Register(plan, policy, Subscription, Tenant));
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
        AzureKeyVaultConstruction.Validate(Plan(physical: "azure/key-vault/vaults/" + name), Policy(), Subscription, Tenant),
        d => d.Code == "azure.key-vault.physical-identity");

    [Fact]
    public void Invalid_or_ambiguous_access_decisions_cannot_silently_grant()
    {
        var decision = Policy().Access[0];
        ImmutableArray<AzureKeyVaultBindingAccess>[] invalid = [default, [null!], [decision, decision],
            [decision with { Binding = new("unknown") }], [decision with { Action = default }],
            [decision with { Action = (AzureKeyVaultAccessAction)999 }]];
        foreach (var access in invalid)
            Assert.Contains(AzureKeyVaultConstruction.Validate(Plan(), Policy() with { Access = access }, Subscription, Tenant),
                d => d.Code == "azure.key-vault.access");
        Assert.Contains(AzureKeyVaultConstruction.Validate(Plan(), Policy() with { SoftDeleteRetentionInDays = 6 }, Subscription, Tenant),
            d => d.Code == "azure.key-vault.retention");
        Assert.Contains(AzureKeyVaultConstruction.Validate(Plan(), Policy(), Subscription, Guid.Empty), d => d.Code == "azure.key-vault.tenant");
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
            var resources = AzureKeyVaultConstruction.Register(plan, policy, Subscription, Tenant);
            Assert.Equal(nonparticipating ? 0 : 1, resources.SecretBindings.Length);
            Assert.Throws<ArgumentException>(() => resources.AccessGrant(Binding, Subscription, Tenant, "principal", Guid.NewGuid()));
        });
        Assert.Single(mocks.Resources, r => r.Type == "azure-native:keyvault:Vault");
        Assert.DoesNotContain(mocks.Resources, r => r.Type == "azure-native:authorization:RoleAssignment");
        if (nonparticipating)
            Assert.Contains(AzureKeyVaultConstruction.Validate(plan, Policy(), Subscription, Tenant), d => d.Code == "azure.key-vault.access");
    }

    [Fact]
    public void External_ownership_and_cancellation_are_rejected()
    {
        Assert.Contains(AzureKeyVaultConstruction.Validate(Plan(external: true), Policy(), Subscription, Tenant),
            d => d.Code == "azure.key-vault.lifecycle");
        Assert.Throws<OperationCanceledException>(() => AzureKeyVaultConstruction.Register(Plan(), Policy(), Subscription, Tenant,
            cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public void Strict_policy_roundtrip_rejects_omitted_actions_and_unsupported_secret_values()
    {
        var options = StrictDocumentJson.CreateOptions();
        var json = JsonSerializer.Serialize(Policy(), options);
        var restored = JsonSerializer.Deserialize<AzureKeyVaultPolicy>(json, options)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored, options));
        Assert.Empty(AzureKeyVaultConstruction.Validate(Plan(), restored, Subscription, Tenant));
        var omitted = JsonNode.Parse(json)!.AsObject();
        omitted["access"]![0]!.AsObject().Remove("action");
        var noAction = JsonSerializer.Deserialize<AzureKeyVaultPolicy>(omitted.ToJsonString(), options)!;
        Assert.Contains(AzureKeyVaultConstruction.Validate(Plan(), noAction, Subscription, Tenant), d => d.Code == "azure.key-vault.access");
        omitted["secretValue"] = "must-not-be-consumed";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AzureKeyVaultPolicy>(omitted.ToJsonString(), options));
        var diagnostics = AzureKeyVaultConstruction.Validate(Plan(), Policy() with
        { Access = [new(Binding, AzureKeyVaultAccessAction.Unspecified, "sensitive-rationale-marker", [Source])] }, Subscription, Tenant);
        Assert.DoesNotContain("sensitive-rationale-marker", JsonSerializer.Serialize(diagnostics, options));
    }

    static InfrastructureTargetDeploymentPlan Plan(string physical = Physical,
        string target = AzureKeyVaultConstruction.Target, bool incomplete = false,
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
                facility.Resource(new(AzureKeyVaultConstruction.Facility)).Provides(new(new("test/secrets/evidence"), retrieval,
                    CapabilityRealizationKind.Native, sourceReferences: [Source]));
            });
        var manifest = InfrastructureTargetDeployments.Define(new("test/deployment/v1"), semantic.Definition, facilities,
            deployment =>
            {
                if (nonparticipating) deployment.NonParticipatingWorkload(Worker, "No reader in this environment.", [Source.Value]);
                else deployment.Workload(Worker, new("test/worker"), new("test/workers/worker"), [Source]);
                deployment.Resource(Secrets, new(AzureKeyVaultConstruction.Facility), new(physical), Authority, [Source]);
                if (alias) deployment.Resource(new("resources/other"), new(AzureKeyVaultConstruction.Facility),
                    new(physical.ToUpperInvariant()), Authority, [Source]);
            });
        return InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
    }

    sealed class Mocks(bool secretProperties = false) : IMocks
    {
        public ConcurrentBag<MockResourceArgs> Resources { get; } = [];
        public Task<object> CallAsync(MockCallArgs args) => throw new InvalidOperationException("Vault construction must never invoke a provider or retrieve secrets.");
        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            Resources.Add(args);
            var state = args.Inputs.ToDictionary();
            if (args.Type == "azure-native:keyvault:Vault")
            {
                state["name"] = args.Inputs["vaultName"];
                var properties = ((ImmutableDictionary<string, object>)args.Inputs["properties"]).ToDictionary();
                properties["vaultUri"] = ActualUri;
                state["properties"] = secretProperties ? Output.CreateSecret(properties) : properties;
            }
            return Task.FromResult<(string?, object)>((args.Type == "azure-native:keyvault:Vault" ? VaultId : args.Name + "-id", state));
        }
    }
}
