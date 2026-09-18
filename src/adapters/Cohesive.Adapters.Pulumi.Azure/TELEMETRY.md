# Native Azure telemetry association

`AzureTelemetryBinding` attaches native Pulumi Application Insights and Log Analytics resources to
an exact Cohesive deployment. It does not construct resources, install instrumentation, or infer
runtime health from successful provisioning.

## Authority and flow

Declare the ingestion resource, supporting telemetry store, export bindings and
`ingestion.RequiresReady(store)` in the canonical Infra definition. Deploy them through facilities
`azure/application-insights` and `azure/log-analytics` with physical identities
`azure/application-insights/components/<name>` and `azure/log-analytics/workspaces/<name>`.
Both resources must be managed exclusively by the same declared Pulumi lifecycle authority under
`pulumi-azure-native/3.16.0`. The policy selects these canonical resources and an export contract;
it does not contain a second name catalog or credentials.

1. Require the exact plan through the existing Aspire/Pulumi handoff.
2. Call `Names(plan, policy, subscriptionId)` before native registration. This validates complete
   realization, ownership, physical identity syntax, case-insensitive aliases, declared dependency,
   consumer direction/contract and explicit non-participation.
3. Construct the native workspace and component with these names. Configure retention, tags,
   network access, SKU, providers, parents, protection and additional dependencies with native SDK
   arguments/options. Link `ComponentArgs.WorkspaceResourceId` to `workspace.Id`.
4. Call `Attach` and use its checked `ComponentId` for downstream consumers such as ML workspaces.
   Select a canonical export binding and call `ConnectionString(binding.Id)` for host configuration.

```csharp
var names = AzureTelemetryBinding.Names(plan, policy, subscriptionId);
var workspace = new Pulumi.AzureNative.OperationalInsights.Workspace("existing-workspace", new()
{
    WorkspaceName = names.Workspace,
    ResourceGroupName = resourceGroup.Name,
    Location = location,
    RetentionInDays = 30,
    Sku = new Pulumi.AzureNative.OperationalInsights.Inputs.WorkspaceSkuArgs { Name = "PerGB2018" }
}, existingWorkspaceOptions);
var component = new Pulumi.AzureNative.ApplicationInsights.Component("existing-insights", new()
{
    ResourceName = names.Component,
    ResourceGroupName = resourceGroup.Name,
    Location = location,
    Kind = "web",
    ApplicationType = "web",
    RetentionInDays = 30,
    WorkspaceResourceId = workspace.Id
}, existingComponentOptions);
var telemetry = AzureTelemetryBinding.Attach(plan, policy, subscriptionId, workspace, component);
var exporter = telemetry.ExportBindings.Single(binding => binding.Source == workloadId);
var connection = telemetry.ConnectionString(exporter.Id);
```

The example assumes an existing plan, explicit `AzureTelemetryPolicy`, provider subscription,
resource group, location, workload identity and native resource options. `SourceReferences` should
point to the host's environment configuration, including its retention decision. Thirty days is an
example host policy, not a Cohesive default. Policies round-trip with `StrictDocumentJson`; connection
strings exist only as runtime Pulumi outputs and never enter the policy/IR.

## Boundaries and failure behavior

The existing resource, lifecycle and readiness IR can express the workspace directly, so this seam
introduces no auxiliary ownership catalog or target IR extension. A standalone workspace consumer
contract is outside this association's supported boundary and is rejected. A host that needs one
must add an explicit supported interpretation rather than silently ignore it. Missing consumers
must be reconciled in the canonical declaration; unknown/non-participating binding IDs cannot obtain
a connection projection. Empty participation is valid and does not add instrumentation.

Native SDK configuration remains authoritative for retention and all provider options. The adapter
checks resolved names, ARM resource ID kinds/subscriptions, and the actual component-to-workspace
link. Resource group selection and tenant/provider setup remain host-owned; physical identities
are relative to the declared subscription and native resource group, as in the other Azure seams.
Checked outputs retain both resource dependencies. Connections are always marked secret even if
the provider returns an unclassified string, and original secret classification is never weakened.
Both IDs require the linked pair to validate. Raw typed native resources remain available; using
their outputs directly bypasses these checks.

`Names` rejects invalid semantics before construction; `Attach` repeats those checks. Resolved
mismatches or a missing connection string fault outputs with non-value-bearing
`InvalidOperationException` messages. Unknown preview outputs remain unknown. Native registration
has already happened at attachment time; an output failure cannot undo it. Cancellation is checked
before attachment. Pulumi owns provider cancellation, retry, partial progress, state and recovery.
This adapter adds no second lifecycle engine or provider invokes.

The caller continues to choose SDK versus codeless instrumentation and the native configuration key
for each workload. `Cohesive.Adapters.OpenTelemetry` owns runtime instrumentation independently.
Successful binding/provisioning establishes neither effective connectivity nor runtime telemetry
health; those require separate observations.

## Validation

`AzureTelemetryTests` exercises semantic failures before registration, native configuration/options,
explicit non-participation, actual identity/linkage failures, secret classification and downstream
configuration, portable policy, and cancellation. The package-only consumer runs the same tests
against packed assemblies. Consumers must still compare fresh credentialed previews against their
existing backend before applying a migration.
