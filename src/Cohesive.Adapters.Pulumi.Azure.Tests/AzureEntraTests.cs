using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Pulumi.Azure;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi;
using Pulumi.AzureAD;
using Pulumi.AzureAD.Inputs;
using Pulumi.Testing;

namespace Cohesive.Adapters.Pulumi.Azure.Tests;

public sealed class AzureEntraTests
{
    static readonly Guid TenantId = Guid.Parse("adc0f122-9477-47b8-b08a-2b2f1b6d0749");
    static readonly Guid ScopeId = Guid.Parse("998d2d2d-a6e3-475e-a44e-fc6b332d579f");
    static readonly Guid RoleId = Guid.Parse("414a3f5f-f358-4b54-a4c4-fd4b51ab79e0");
    const string ClientId = "7cc7226b-1b5c-4c4b-b0dc-f0f49c993b17";
    const string ObjectId = "1b5f037d-0fdd-47d2-8b0c-c766717f671a";
    const string PrincipalId = "56f17c6b-d065-4b01-bae8-3dcb98f2cdfe";
    const string WorkerPrincipalId = "1d20fb9f-3704-41ee-af69-1fc8e5d7b19d";
    static readonly SourceReference Source = SourceReference.Create("test", "entra-policy");
    static readonly InfrastructureNodeId Tenant = new("resources/tenant");
    static readonly InfrastructureNodeId App = new("resources/application");
    static readonly InfrastructureNodeId Principal = new("resources/principal");
    static readonly InfrastructureNodeId Worker = new("workloads/worker");
    static readonly InfrastructureBindingId Scope = new("bindings/scope");
    static readonly InfrastructureBindingId Role = new("bindings/role");
    static readonly InfrastructureBindingContractId ScopeContract = new("contracts/scope");
    static readonly InfrastructureBindingContractId RoleContract = new("contracts/role");
    static readonly InfrastructureLifecycleAuthorityId Authority = new("pulumi/test/production");
    static TestOptions Options(bool preview = false) => new() { ProjectName = "test", StackName = "production", IsPreview = preview };
    static AzureEntraPolicy Policy() => new()
    {
        Tenant = Tenant, Application = App, ServicePrincipal = Principal, LifecycleAuthority = Authority, TenantId = TenantId,
        DelegatedAccessContract = ScopeContract, ApplicationAccessContract = RoleContract, SourceReferences = [Source],
        Permissions = [new(Scope, ScopeId, AzureEntraPermissionAction.RequestDelegatedScope, [Source]),
            new(Role, RoleId, AzureEntraPermissionAction.AssignApplicationRole, [Source])]
    };

    [Fact]
    public async Task Native_options_permission_ids_parents_and_classified_outputs_are_preserved()
    {
        var plan = Plan(); var policy = Policy(); var mocks = new Mocks(); var parentChecked = false;
        Assert.True(plan.IsComplete);
        await Deployment.TestAsync(mocks, Options(), async () =>
        {
            ComponentResource? parent = null;
            parent = new ComponentResource("cohesive:ari:AppRegistration", "original-parent", new ComponentResourceOptions
            {
                ResourceTransformations = { args =>
                {
                    if (args.Resource is Application or ServicePrincipal)
                    { Assert.Same(parent, args.Options.Parent); Assert.True(args.Options.Protect); parentChecked = true; }
                    return null;
                } }
            });
            var provider = new Provider("original-provider", new() { TenantId = TenantId.ToString("D") });
            var (app, principal) = Native(plan, policy, new() { Parent = parent, Provider = provider, Protect = true });
            var resources = AzureEntraBinding.Attach(plan, policy, TenantId, app, principal);
            Assert.Same(app, resources.Application); Assert.Same(principal, resources.ServicePrincipal);
            Assert.Equal(new[] { Role, Scope }, resources.AccessBindings.Select(b => b.Id));
            Assert.Contains(resources.ConsentDiagnostics, d => d.Code == "azure.entra.consent-unverified" && d.Severity == DiagnosticSeverity.Warning);
            _ = new Application("native-consumer", new()
            { DisplayName = "Unchanged native client", RequiredResourceAccesses = { resources.RequiredScopeAccess(Scope) } });
            var grant = resources.AppRoleGrant(Role, Worker, Output.CreateSecret(WorkerPrincipalId), TenantId.ToString("D"));
            Assert.True(await Output.IsSecretAsync((Output<string>)grant.PrincipalObjectId));
            _ = new AppRoleAssignment("existing-role-assignment", grant, new() { Parent = parent, Provider = provider });
            var password = new ApplicationPassword("existing-password", new() { ApplicationId = resources.ApplicationId, DisplayName = "native-rotation", EndDateRelative = "8760h" });
            var secret = resources.ClientSecret(password);
            Assert.True(await Output.IsSecretAsync(secret));
            secret.Apply(value => { Assert.Equal("fixture-secret", value); return value; });
            Assert.Throws<ArgumentException>(() => resources.RequiredScopeAccess(Role));
            Assert.Throws<ArgumentException>(() => resources.AppRoleGrant(Scope, Worker, WorkerPrincipalId, TenantId.ToString()));
            Assert.Throws<ArgumentException>(() => resources.AppRoleGrant(Role, App, WorkerPrincipalId, TenantId.ToString()));
        });
        Assert.True(parentChecked);
        var appArgs = Assert.Single(mocks.Resources, r => r.Name == "existing-application");
        Assert.Contains("original-provider", appArgs.Provider);
        Assert.Equal("Native display name", appArgs.Inputs["displayName"]);
        Assert.Equal("AzureADMultipleOrgs", appArgs.Inputs["signInAudience"]);
        var web = Assert.IsAssignableFrom<ImmutableDictionary<string, object>>(appArgs.Inputs["web"]);
        Assert.Contains("https://example.invalid/callback", (IEnumerable<object>)web["redirectUris"]);
        var grantArgs = Assert.Single(mocks.Resources, r => r.Type == "azuread:index/appRoleAssignment:AppRoleAssignment");
        Assert.Equal(RoleId.ToString("D"), grantArgs.Inputs["appRoleId"]);
        Assert.Equal(PrincipalId, grantArgs.Inputs["resourceObjectId"]);
        Assert.DoesNotContain(mocks.Resources, r => r.Type?.Contains("DelegatedPermissionGrant", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("tenant-ownership")]
    [InlineData("tenant-identity")]
    [InlineData("authority")]
    [InlineData("lifecycle")]
    [InlineData("provenance")]
    [InlineData("contracts")]
    [InlineData("permissions")]
    [InlineData("permission-action")]
    [InlineData("permission-evidence")]
    [InlineData("dependency")]
    [InlineData("physical-identity")]
    [InlineData("alias")]
    [InlineData("binding")]
    [InlineData("consumer")]
    [InlineData("target")]
    [InlineData("incomplete")]
    public async Task Invalid_canonical_ownership_and_authorization_fail_before_registration(string failure)
    {
        var policy = failure switch
        {
            "tenant" => Policy() with { TenantId = Guid.NewGuid() },
            "authority" => Policy() with { LifecycleAuthority = new("ambiguous") },
            "lifecycle" => Policy() with { LifecycleAuthority = new("pulumi/other/production") },
            "provenance" => Policy() with { SourceReferences = [] },
            "contracts" => Policy() with { DelegatedAccessContract = RoleContract },
            "binding" => Policy() with { ApplicationAccessContract = new("contracts/unsupported") },
            "permissions" => Policy() with { Permissions = [] },
            "permission-action" => Policy() with { Permissions = [Policy().Permissions[0] with { Action = default }, Policy().Permissions[1]] },
            "permission-evidence" => Policy() with { Permissions = [Policy().Permissions[0] with { SourceReferences = [] }, Policy().Permissions[1]] },
            _ => Policy()
        };
        var mocks = new Mocks();
        await Deployment.TestAsync(mocks, Options(), () =>
        {
            var error = Assert.Throws<AzureEntraValidationException>(() => AzureEntraBinding.Names(Plan(failure), policy, TenantId));
            Assert.Contains(error.Diagnostics, d => d.Code == "azure.entra." + failure);
            Assert.All(error.Diagnostics, d => Assert.NotNull(d.Evidence));
        });
        Assert.Empty(mocks.Resources);
    }

    [Theory]
    [InlineData("application-id")]
    [InlineData("application-client")]
    [InlineData("application-urn")]
    [InlineData("principal-client")]
    [InlineData("principal-id")]
    [InlineData("foreign-stack")]
    [InlineData("principal-tenant")]
    [InlineData("principal-urn")]
    [InlineData("scope-disabled")]
    [InlineData("role-disabled")]
    [InlineData("role-members")]
    [InlineData("foreign-source")]
    [InlineData("password-application")]
    public async Task Resolved_foreign_or_mismatched_native_values_fail_without_exposing_secrets(string mismatch)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Deployment.TestAsync(new Mocks(mismatch), mismatch == "foreign-stack" ? new TestOptions { ProjectName = "other", StackName = "production", IsPreview = false } : Options(), () =>
        {
            var (app, principal) = Native(Plan(), Policy(), appName: mismatch == "application-urn" ? "wrong-name" : null,
                principalName: mismatch == "principal-urn" ? "wrong-principal" : null);
            var resources = AzureEntraBinding.Attach(Plan(), Policy(), TenantId, app, principal);
            _ = new Application("consumer", new() { DisplayName = "Client", RequiredResourceAccesses = { resources.RequiredScopeAccess(Scope) } });
            _ = new AppRoleAssignment("grant", resources.AppRoleGrant(Role, Worker, WorkerPrincipalId,
                mismatch == "foreign-source" ? Guid.NewGuid().ToString() : TenantId.ToString()));
            var password = new ApplicationPassword("credential", new() { ApplicationId = resources.ApplicationId });
            resources.ClientSecret(password).Apply(value => value);
        }));
        Assert.Contains("InvalidOperationException", error.ToString());
        Assert.DoesNotContain("fixture-secret", error.ToString());
    }

    [Fact]
    public async Task Excluded_consumers_get_no_permission_projection_and_no_implicit_grants()
    {
        var plan = Plan(nonparticipating: true); var policy = Policy() with { Permissions = [] }; var mocks = new Mocks();
        await Deployment.TestAsync(mocks, Options(), () =>
        {
            var (app, principal) = Native(plan, policy);
            var resources = AzureEntraBinding.Attach(plan, policy, TenantId, app, principal);
            Assert.Empty(resources.AccessBindings); Assert.Empty(resources.ConsentDiagnostics);
            Assert.Throws<ArgumentException>(() => resources.RequiredScopeAccess(Scope));
            Assert.Throws<ArgumentException>(() => resources.AppRoleGrant(Role, Worker, WorkerPrincipalId, TenantId.ToString()));
        });
        Assert.Equal(2, mocks.Resources.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delegated_clients_do_not_require_a_resource_service_principal(bool resourceConsumer)
    {
        var plan = Plan(scopeOnly: true, resourceConsumer: resourceConsumer);
        var policy = Policy() with { ServicePrincipal = null, Permissions = [Policy().Permissions[0]] };
        var mocks = new Mocks();
        await Deployment.TestAsync(mocks, Options(), () =>
        {
            var names = AzureEntraBinding.Names(plan, policy, TenantId);
            Assert.Null(names.ServicePrincipal);
            var app = new Application(names.Application, new() { DisplayName = "Native application" });
            var resources = AzureEntraBinding.Attach(plan, policy, TenantId, app);
            Assert.Null(resources.ServicePrincipal);
            Assert.Equal(Scope, Assert.Single(resources.AccessBindings).Id);
            Assert.Single(resources.ConsentDiagnostics);
            Assert.Throws<ArgumentException>(() => resources.AppRoleGrant(Scope, Worker, WorkerPrincipalId, TenantId.ToString()));
        });
        Assert.DoesNotContain(mocks.Resources, r => r.Type == "azuread:index/servicePrincipal:ServicePrincipal");
    }

    [Fact]
    public async Task Unknown_preview_credentials_stay_unknown_and_secret()
    {
        var resolved = false;
        await Deployment.TestAsync(new Mocks("unknown-secret"), Options(preview: true), async () =>
        {
            var (app, principal) = Native(Plan(), Policy());
            var resources = AzureEntraBinding.Attach(Plan(), Policy(), TenantId, app, principal);
            var password = new ApplicationPassword("password", new() { ApplicationId = resources.ApplicationId });
            var secret = resources.ClientSecret(password);
            Assert.True(await Output.IsSecretAsync(secret));
            secret.Apply(value => { resolved = true; return value; });
        });
        Assert.False(resolved);
    }

    [Fact]
    public void Policy_roundtrip_and_cancellation_preserve_the_boundary()
    {
        var options = StrictDocumentJson.CreateOptions();
        var json = JsonSerializer.Serialize(Policy(), options);
        var restored = JsonSerializer.Deserialize<AzureEntraPolicy>(json, options)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored, options));
        Assert.Empty(AzureEntraBinding.Validate(Plan(), restored, TenantId));
        Assert.Throws<OperationCanceledException>(() => AzureEntraBinding.Attach(Plan(), Policy(), TenantId, null!, cancellationToken: new(true)));
        Assert.Contains(AzureEntraBinding.Validate(Plan(), Policy() with { Permissions = default }, TenantId), d => d.Code == "azure.entra.permissions");
        Assert.Contains(AzureEntraBinding.Validate(Plan(), Policy() with { ServicePrincipal = null }, TenantId), d => d.Code == "azure.entra.permission-action");
    }

    static (Application, ServicePrincipal) Native(InfrastructureTargetDeploymentPlan plan, AzureEntraPolicy policy,
        CustomResourceOptions? options = null, string? appName = null, string? principalName = null)
    {
        var names = AzureEntraBinding.Names(plan, policy, TenantId);
        var app = new Application(appName ?? names.Application, new()
        {
            DisplayName = "Native display name", SignInAudience = "AzureADMultipleOrgs",
            Web = new ApplicationWebArgs { RedirectUris = { "https://example.invalid/callback" } },
            Api = new ApplicationApiArgs { Oauth2PermissionScopes = { new ApplicationApiOauth2PermissionScopeArgs
            { Id = ScopeId.ToString("D"), Value = "access", Type = "User", Enabled = true } } },
            AppRoles = { new global::Pulumi.AzureAD.Inputs.ApplicationAppRoleArgs
            { Id = RoleId.ToString("D"), Value = "invoke", DisplayName = "Invoke", Description = "Fixture", AllowedMemberTypes = { "Application" }, Enabled = true } }
        }, options);
        return (app, new ServicePrincipal(principalName ?? names.ServicePrincipal!, new() { ClientId = app.ClientId }, options));
    }

    static InfrastructureTargetDeploymentPlan Plan(string? failure = null, bool nonparticipating = false, bool scopeOnly = false, bool resourceConsumer = false)
    {
        InfrastructureCapabilityId execution = new("test/execution");
        InfrastructureCapabilityId identity = new("test/identity");
        InfrastructureCapabilityId directory = new("test/directory");
        InfrastructureCapabilityId servicePrincipal = new("test/principal");
        var semantic = Infrastructure.Define(new("test/entra"), new("1"), new("test/bindings/v1"), infra =>
        {
            var scope = infra.Contract(ScopeContract, new("test/scope")).Requires(identity).SourcedFrom(Source.Value);
            var role = scopeOnly ? null : infra.Contract(RoleContract, new("test/role")).Requires(identity).SourcedFrom(Source.Value);
            var worker = infra.Workload(Worker).Requires(execution);
            if (failure == "incomplete") worker.Requires(new("unsupported"));
            var tenant = infra.Resource(Tenant).Requires(directory);
            if (failure == "tenant-ownership") tenant.Persistent(); else tenant.External();
            var app = infra.Resource(App).Persistent().Requires(identity);
            if (!scopeOnly) infra.Resource(Principal).Persistent().Requires(servicePrincipal).RequiresReady(app);
            if (resourceConsumer) infra.Resource(new("resources/client")).Persistent().Requires(identity);
            if (failure != "dependency") app.RequiresReady(tenant);
            if (failure == "alias") infra.Resource(new("resources/alias")).Persistent().Requires(identity);
            infra.Bind(Scope, failure == "consumer" ? Tenant : resourceConsumer ? new InfrastructureNodeId("resources/client") : Worker).To(app).As(scope);
            if (role is not null) infra.Bind(Role, worker).To(app).As(role);
        });
        var facilities = InfrastructureTargetFacilities.Define(new("test/facilities/v1"), new("test/capabilities/v1"),
            new(failure == "target" ? "other-target" : AzureEntraBinding.Target), new("test/production"),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion], target =>
            {
                target.Workload(new("test/worker")).Provides(new(new("test/execution"), execution, CapabilityRealizationKind.Native, sourceReferences: [Source]));
                foreach (var (facility, capability) in new[] { (AzureEntraBinding.TenantFacility, directory), (AzureEntraBinding.ApplicationFacility, identity), (AzureEntraBinding.PrincipalFacility, servicePrincipal) })
                    target.Resource(new(facility)).Provides(new(new(facility + "/evidence"), capability, CapabilityRealizationKind.Native, sourceReferences: [Source]));
            });
        var manifest = InfrastructureTargetDeployments.Define(new("test/deployment/v1"), semantic.Definition, facilities, deployment =>
        {
            if (nonparticipating) deployment.NonParticipatingWorkload(Worker, "No consumer in this environment", [Source.Value]);
            else deployment.Workload(Worker, new("test/worker"), new("test/workers/worker"), [Source]);
            deployment.Resource(Tenant, new(AzureEntraBinding.TenantFacility), new(failure == "tenant-identity" ? "wrong" : "azure/entra/tenants/current-provider"), new("external/entra"), [Source]);
            deployment.Resource(App, new(AzureEntraBinding.ApplicationFacility), new(failure == "physical-identity" ? "wrong" : "azure/entra/applications/existing-application"), Authority, [Source]);
            if (!scopeOnly) deployment.Resource(Principal, new(AzureEntraBinding.PrincipalFacility), new("azure/entra/service-principals/existing-principal"), Authority, [Source]);
            if (resourceConsumer) deployment.Resource(new("resources/client"), new(AzureEntraBinding.ApplicationFacility), new("azure/entra/applications/client"), Authority, [Source]);
            if (failure == "alias") deployment.Resource(new("resources/alias"), new(AzureEntraBinding.ApplicationFacility), new("azure/entra/applications/existing-application"), Authority, [Source]);
        });
        return InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
    }

    sealed class Mocks(string? mismatch = null) : IMocks
    {
        public ConcurrentBag<MockResourceArgs> Resources { get; } = [];
        public Task<object> CallAsync(MockCallArgs args) => throw new InvalidOperationException("Entra association must not invoke providers.");
        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            Resources.Add(args); var state = args.Inputs.ToDictionary(); var id = args.Name + "-id";
            switch (args.Type)
            {
                case "azuread:index/application:Application":
                    id = mismatch == "application-id" ? "/applications/wrong" : "/applications/" + ObjectId;
                    state["objectId"] = ObjectId; state["clientId"] = mismatch == "application-client" ? "invalid" : ClientId;
                    if (mismatch == "scope-disabled" && state.TryGetValue("api", out var api))
                    {
                        var values = ((ImmutableDictionary<string, object>)api).ToDictionary();
                        values["oauth2PermissionScopes"] = new[] { new Dictionary<string, object> { ["id"] = ScopeId.ToString(), ["enabled"] = false } };
                        state["api"] = values;
                    }
                    if (mismatch is "role-disabled" or "role-members") state["appRoles"] = new[] { new Dictionary<string, object>
                    { ["id"] = RoleId.ToString(), ["enabled"] = mismatch != "role-disabled", ["allowedMemberTypes"] = new[] { "User" } } };
                    break;
                case "azuread:index/servicePrincipal:ServicePrincipal":
                    id = mismatch == "principal-id" ? "/servicePrincipals/wrong" : "/servicePrincipals/" + PrincipalId;
                    state["objectId"] = PrincipalId;
                    state["clientId"] = mismatch == "principal-client" ? Guid.NewGuid().ToString() : args.Inputs["clientId"];
                    state["applicationTenantId"] = mismatch == "principal-tenant" ? Guid.NewGuid().ToString() : TenantId.ToString();
                    break;
                case "azuread:index/applicationPassword:ApplicationPassword":
                    if (mismatch != "unknown-secret") state["value"] = "fixture-secret";
                    if (mismatch == "password-application") state["applicationId"] = "/applications/foreign";
                    break;
            }
            return Task.FromResult<(string?, object)>((id, state));
        }
    }
}
