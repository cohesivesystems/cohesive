# Shared Azure Blob Storage construction

`AzureBlobStorageConstruction` creates one Azure Storage account and the complete set of canonical
blob containers placed in that account. It uses the host's existing Pulumi provider, parent and state.
No key lookup, SAS generation, cloud observation or second lifecycle executor is introduced.

## Semantic authority and ownership

The exact `InfrastructureTargetDeploymentPlan` owns canonical bindings, physical placements,
capability/readiness proof and lifecycle authority. Each selected resource must use facility
`azure/blob-storage`, target `pulumi-azure-native/3.16.0`, and a physical identity of the form:

```text
azure/storage/accounts/<account>/blob-services/default/containers/<container>
```

`AzureBlobStoragePolicy.AccountOwner` explicitly selects one canonical container as the construction
anchor for its shared account. Every selected container must have the same exclusive managed Pulumi
lifecycle authority. The policy must include **every** manifest resource in that account and no
resources from another account. Duplicate physical aliases, missing members, external ownership,
an alternative managing interpreter and queue/table placements in that account are rejected.
An unbound container is valid and receives no inferred grant.

One account policy supplies location, resource group, tags and the preserved account logical name;
children only supply their canonical resource and Pulumi logical name. There are no per-container
account settings that could conflict and no second account/container naming catalog. Call `Register`
once for each complete account group. This is not a process-global registry: repeated registration
of the same logical names remains a Pulumi duplicate-resource error. Separate account groups can
be constructed independently; the host owns provider configuration and must pass its actual
subscription to construction and grant creation.

## Bounded account policy

The initial slice preserves this explicit construction profile:

- StorageV2, Standard_LRS, the selected single region and non-secret account tags.
- HTTPS-only, minimum TLS 1.2, account anonymous blob access disabled.
- Every container has `PublicAccess.None`.
- No retrieved account keys or connection strings with embedded credentials.

Other properties are left to the pinned Azure API defaults rather than claimed as configured
invariants. In particular, shared-key authentication is not explicitly disabled and public network
access is not changed. This slice does not model queue/table services or grants, SAS, hierarchical
namespaces, private endpoints, firewall rules, retention/lifecycle/immutability policies, alternative
SKUs, anonymous public containers, special `$` containers, per-child parents, aliases or imports.
Those require an explicit extension, not a hidden override. Account names accept 3–24 lowercase
letters/digits; ordinary container names accept 3–63 lowercase letters/digits/hyphens, with no
consecutive hyphens and alphanumeric ends.

## Construction and grants

The host verifies any Aspire handoff, creates immutable policy from the same plan, and calls pure
`Validate` before beginning its program. `Register` repeats validation and checks cancellation before
any registration. The account is registered once with an explicit resource-group dependency; children
consume its actual `Name` output, preserving account creation/deletion ordering. Construction order
is ordinal canonical resource identity, so reordering the input policy does not change emitted inputs.

```csharp
var policy = new AzureBlobStoragePolicy
{
    AccountOwner = artifactsId,
    LifecycleAuthority = authority,
    BlobContract = blobReadWriteContract,
    SubscriptionId = subscriptionId,
    ResourceGroupName = resourceGroupName,
    Location = location,
    AccountName = "existing-storage",
    Containers = [new(artifactsId, "existing-artifacts"), new(dataId, "existing-data")],
    Access = [new(artifactsBindingId, AzureBlobScope.Account),
              new(dataBindingId, AzureBlobScope.Account)],
    SourceReferences = [SourceReference.Create("migration-policy", "preserve-reviewed-account-scope")]
};
var storage = AzureBlobStorageConstruction.Register(plan, policy, subscriptionId,
    provider: existingProvider, resourceGroupDependency: resourceGroup);
var root = storage.ContainerEndpoint(dataId);
var grant = storage.AccessGrant([artifactsBindingId, dataBindingId], subscriptionId,
    workloadPrincipalId, existingRoleGuid);
// Register grant under the existing logical role name, parent/provider and dependencies.
```

Each participating canonical binding must have exactly one explicit scope decision. Missing/default
scope (`Unspecified`), duplicate/absent bindings, other contracts, outgoing resource bindings and
unknown workload participation are rejected. Nonparticipating workloads receive no access entry.
The caller-selected contract must mean blob read/write access; queue/table contracts are not
reinterpreted as blob access. The adapter does not infer principal identity from application names.

`AccessGrant` returns Azure **Storage Blob Data Contributor** role inputs. `Container` scope uses the
actual selected container ARM ID. `Account` scope uses the actual shared account ID and must already
be explicit in every selected binding's policy. Several binding IDs may share one existing role
assignment only when they have the same source workload and effective scope. Distinct containers at
container scope, mixed scope decisions, different workloads or duplicated IDs fail. Callers can thus
preserve a single account-wide migration grant without silently broadening a narrower declaration.
The host supplies each stable role GUID and principal, and owns role registration/dependencies.

`BlobEndpoint` comes from the account's actual provider `PrimaryEndpoints.Blob`, not a duplicated
Azure host suffix catalog. `ContainerEndpoint` combines it with the actual container name. Both carry
Pulumi dependencies and retain Output secret classification. They contain no newly obtained
credentials; secret-classified provider values remain secret. Principal input classification is also
preserved. A resolved provider response without a blob endpoint raises an explicit error.

## Reuse decisions and failures

`Cohesive.Adapters.AzureStorage` was evaluated: it owns runtime blob clients, dataset/code packaging
and target URI interpretation. It does not own resource lifecycle, account sharing or deployment
proofs. This constructor returns ordinary provider HTTPS roots that those runtime clients can
consume. It does not duplicate their URI schemes or perform runtime client registration.

The Cosmos and Durable Task constructors supply the existing validation/registration boundary.
Their stable deployment proof, subscription and provenance checks now share
`AzureConstructionPolicy.ValidateDeployment`; common ownership and Azure scope validation are also
reused. Blob account membership and binding coalescing stay here because their invariants differ
from Cosmos account/database and Durable Task scheduler/hub ownership. No generic graph executor
or second semantic IR is introduced. Policies are immutable data and support strict JSON round trips.

Validation returns deterministic `azure.blob-storage.*` diagnostics with the owner, manifest
fingerprint and source references. Registration raises `AzureBlobStorageValidationException` before
resources are created on invalid input. Pulumi owns retries, provider failures and cancellation after
registration begins; registration is not a transaction and this adapter performs no rollback or
state edits. Azure remains responsible for global name availability and service admission. Policy
and result objects can be read concurrently; host registration follows Pulumi's execution context.

## Verification and adoption

`AzureBlobStorageTests` covers shared-account construction, unbound containers, canonical names,
fixed account settings, explicit account/container scopes, coalescing rejection, omitted scopes in
JSON, conflicting ownership, aliasing, invalid names, unsupported queue/table placements,
nonparticipation, parent/provider/resource-group dependencies, deterministic input reordering,
cancellation, no key invokes and provider/principal secret propagation. Cosmos and Durable Task
conformance runs alongside it to protect the shared validation extraction.

The same tests compile against the NuGet package without source project references, including a
release-style `dotnet pack --no-build` check. The SDK remains Azure Native 3.16.0 (its Storage account
API is 2024-01-01); SDK types stay out of Cohesive.Infra.

COH-118 supplies the provider. ARI-545 must consume a published version, remove its replaced account,
container and endpoint construction, and preserve attributable account-wide blob grants and stable
role IDs. Ari's existing queue/table grants require a separate consumer/ownership decision or
explicitly isolated compatibility policy; this blob slice does not claim to adopt them. Compare fresh
credentialed previews against the same backend before any independently authorized apply. Package
conformance itself performs no Azure apply, destroy, DNS or state operations.
