# Exact Key Vault construction

`AzureKeyVaultConstruction` constructs one Standard, RBAC-authorized, soft-delete-enabled vault
inside an existing Pulumi program. It never retrieves, sets or exports secret values, creates a
provider, or creates role assignments automatically.

## Authority and flow

The exact `InfrastructureTargetDeploymentPlan` is the authority for canonical resource identity,
consumer bindings, physical placement, capability/readiness obligations and lifecycle ownership.
The resource must use `azure/key-vault`, target `pulumi-azure-native/3.16.0`, and a single managed
Pulumi authority. Physical identity is `azure/key-vault/vaults/<vault-name>`; there is no second
physical-name property. Case-insensitive aliases and external ownership are rejected.

`AzureKeyVaultPolicy` supplies attributable subscription, tenant, resource group, location, logical
name, RBAC selection, retention, public network setting and per-consumer access decisions. Host
subscription and tenant must match before any registration. These are **declared scope checks**:
the caller must obtain the actual host scope and use the matching default or explicit provider.
The adapter cannot attest arbitrary provider credentials or map a principal to a workload.

Call the existing Aspire handoff's `RequireExactPlan(plan)` before `Register`. Pass the original
parent and provider, plus `resourceGroupDependency` when the group is created in the program.
No implicit component changes resource URNs. The returned typed `Vault.Id` retains dependencies
for consumers such as ML workspaces; `VaultUri` comes from the provider's actual properties, so
sovereign-cloud endpoints are not reconstructed. Unknown outputs stay unknown. Pulumi secret
classification propagates through URI and principal projections; a missing resolved URI faults
with `InvalidOperationException` rather than manufacturing an endpoint.

## Access decisions are not access observations

Every participating incoming binding must use the explicitly selected secret-read contract and
have exactly one `AzureKeyVaultBindingAccess` decision. Nonparticipating workloads cannot acquire
grants. Decisions require a non-secret reason and references to evidence or a concrete unresolved
access follow-up. Missing, duplicate, unknown or unattributed decisions fail before registration.

- `AssignSecretsUser` authorizes `AccessGrant` to produce the built-in
  [Key Vault Secrets User](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/security#key-vault-secrets-user)
  role arguments at the **actual vault ID**. This reads all secrets in that vault; selecting it is
  an explicit vault-wide decision, not a least-privilege inference from an individual secret.
- `NoManagedGrant` deliberately preserves absence of a managed role. It does **not** assert that
  an external role exists, that the workload can read secrets, or that runtime readiness is proven.
  `AccessGrant` rejects such a binding. The original decision remains inspectable in `Policy` and
  the canonical consumer remains visible in `SecretBindings`.

The caller owns the principal/workload association, role logical name, GUID, parent, provider and
extra dependencies. `AccessGrant` requires the same subscription and tenant and a non-empty
assignment GUID. It cannot broaden scope to a resource group/subscription, substitute another role,
or authorize an undeclared/nonparticipating consumer. Role creation remains explicit:

```csharp
var vault = AzureKeyVaultConstruction.Register(plan, policy, subscriptionId, tenantId,
    provider: azureProvider, parent: existingParent, resourceGroupDependency: resourceGroup);
var grant = new RoleAssignment(existingRoleName,
    vault.AccessGrant(secretReaderBinding, subscriptionId, tenantId, workloadPrincipal, existingRoleId),
    new CustomResourceOptions { Provider = azureProvider, Parent = existingParent });
```

This fragment assumes an existing verified plan and explicit policy. Preserve existing access
absence during mechanical migration; reconcile runtime consumers and authorization evidence in a
separate policy decision before adding permissions. Ari's adoption is ARI-546, separately from the
COH-119 reusable provider deliverable.

## Supported profile and failure contract

The profile fixes Standard/A SKU, RBAC, soft delete enabled, and deployment/disk-encryption/template
integration flags disabled. Retention is explicitly 7–90 days, matching
[Azure's soft-delete contract](https://learn.microsoft.com/en-us/azure/key-vault/general/soft-delete-overview).
`AuthorizationMode` must be `Rbac`; `PublicNetworkAccess` must be `Enabled` or `Disabled`.
Purge protection is left unspecified, preserving the provider/default behavior; this API does not
claim to configure it. Private endpoints, firewall rules, legacy access policies, HSM/Premium,
secret-specific scopes, secret/key/certificate contents, imports/aliases and other resource options
are outside this slice. A disabled public endpoint does not provision a private network or prove
reachability. Extend policy explicitly before adopting a vault needing unsupported options.

Policies are immutable target configuration, not a parallel infrastructure IR. Persist them with
`StrictDocumentJson.CreateOptions()` so unsupported fields fail rather than disappear. No credentials
belong in tags, reasons, identifiers or source references. Diagnostics do not interpolate arbitrary
policy values or rationale; they contain canonical resource/binding identifiers, fingerprint and
non-secret provenance. `Validate` returns normalized `azure.key-vault.*` errors; `Register` repeats
it and throws `AzureKeyVaultValidationException` before resource registration on failure.

Cancellation is checked before registration. Pulumi owns subsequent cancellation, retries, state,
partial progress and recovery; the adapter adds no lifecycle engine or transactional guarantee.

## Design decision and verification

Reuse the exact Infra deployment/binding/lifecycle contracts and `AzureConstructionPolicy` admission
checks used by Durable Task, Cosmos and Storage. Extend the Azure provider adapter rather than core
Infra or the provider-neutral Aspire bridge. No existing runtime secret client is a provisioning
mechanism. Per-binding decisions are provider authorization policy attached to canonical bindings,
not a second consumer catalog. A no-grant decision is intentionally separate from observed access,
which requires evidence outside resource construction. No product secret names or principal lookup
rules enter the reusable component.

Source and package-only tests cover valid retention/network profiles, early scope/ownership/policy
rejection, aliases, missing access evidence, explicit absence, nonparticipation, strict JSON,
credential classification, parent/provider/group dependencies, exact scope and stable role GUIDs,
cancellation, and zero provider invokes. Run:

```bash
dotnet test src/Cohesive.Adapters.Pulumi.Azure.Tests -c Release
COHESIVE_NUGET_LOCAL_FEED=artifacts/nuget bash eng/test-pulumi-azure-package-consumer.sh <version>
```

The same tests are linked into the package consumer. Pack after building with `--no-build` to verify
the published boundary, including the bundled Durable Task SDK. Azure SDK dependencies stay in this
adapter. Live migration preview equivalence, actual identity evidence and authorized deployment are
consumer responsibilities; offline tests do not prove cloud permissions or availability.
