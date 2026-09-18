# Azure Machine Learning native binding seam

`AzureMachineLearningBinding` associates an exact canonical workspace with existing native
storage, Key Vault and Application Insights resources. `AzureMachineLearningRegistryBinding`
checks an explicit disabled, managed or externally referenced registry selection. Neither class
registers resources, creates StackReferences, invokes providers or applies infrastructure.

## Authority and ownership

`Cohesive.Infra` owns logical resources, readiness relationships, physical identity, lifecycle
and provenance. Native Pulumi Azure Native 3.16.0 owns provider configuration and resource
options. The host selects subscription, group and an exact `pulumi/project/stack` owner.
Policies are immutable, JSON-portable associations; native SDK objects never enter canonical IR.

The workspace requires these declared identities, all managed by the same stack in one group:

| Policy node | Facility | Physical locator |
| --- | --- | --- |
| Workspace | `azure/machine-learning` | `azure/machine-learning/workspaces/<name>` |
| Storage | `azure/blob-storage` | `azure/storage/accounts/<account>/blob-services/default/containers/<container>` |
| Vault | `azure/key-vault` | `azure/key-vault/vaults/<name>` |
| Telemetry | `azure/application-insights` | `azure/application-insights/components/<name>` |

Declare `workspace.RequiresReady(storage)`, `workspace.RequiresReady(vault)` and
`workspace.RequiresReady(telemetry)`. These express supporting construction prerequisites, not
observed runtime health. Storage uses the existing Blob container identity and its owning account;
there is no second account-name catalog. More than one node claiming the selected physical identity
is rejected. Cross-stack supporting resources and key-based workspace datastore authentication are
not supported by this first association contract.

## Workspace workflow

1. Compile the canonical definition and target manifest; populate the attributed workspace policy.
2. Create supporting native resources through their existing owning construction paths.
3. Call `ConfigureWorkspace(plan, policy, subscription, nativeArgs, storage, vault, telemetry)`.
   It returns the same argument object, filling the canonical name and validated actual dependency
   IDs. Explicit native `Enabled`/`Disabled` public network access, `SystemAssigned` identity,
   `Identity` datastore authentication and the selected resource group are required.
4. Construct `new Workspace(logicalName, nativeArgs, nativeOptions)` in the host. Description,
   friendly name, HBI, tags, location and other SDK details stay native. Provider, parent,
   dependencies, protection and logical name stay on the original construction call.
5. Call `AttachWorkspace` and consume its checked `WorkspaceId`/`WorkspaceName`. It checks actual
   ARM identity and scope, Pulumi stack/project/type, supporting IDs, network and identity auth.
   The original workspace remains available as an explicit raw-output escape hatch.

Network/authentication settings are native inputs rather than duplicate policy enums. This seam
requires a supported explicit setting, but does not claim it independently audits an organization's
network policy. Azure ML job submission, datasets and training orchestration remain outside it.

## Registry workflow

Use facility `azure/machine-learning-registry`, physical identity
`azure/machine-learning/registries/<name>`, and one of three explicit policies:

- **Disabled:** no registry node, owner, group, reference or export keys in the policy. `Disabled`
  returns false and empty name. It never constructs resources or infers enablement from a name.
- **Managed:** canonical persistent resource with one managed lifecycle owner. Pass native
  `RegistryArgs` through `ConfigureManaged`, construct the native `Registry`, then `AttachManaged`.
  Explicit network, system-assigned identity and resource group are required. Native `RegionDetails`
  retains named system-created storage and ACR accounts, SKUs, HNS and public-blob choices. This
  seam creates no separate accounts and does not wrap the provider's region configuration schema.
- **Referenced:** canonical **external** resource, with the foreign stack's authority on the
  manifest's ordinary `Resource(...)` declaration. Existing Infra compilation derives a referenced
  lifecycle. Do not use `ReferencedResource(...)` with the same interpreter: that API describes a
  different managing interpreter, not a second stack running the same Pulumi target. The owner stack
  separately declares its own persistent resource. Pass the existing native StackReference to
  `AttachReference`; no locally managed registry or invented ID is returned.

References require an exact `organization/project/stack` name matching the declared foreign
`pulumi/project/stack` authority and explicit distinct enabled/name export keys. The enabled value
must be Boolean. The name must be a string: empty when disabled, equal to the canonical registry
name when enabled. Missing values, string booleans, contradictory disabled/name pairs and a different
source stack fail closed. A name alone never enables a potentially billable registry. These outputs
prove configured availability, not live Azure existence, authorization or runtime health. Subscription
and group on referenced policy are declared expectations, not remotely observed ARM evidence.

## Failure and lifecycle boundaries

Pure validation returns stable attributed diagnostics; configure/attach operations reject invalid
canonical associations with `AzureMachineLearningValidationException`. Missing required native
inputs fail before workspace/registry construction. Invalid resolved native values fault Pulumi
outputs with diagnostics that exclude provider values. Unknown preview values stay unknown;
secret classification and dependency graphs propagate through native `Output` composition.
Consume checked outputs when their validation is needed; raw SDK outputs bypass these guarantees.

Call configure once before construction and do not mutate the argument object concurrently.
Attachment checks cancellation before association; it cannot cancel prior registrations or roll
back resources. Any partial provider registration remains owned by the caller's normal Pulumi
preview/update lifecycle. Repeated attachment registers nothing. No retries or provider cleanup
are invented here.

## Design decision and validation

COH-123 extends the existing `Cohesive.Adapters.Pulumi.Azure` binding model. Existing Infra lifecycle,
readiness and manifest semantics fit as-is. `Cohesive.Adapters.AzureML` handles runtime ML work and
is not the owner of provisioning. A new parallel ML provider-options model and a generic construction
wrapper were rejected: they duplicate the SDK without adding an invariant. Existing ARM/URN checks
are reused, including extracting the repeated Pulumi-owner check shared with Entra and App Service.

`AzureMachineLearningTests` exercises native workspace options and stable dependency IDs, explicit
network/authentication, invalid supporting identities and links, owner/alias failures, all registry
modes, strict shared export validation, unknown/secret outputs, cancellation and portable policies.
Mocks reject all invokes; disabled/reference tests prove no native registry registration. The same
suite compiles against NuGet-only references in the package consumer. This document is included in
the no-build package payload.

```bash
dotnet test src/Cohesive.Adapters.Pulumi.Azure.Tests -c Release
COHESIVE_NUGET_LOCAL_FEED=/path/to/feed bash eng/test-pulumi-azure-package-consumer.sh <version>
```

Publish this package before ARI-550 adopts it. Ari retains shared-stack selection, training policy and
native configuration, then verifies baseline/adoption previews against the same backend. This change
contains no Ari bridge and performs no live deployment.
