# Key Vault binding seam

Cohesive.Infra declares the vault, its identity, ownership and consumer bindings. Pulumi's native
`VaultArgs`, `VaultPropertiesArgs` and `CustomResourceOptions` configure and create it. This adapter
connects those authorities; it does not maintain a second SKU, retention, networking or resource-options API.

## Declaration → native configuration → association

1. The existing Aspire handoff verifies the exact `InfrastructureTargetDeploymentPlan`.
2. `AzureKeyVaultBinding.VaultName` validates the semantic selection and returns the manifest name.
3. The host constructs a normal Pulumi `Vault`, supplying all native configuration and options.
4. `Attach` associates that resource with the canonical declaration and produces identity-checked
   outputs and explicit access-grant arguments. It registers no resources and invokes no provider.

```csharp
var name = AzureKeyVaultBinding.VaultName(plan, policy, subscriptionId, tenantId);
var nativeVault = new Vault("existing-vault", new VaultArgs
{
    VaultName = name,
    ResourceGroupName = resourceGroup.Name,
    Location = location,
    Properties = new VaultPropertiesArgs
    {
        TenantId = tenantId.ToString("D"),
        EnableRbacAuthorization = true,
        EnableSoftDelete = true,
        SoftDeleteRetentionInDays = 7,
        PublicNetworkAccess = "Enabled",
        Sku = new SkuArgs { Family = "A", Name = SkuName.Standard }
    }
}, new CustomResourceOptions { Provider = azureProvider, Parent = existingParent });
var bound = AzureKeyVaultBinding.Attach(plan, policy, subscriptionId, tenantId, nativeVault);
// Use bound.VaultId / bound.VaultUri for checked downstream projections.
```

The fragment assumes existing plan, policy, scope and host variables. The seven-day Standard profile
is only example host policy. Premium, purge protection, retention, firewall/private networking,
access policies, tags, native dependencies, imports, aliases and protection remain native Pulumi
configuration. The host must ensure those settings satisfy the capabilities its target declares.
Azure/Pulumi retain their validation and lifecycle contracts; this seam does not certify every
native setting or claim that attached resource properties prove general readiness.

The selected canonical resource must use `azure/key-vault`, target `pulumi-azure-native/3.16.0`, and
one managed Pulumi authority. Physical identity is `azure/key-vault/vaults/<vault-name>`. The manifest
remains the sole physical-name authority. Ambiguous aliases, external ownership, incomplete plans,
foreign targets and mismatched declared subscription/tenant are rejected. Group and region are
native target configuration, not a second placement catalog in the binding policy.

## Explicit access decisions

Each participating incoming secret-read binding needs exactly one attributed decision:

- `AssignSecretsUser` permits `AccessGrant` to produce
  [Key Vault Secrets User](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/security#key-vault-secrets-user)
  role arguments at the identity-checked actual vault ID. The resolved vault must use RBAC.
  This is an explicit vault-wide read grant, not inferred least privilege for an individual secret.
- `NoManagedGrant` creates no grant and cannot produce grant arguments. It does not claim external
  access exists or the workload is ready. Its non-secret reason and evidence/follow-up references
  remain inspectable alongside the canonical consumer.

Missing, duplicate, unknown or unattributed decisions fail semantic validation. Nonparticipating
workloads cannot receive grants. The host owns principal-to-workload association and retains the
role's native logical name, GUID, provider, parent and additional dependencies:

```csharp
var role = new RoleAssignment(existingRoleName,
    bound.AccessGrant(readerBinding, subscriptionId, tenantId, workloadPrincipal, existingRoleId),
    new CustomResourceOptions { Provider = azureProvider, Parent = existingParent });
```

Other authorization mechanisms can remain native with an attributed `NoManagedGrant` decision.
The helper intentionally authorizes only this supported canonical secret-reader/RBAC relationship;
it is not a general Azure permission catalog. No secret values are accepted, retrieved or exported.

## Validation timing and outputs

`Validate` is pure and returns normalized `azure.key-vault.*` diagnostics, canonical location,
manifest fingerprint and non-secret source references. `VaultName` throws
`AzureKeyVaultValidationException` before the host constructs a vault if semantic validation fails.
`Attach` repeats those checks, but the native resource supplied by the caller may already have been
registered. This API makes no fail-before-registration promise for caller-owned resource creation.

Resolved native name, resource ID subscription/provider/type/name and tenant must match the
association. `VaultId` and `VaultUri` fault with `InvalidOperationException` on mismatch, without
including resolved values in the message. A missing resolved URI also faults. Grant scope additionally
faults if the resolved vault is not RBAC-authorized. Unknown preview outputs remain unknown, and
Pulumi dependencies and secret classification propagate. Provider outputs are not reconstructed from
public-cloud hostname assumptions. The typed `Vault` remains available for native operations; its
raw outputs bypass these checked projections, so consumers needing the checked contract must use
the bound outputs. Post-registration checks cannot roll back resources or guarantee cloud atomicity.

Persist the immutable **binding policy** with `StrictDocumentJson.CreateOptions()`. It contains only
canonical selection, declared scope, access decisions and provenance, with no callback or provider
object in semantic IR. Native configuration stays in the host's target refinement. Credentials must
not appear in identifiers, reasons, tags or provenance; diagnostics do not echo access rationale.
Pulumi owns reconciliation, state, retries and cancellation of native operations. `Attach` cancellation
only prevents association and cannot cancel a vault that the caller has already registered.

## Design and verification

Reuse exact Infra plans/bindings/lifecycle and `AzureConstructionPolicy` admission checks. Extend the
existing Azure adapter at the semantic/native-resource seam. Avoid a parallel vault-options record,
resource factory callback, generic lowering framework or copied provider model. Aspire's existing
handoff/executor remains unchanged; no SDK dependency enters core Infra. Product topology and native
configuration remain with Ari, whose adoption is ARI-546 after COH-119 publication.

Source and package-only tests cover semantic rejection, native option preservation, parent/provider
and dependency behavior, explicit no-grant decisions, identity/tenant mismatches, RBAC prerequisites,
exact grant identity/scope, strict JSON, secret propagation, cancellation and zero provider invokes.

```bash
dotnet test src/Cohesive.Adapters.Pulumi.Azure.Tests -c Release
COHESIVE_NUGET_LOCAL_FEED=artifacts/nuget bash eng/test-pulumi-azure-package-consumer.sh <version>
```

Build then pack with `--no-build`; the package-only consumer repeats the same tests. Native live
preview equivalence and runtime access observations remain consumer qualification, not claims of
these offline tests.
