# Native App Service hosting seam

`AzureAppServiceBinding` associates native Azure Native 3.16.0 plans and sites with an exact
Cohesive infrastructure plan. It registers no resources and introduces no component parent.
Cohesive owns placement, hosting relationships, ownership checks and classified projections;
Pulumi owns sizing, platform, startup, health paths, provider options, execution and state.

## Canonical model and ownership

Declare each hosting plan as a managed persistent resource at facility `azure/app-service-plan`,
with locator `azure/app-service/plans/<physical-plan-name>`. Workloads use facility
`azure/app-service` and locator `azure/app-service/sites/<physical-site-name>`. Each workload
requires its selected plan to be ready in the canonical definition. Shared sites reference the
same canonical plan; dedicated sites reference a distinct plan. Aliased physical identities and
foreign/external plan ownership are rejected.

`AzureAppServicePolicy` covers every participating App Service workload exactly once. It selects
one explicit subscription, resource group and `pulumi/<project>/<stack>` lifecycle authority.
`AzureAppServicePlacement` adds only the missing association and activation decisions, plus
setting-to-binding attribution. It is not a second native site/options model. Non-participating
workloads have no placement; disabled sites are participating, deliberately retained resources.

The host constructs each unique plan once and reuses the native object. The seam validates the
actual site-to-plan ID, subscription/resource group, physical names, native URN project/stack/type
and enabled state. It does not maintain a global registration cache or prevent direct SDK code
from creating a second native object behind its back. Pulumi logical names and parent chains
remain caller-owned; fresh migration previews must establish that they were preserved.

## Native integration

First verify the exact Aspire/Pulumi handoff, then call `Validate` or `Names` before constructing
native resources. A complete canonical resource declaration is required; an existing site locator
alone cannot establish ownership of a shared plan.

```csharp
var names = AzureAppServiceBinding.Names(deployment, policy, workloadId, subscriptionId);
// Construct this native plan once per canonical plan; reuse it for every shared placement.
var plan = new AppServicePlan(existingPlanLogicalName, nativePlanArgs, nativePlanOptions);

var settings = AzureAppServiceBinding.Settings(deployment, policy, workloadId, nativeSettingValues);
nativeSiteArgs.Name = names.Site;
nativeSiteArgs.ServerFarmId = plan.Id;
nativeSiteArgs.SiteConfig = new SiteConfigArgs {
    AppSettings = settings,
    HealthCheckPath = existingHealthPath,
    AppCommandLine = existingStartupCommand
};
var site = new WebApp(existingSiteLogicalName, nativeSiteArgs, nativeSiteOptions);
var attached = AzureAppServiceBinding.Attach(deployment, policy, workloadId, subscriptionId, plan, site);
var identity = attached.ReadManagedIdentity(nativeInvokeOptions);
var principalId = identity.Apply(value => value.PrincipalId);
var tenantId = identity.Apply(value => value.TenantId);
```

This is a partial integration sketch: native arguments/options, canonical policy, existing IDs and
product settings are host inputs. Set the plan's native `Name` to `names.Plan`; preserve all other
required native configuration and the same explicit provider for resources and invokes. Reuse each
identity observation output when projecting principal and tenant; each method call is an explicit
read, not a global cached identity. The adapter creates no Azure role assignments.

## Configuration and activation

Settings use existing `InfrastructureSettingId` identities and native `Input<string>` values.
`BindingSettings` attributes relevant keys to incident participating canonical bindings. Platform
and product settings may remain unbound; do not invent a relationship merely to classify a native
option. The host remains responsible for supplying the value appropriate to each attributed key.
Canonical convention/effective-configuration artifacts are suitable producers for non-secret
policy, but their serialized string values are not a safe container for live credentials or
unresolved Pulumi outputs. This seam does not create a competing configuration resolver.

`Settings` checks all names against the explicit positive host name-length budget, rejects null
values, empty/invalid names and case-insensitive collisions, and requires every declared binding
and secret key to be present. It returns deterministic native `NameValuePairArgs` inputs. Existing
secret inputs retain their classification; explicitly selected secret keys are always classified
secret, including unknown values. Do not store raw values in policy or diagnostics. This is a
name-budget check, not a claim to enforce every Azure or OS environment-size limit. Call it before
native site construction; unresolved value sizes cannot be checked synchronously.

An enabled target may expose an HTTPS URL only for an incoming canonical binding using an explicitly
allowed endpoint contract and an enabled, participating App Service source. Unknown contracts,
foreign bindings, excluded sites and disabled sources or targets cannot acquire active endpoint
projections. HTTPS-only and native hostname checks occur when an endpoint is requested.

Disabled sites can retain configuration and managed identity for existing lifecycle operations.
Preparing their settings does not activate a binding. Do not interpret a disabled site as absent,
strip retained credentials or invent grants merely to make a migration fit the API. The host must
make any deliberate policy change separately reviewable. These activation decisions do not establish
runtime health, network reachability, authentication or effective authorization.

## Identity observation and failure behavior

`ReadManagedIdentity` explicitly invokes native `GetWebApp` only after the checked site ID is known.
A fresh preview with unknown identity does not make a premature lookup. A resolved observation must
match that site's name and ARM ID and provide non-empty GUID principal/tenant evidence for a
system-assigned identity. Caller-supplied invoke options preserve the native provider boundary.
No assumed tenant or directory lookup substitutes for the live evidence. Native SDK failures
propagate through the output; the caller uses the same provider and remains responsible for access.

Pure semantic errors have normalized attributable `AzureAppServiceValidationException` diagnostics.
Native association and observation mismatches fault outputs with `InvalidOperationException` without
printing resolved values. Unknown outputs remain unknown. Cancellation is checked before attachment;
Pulumi owns operation cancellation, retries and recovery after registration. Failed attachment or
observation cannot undo native registrations. Raw SDK objects remain explicit escape hatches.

## Fit, qualification and Ari adoption

The existing exact-plan/lifecycle checks are reused. ARM identity parsing shared by Key Vault,
telemetry and App Service now has one implementation, preserving existing optional-resource-group
behavior while App Service requires its declared group. No new package or Azure SDK dependency is
needed. A construction facade was rejected because the native SDK already owns the options; a
process-global shared-plan registry would add hidden lifetime and ordering behavior.

Source and package-only `AzureAppServiceTests` exercise shared/dedicated plans, native parents and
configuration, attribution, secret classification, disabled/excluded placement admission, ownership
and identity mismatches, explicit provider-read failures, cancellation and unknown fresh previews.
The package consumer reuses the same tests, including the no-build packaging path.

ARI-549 must adopt the published seam, first adding shared/dedicated plan resources and dependencies
to its canonical declaration. Preserve all six existing sites, `cohesive:ari:WebApp` parent tokens,
logical names, sizing/platform/startup/health settings, disabled-site configuration, principal lookup
behavior and grant identities. Project attributed product settings once. Qualify the stopped-site
policy explicitly; do not silently turn prepared settings into active endpoint admission. Refine local
Aspire plan dependencies through its existing semantic facilities rather than adding fake Azure services.
Compare fresh same-backend previews. Bootstrap, custom domains and runtime readiness remain separate.
No apply, destroy, DNS or state mutation belongs to this library change.
