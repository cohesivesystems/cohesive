# Cohesive.Adapters.Pulumi.Azure

Construct one Azure Durable Task Consumption scheduler and task hub from an exact
`InfrastructureTargetDeploymentPlan` inside an existing Pulumi program. The adapter returns
managed-identity connection outputs and canonical worker binding access-grant inputs.

## Ownership and flow

`Cohesive.Infra` remains the authority for topology, canonical bindings, capability/readiness proof,
physical identity, and lifecycle ownership. `Validate` reads that compiled plan; `Register` repeats
validation before creating any provider resource. The selected resource must use facility
`azure/durable-task`, target `pulumi-azure-native/3.16.0`, and the expected single Pulumi lifecycle
authority. Its physical identity is:

```text
azure/durable-task/schedulers/<scheduler-name>/task-hubs/<hub-name>
```

The manifest identity supplies both Azure names. `AzureDurableTaskPolicy` supplies the explicit
subscription, resource group, location, Pulumi logical names, worker contract, and attributed network
rules. The caller must provide the same subscription it uses for its other Azure resources; the
generated provider never falls back to an ambient subscription. This physical identity convention is
relative to that explicit deployment scope; it does not itself assert subscription ownership.

The generated provider creates scheduler → hub dependencies through Pulumi outputs. Active canonical
worker bindings produce `AccessGrant` inputs scoped to **the hub's actual ID**, with the built-in
Durable Task Data Contributor role. The caller supplies the source workload's managed-identity
principal and preserves its role-assignment GUID, logical name, parent, and Azure Native provider.
An explicitly non-participating worker has no grant. Endpoint and managed-identity connection strings
contain no credentials; Pulumi output secret propagation is preserved, including secret principal inputs.

Pulumi owns state, reconciliation, retries, update atomicity, and operation cancellation. The adapter
adds no lifecycle execution or store. Cancellation is checked before registration; after registration
starts, cancellation belongs to the existing Pulumi executor. A failed provider operation may leave
partial Pulumi-managed progress and must be recovered through the same backend, not a second engine.

## Existing-plan integration and migration

Within the existing Pulumi program, first call the Aspire handoff's `RequireExactPlan(plan)`. The
[`Cohesive.Adapters.Aspire.Pulumi`](../Cohesive.Adapters.Aspire.Pulumi/README.md) executor and handoff
remain unchanged. This provider adapter deliberately has no Aspire dependency.

The following fragment assumes the program already has `plan`, `handoff`, `subscriptionId`,
`resourceGroupName`, `location`, `workerPrincipalId`, `existingAssignmentId`, and `azureProvider`:

```csharp
handoff.RequireExactPlan(plan);
var durable = AzureDurableTaskConstruction.Register(plan, new AzureDurableTaskPolicy
{
    Resource = new("resources/process-scheduler"),
    WorkerContract = new("contracts/durable-worker"), // use the existing canonical contract ID
    LifecycleAuthority = handoff.LifecycleAuthority,
    SubscriptionId = subscriptionId,
    ResourceGroupName = resourceGroupName,
    Location = location,
    ProviderName = "existing-durable-provider",
    SchedulerName = "existing-scheduler",
    TaskHubName = "existing-hub",
    IpAllowlist = ["192.0.2.0/24"], // replace with explicit environment policy
    SourceReferences = [SourceReference.Create("environment-policy", "production/durable-task/v1")]
}, subscriptionId);

var workerBinding = durable.WorkerBindings.Single(b => b.Source == new InfrastructureNodeId("workloads/worker"));
var grant = new RoleAssignment("existing-worker-grant",
    durable.AccessGrant(workerBinding.Id, subscriptionId, workerPrincipalId, existingAssignmentId),
    new CustomResourceOptions { Provider = azureProvider });
var connectionString = durable.ConnectionString;
```

Replace the handwritten provider/scheduler/hub declarations in place. Preserve their logical names,
physical names, subscription, resource group, region, common parent (null for root resources), and
role identities. No component parent is introduced. Preserve the role provider and any existing
resource options when registering the returned role arguments. Programs requiring distinct parents,
imports, aliases, or other scheduler/hub options need an explicit adapter extension before adoption.
Do not construct both the old and new slice in one program.

Remove the consumer's generated Durable Task SDK project reference when adopting this package; the
package contains that assembly. Compare previews using the same exact plan and Pulumi backend.
Any unexpected replacement or unrelated drift must be resolved before an apply. Ari adoption and
environment-specific identity/network decisions are tracked separately in ARI-535.

## Supported boundary and diagnostics

This initial slice supports Consumption, one scheduler/hub pair, managed resources, same-subscription
ServicePrincipal grants, explicit IPv4 address/CIDR rules, and managed-identity connections. Empty
network lists are emitted as empty; there is no automatic public-access fallback. Dedicated capacity,
private networking, IPv6, retention policy, scheduler tags, cross-subscription grants, physical aliases,
other binding contracts/directions, and referenced/external lifecycle ownership are outside this slice.
Unsupported options are absent from the policy rather than silently ignored; use strict document JSON
when restoring policy so unknown properties are rejected.

`Validate` returns structured `DocumentValidationDiagnostic` values with stable `azure.durable-task.*`
codes, canonical resource location, manifest fingerprint, and policy source references. `Register`
throws `AzureDurableTaskValidationException` containing those diagnostics before any registration.
The original plan's compilation errors are retained. Policies can be serialized with
`StrictDocumentJson.CreateOptions`; they are target configuration, not another topology or binding IR.
Principal-to-workload association remains an explicit caller responsibility: the adapter cannot infer
the Azure principal belonging to a canonical workload or verify an arbitrary external provider's settings.

## SDK provenance and packaging

The generated sources under `Generated/dotnet` come from Pulumi Azure Native **3.19.0**, module
`durabletask v20251101`, generated by Pulumi CLI **3.260.0**. The schema is pinned to
[Azure API 2025-11-01](https://learn.microsoft.com/en-us/azure/templates/microsoft.durabletask/2025-11-01/schedulers)
and its [task-hub schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.durabletask/2025-11-01/schedulers/taskhubs).
The generated resource token namespace is preserved for migration. Regenerate from the repository root:

```bash
pulumi package gen-sdk azure-native --version 3.19.0 --language dotnet --local \
  --out src/adapters/Cohesive.Adapters.Pulumi.Azure/Generated -- durabletask v20251101
```

Do not hand-edit generated files. The adjacent host `Directory.Build.props`/`targets` disable Cohesive
global usings, pin the generated project's Pulumi runtime to 3.113.1, and disable independent packing.
The Cohesive NuGet package bundles the generated assembly; it does not publish a package under Pulumi's
namespace. Azure Native 3.16.0 is the existing general Azure role-assignment SDK. No SDK dependency is
introduced into `Cohesive.Infra`.

The design evaluated the existing exact target deployment plan, Aspire/Pulumi handoff and executor,
Infra configuration bindings, and Durable Task runtime adapter. Reuse the first three unchanged;
runtime orchestration is not infrastructure construction. A separate provider adapter keeps generated
Azure dependencies outside core Infra and the provider-neutral Aspire bridge. A general graph lowering
framework or parallel connection-binding catalog would duplicate existing semantic authority and is
unnecessary for this bounded facility.

## Verification

```bash
dotnet test src/Cohesive.Adapters.Pulumi.Azure.Tests -c Release
dotnet test src/Cohesive.Adapters.Aspire.Pulumi.Tests -c Release
dotnet pack src/adapters/Cohesive.Adapters.Pulumi.Azure -c Release -o artifacts/nuget
```

After `eng/pack-local.sh <version>`, run `bash eng/test-pulumi-azure-package-consumer.sh <version>`.
This repeats the semantic tests against NuGet packages only and verifies that the bundled provider
assembly loads without a consumer-side generated project. CI runs the same package-only check.

Offline Pulumi mocks verify stable construction under reordered network rules, provider subscription,
schema namespace, physical names, preserved parent options, credential classification, exact hub-scoped
grants, absent-worker exclusion, early cancellation, policy roundtrips, and fail-before-registration
diagnostics. These checks do not prove live Azure availability, RBAC permission, or migration preview
equivalence; the consumer validates those against its existing backend before applying.
