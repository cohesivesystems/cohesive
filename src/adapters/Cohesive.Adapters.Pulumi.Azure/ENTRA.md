# Native Entra application and authorization association

`AzureEntraBinding` connects native Pulumi AzureAD 6.9.0 resources to an exact Cohesive Azure
program plan. It registers nothing, performs no directory lookups, and never creates a tenant or
implicitly grants consent. Native application configuration remains the SDK's responsibility.

## Canonical ownership

Use existing Infra resources and lifecycle declarations:

- An **external tenant** at facility `azure/entra`, with physical locator
  `azure/entra/tenants/<tenant-guid>` or `azure/entra/tenants/current-provider`.
- A **managed application** at `azure/entra-application`, located at
  `azure/entra/applications/<native-Pulumi-logical-name>`.
- An optional, separately declared **managed service principal** at
  `azure/entra-service-principal`, located at
  `azure/entra/service-principals/<native-Pulumi-logical-name>`.

The application requires the tenant to be ready; the principal requires its application. These
are canonical readiness dependencies, not a second relationship catalog. Application and principal
share one exclusive lifecycle authority, `pulumi/<project>/<stack>`. The tenant remains externally
owned. A native resource is associated through its Pulumi registration locator because Graph assigns
object/client IDs during creation. Display names are mutable and non-unique, so they are not identity.
Registration logical names must be unique per kind in this program even across different parents;
this bounded convention does not claim directory-global name uniqueness.

The supported target remains the existing Azure program interpretation
`pulumi-azure-native/3.16.0`; the Entra facility additionally uses AzureAD 6.9.0. Target identity denotes
the compiled program, not a claim that AzureAD resources are Azure Native resources. The adapter's
optional Azure package already owns this program boundary and shares its exact-plan/lifecycle
validation. Adding the AzureAD dependency there avoids duplicating those mechanisms in another
package. The tradeoff is an AzureAD transitive dependency for Azure-only adapter consumers; core
`Cohesive.Infra` remains independent of both SDKs.

## Permission boundaries

Incoming canonical bindings select either a delegated-access or application-access contract. Exactly
one attributed permission decision is required for each participating binding. Sources may be deployed
workloads or managed application registrations; explicitly non-participating workloads are excluded.
Outgoing bindings belong to the target application's association rather than being reinterpreted as
permissions offered by the source application. Product scope/role GUIDs come from the host's existing
policy authority and are passed unchanged; do not build a parallel GUID catalog for the seam.

| Action | Projection | Guarantee |
| --- | --- | --- |
| `RequestDelegatedScope` | `RequiredScopeAccess(binding)` | Requests an existing enabled scope in native required-resource-access configuration. Does **not** grant consent. |
| `AssignApplicationRole` | `AppRoleGrant(binding, source, principalId, principalTenantId)` | Produces an explicitly authorized assignment to an owned resource principal for an enabled application-assignable role. |

`ConsentDiagnostics` reports `azure.entra.consent-unverified` for each delegated request. The seam
has no observation proving either absence or presence of user/admin consent. A readiness assessment
must obtain separate evidence; requested permissions must never be treated as granted permissions.
There is no consent-grant method and no default authorization action.

The host associates canonical sources with their actual native principal ID and tenant outputs.
Resolved foreign-tenant or invalid principal evidence fails closed. This check does not query Graph,
authenticate caller-provided evidence, or independently prove which workload owns a supplied ID;
that association remains a trusted host boundary. Native system-assigned identity outputs are an
appropriate source. Never substitute an assumed tenant merely to make the check pass.

## Native integration

First require the existing exact Aspire/Pulumi handoff. Obtain the tenant using the **same AzureAD
provider configuration** used for native resources; pass it to `Names` and `Attach`. The application
SDK does not expose its owning tenant, so applications without service principals rely on this host
scope assertion. When a principal exists, its resolved application tenant and client ID are checked
against the association as well.

```csharp
var names = AzureEntraBinding.Names(plan, policy, providerTenantId);
var application = new Pulumi.AzureAD.Application(names.Application, nativeApplicationArgs, nativeApplicationOptions);
var principal = names.ServicePrincipal is null ? null : new Pulumi.AzureAD.ServicePrincipal(
    names.ServicePrincipal, new() { ClientId = application.ClientId }, nativePrincipalOptions);
var registration = AzureEntraBinding.Attach(plan, policy, providerTenantId, application, principal);

// Configure the canonical source client using the returned native SDK input object.
var requestedAccess = registration.RequiredScopeAccess(delegatedBindingId);

// Register a role only where the canonical binding policy explicitly selects this action.
var grantInputs = registration.AppRoleGrant(roleBindingId, workloadId,
    nativePrincipalId, nativePrincipalTenantId);
var grant = new Pulumi.AzureAD.AppRoleAssignment(existingGrantName, grantInputs, nativeGrantOptions);
```

The example assumes an explicit `AzureEntraPolicy`, native SDK arguments/options and existing binding
IDs from the plan. It is illustrative of two different actions; a scope-only application need not
have a principal or a role grant. The native host retains audiences, owners, redirects, scope/role
metadata, identifier URI resources, Graph propagation timing, consent administration, password
creation/rotation and federation. Existing component types and parents need not change.

`ApplicationId` and `ClientId` retain native dependencies and secret classification. Applications
are checked against native URN project/stack/type/logical name and Graph object/client IDs. Principal
ID, URN, tenant and application linkage are also checked. Identifier URIs can continue to be derived
natively from the checked client ID. `ClientSecret(existingPassword)` checks the password's application
association and always classifies its value as secret. It does not declare the credential's lifecycle
or create/rotate a password; the host must account for that ownership and its consumers.

## Failure and lifecycle behavior

`Names` validates semantic facts before registration; `Attach` repeats validation after callers have
constructed native resources. Ownership, permission and provenance failures have structured,
deterministically ordered diagnostics. Resolved identity, permission, principal or credential
mismatches fault outputs with `InvalidOperationException` without including resolved values. Unknown
preview values remain unknown. An output failure cannot undo registration. Pulumi retains execution,
cancellation after registration, retry, partial progress and recovery. Cancellation before attachment
is checked explicitly. Raw native resources remain available and bypass checked projections.

A successful association is not runtime authentication, consent or readiness evidence. Tenant
creation, directory-wide administration, implicit consent, automatic privilege assignment and
provider-option wrappers are outside this boundary.

## Qualification and Ari adoption

`AzureEntraTests` exercises native options/parents, canonical ownership errors, exact permission
selection, foreign/mismatched identities, delegated consent diagnostics, optional principals,
non-participation, secret propagation, unknown previews and cancellation. Package-only consumers
run the same tests against packed assemblies, including the no-build pack path.

ARI-548 must first refine Ari's canonical declaration: its existing external tenant is not ownership
of the managed applications or principals. Seven product applications, the Training API resource
principal, requested scopes, two workload role edges and the existing Training UI password need
explicit association/ownership decisions. Deployment applications, their principals, federation and
bootstrap Azure grants remain the separate ARI-551 responsibility. Preserve existing component tokens,
logical names, scope/role GUID seeds, audiences, redirects, Graph propagation timing, consent behavior
and classified outputs. Publish this seam before adoption and compare fresh same-backend previews;
no apply, destroy, DNS or state changes are part of this library work.
