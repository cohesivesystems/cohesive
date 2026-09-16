# Cosmos SQL construction

`AzureCosmosConstruction` interprets one exact `azure/cosmos-db` database placement into a
single-region account, a SQL database and explicitly declared containers. It uses the existing
Azure Native 3.16.0 SDK (Cosmos REST API 2025-10-15) and Pulumi lifecycle. It performs no data-plane calls, account-key reads,
provider invokes or independent state management.

## Authority and integration

The compiled `InfrastructureTargetDeploymentPlan` remains authoritative for the canonical resource,
repository bindings, target, capability/readiness evidence and lifecycle owner. Its physical identity is
`azure/cosmos-db/accounts/<account>/databases/<database>`. Those names are never supplied a second time.
`AzureCosmosPolicy` supplies attributable deployment policy, preserved Pulumi logical names and a
container projection from the consumer's existing authoritative topology. It is immutable and can be
round-tripped with `StrictDocumentJson.CreateOptions`; reject unknown JSON members when importing it.
Do not maintain a second container catalog for provider construction.

First validate the exact Aspire handoff, if present. Read the host's explicit subscription configuration
and pass the same subscription to policy validation and registration. Unlike the generated Durable Task
provider, Cosmos preserves the existing Azure Native provider: null means the host's default provider;
a supplied provider must already be configured for that subscription. The adapter checks the declared
scope but cannot introspect or attest an arbitrary provider's credentials/configuration. Never substitute
an ambient CLI subscription for the host's explicit configuration.

```csharp
handoff.RequireExactPlan(plan);
var subscription = Guid.Parse(new Pulumi.Config("azure-native").Require("subscriptionId"));
// policy is projected from the consumer's topology and attributed environment configuration.
var cosmos = AzureCosmosConstruction.Register(plan, policy, subscription,
    resourceGroupDependency: existingResourceGroup);

var repositoryBinding = cosmos.RepositoryBindings.Single(b => b.Source == workloadId);
var grant = new Pulumi.AzureNative.CosmosDB.SqlResourceSqlRoleAssignment("existing-role-name",
    cosmos.AccessGrant(repositoryBinding.Id, subscription, existingPrincipal, existingRoleGuid),
    new Pulumi.CustomResourceOptions
    {
        DependsOn = [existingWorkload, .. cosmos.Containers.Values]
    });
var endpoint = cosmos.Endpoint;
```

Preserve the existing provider, common parent, names, tags and grant options. The account explicitly
depends on a resource group supplied by the caller. Database construction consumes `account.Name`;
container construction consumes `database.Name`, preserving Pulumi output dependencies. Containers
register in ordinal physical-name order. Index path order is preserved exactly, never sorted.
Distinct per-resource parents, imports, aliases and additional resource options need an explicit
extension before adoption rather than silent loss. No component parent or provider is added.

## Policy boundary

This first slice supports SQL/GlobalDocumentDB, Standard account offer, one region, no zone redundancy
or automatic failover, explicit Session/Eventual/ConsistentPrefix/Strong consistency, explicit free-tier
and local-auth policy, and explicit public-network enablement. Free-tier eligibility, account uniqueness,
quota and cloud authorization remain Azure admission decisions.

The database has manual shared throughput. Each container either inherits it (`Throughput = null`) or
has explicit dedicated manual throughput. RU/s must be at least 400 and a multiple of 100. The slice
supports one Hash partition path, default indexing with optional composite indexes, no TTL and no unique
keys. Each composite index has 2–8 distinct paths with explicit ascending/descending ordering.
Containers include non-entity infrastructure such as an inbox when declared by the caller; no entity
reflection, automatic inbox insertion or repository enumeration occurs.

Supported database/container names are a deliberately restricted identifier subset: letters, digits,
underscore and hyphen, 1–255 characters. Paths use slash-separated identifier segments beginning with a
letter or underscore. More general quoted names, escaped paths, wildcard indexing, hierarchical keys,
autoscale/serverless, custom included/excluded indexes, TTL, unique keys, vector/analytical configuration,
BoundedStaleness, multiple regions, private endpoints and IP rules require an adapter extension. They
must not be approximated or silently mapped onto this slice. Disabling public access does not construct
private connectivity. Existing resources needing other settings are not supported migrations.

Azure's [container resource contract](https://learn.microsoft.com/en-us/azure/templates/microsoft.documentdb/2025-10-15/databaseaccounts/sqldatabases/containers)
is the provider schema reference; the pinned Azure Native SDK controls emitted resource tokens and inputs.

## Access scope and bootstrap consumers

`Access` requires exactly one explicit decision per participating repository binding. Supported scope
levels are account, selected database, or a named declared container. Only container scope accepts a
container name. Missing, duplicated, unknown and non-participating binding decisions fail validation.
Account scope is a deliberate broad grant, never inferred from repository access.

`AccessGrant` creates **Cosmos SQL data-plane** Data Contributor arguments, not management-plane RBAC.
The role is `${actualAccountId}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002`.
Database scope uses `${actualAccountId}/dbs/${databaseName}` and container scope appends
`/colls/${containerName}`. It does not use ARM `sqlDatabases/containers` child IDs for data permissions.
Principal inputs retain Pulumi secret classification. The caller retains principal-to-workload association,
assignment GUID, role logical name, provider, parent and explicit dependencies.

`DataPlaneScope` and `DataContributorRoleId` also expose validated outputs for explicitly owned bootstrap
consumers that are outside canonical workload bindings. They grant nothing themselves. For example, an
operator's catalog-seeder assignment remains consumer policy, with its own evidence and identity; it
must not masquerade as a workload binding or silently inherit account-wide access.

## Failure and lifecycle behavior

`Validate` returns normalized `azure.cosmos.*` diagnostics containing manifest and policy provenance.
`Register` repeats validation and throws `AzureCosmosValidationException` before provider registration
when the plan, resource owner, topology or access policy is unsupported. An account shared by another
canonical resource is rejected; this slice does not create the same account for multiple database owners.
Cancellation is checked before construction. Once registration begins, Pulumi owns cancellation, retries,
partial failure and recovery through the same backend. The adapter cannot make multi-resource updates atomic.
Do not put credentials or sensitive values in policy/source-reference metadata.

## Fit and qualification

The existing Cosmos runtime adapter's `CosmosDatabaseEnvironment` can create data-plane test environments
and validates partition compatibility; it cannot take over a Pulumi-managed production account's lifecycle.
Its storage commit/inbox contracts rely on `/partitionKey`, no TTL and no additional unique keys. This
provider accepts the caller's exact partition declaration without introducing another runtime encoding model.
The existing Infra compiler and binding catalog are reused unchanged. Shared resource-group/tag validation,
managed-owner selection and canonical binding traversal are internal package mechanisms shared with Durable Task;
Cosmos's account aliasing, container topology and data-plane scopes remain facility-specific.

Source and package-only tests cover explicit grant levels, invalid scopes/topology/policies, wrong target,
subscription and authority, secret propagation, deterministic construction, parent/provider preservation,
resource-group ordering, non-participation, cancellation and JSON roundtrips. Run:

```bash
dotnet test src/Cohesive.Adapters.Pulumi.Azure.Tests -c Release -m:1 -nodeReuse:false
```

Pack after building with `dotnet pack --no-build` and run
`eng/test-pulumi-azure-package-consumer.sh <version>` against the package feed. The consumer has no source
project references. Publish before ARI-544 upgrades; Ari must preserve its 14-container topology, explicitly
retain or separately revise existing broad account grants and compare fresh credentialed previews before
claiming migration equivalence. Mocks do not establish live Azure authorization, quota or preview equivalence.
