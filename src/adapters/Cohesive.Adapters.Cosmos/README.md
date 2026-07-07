# Cohesive.Adapters.Cosmos

Azure Cosmos DB adapters for Cohesive storage, relation query compilation, aggregation, outbox records, and vector storage.

## Install

```bash
dotnet add package Cohesive.Adapters.Cosmos
```

## Use When

- You want Cohesive entity and observation storage backed by Azure Cosmos DB.
- You need relation query or aggregation plans compiled to Cosmos SQL.
- You want Cosmos-backed vector storage or process outbox persistence.

## Example

```csharp
using Cohesive.Adapters.Cosmos;
using Cohesive.Model;
using Cohesive.Relations.Queries;
using Cohesive.Storage;
using Microsoft.Azure.Cosmos;

await using var environment = await CosmosClientFactory.Shared.CreateDatabaseEnvironment(
    new CosmosDatabaseEnvironmentOptions(
        ClientOptions: new()
        {
            Endpoint = configuration["Cosmos:Endpoint"],
            UseDefaultCredential = true
        },
        DatabaseName: "cohesive-dev"),
    containers:
    [
        new ContainerProperties(id: "entities", partitionKeyPath: "/partitionKey"),
        new ContainerProperties(id: "leases", partitionKeyPath: "/id")
    ]);

var entityContainer = environment.ContainersByName["entities"].Item2;
var leaseContainer = environment.ContainersByName["leases"].Item2;

services.RegisterEntityRepository(LoadEntity.Instance, (_, _) =>
    new CosmosEntityOutboxRepository(
        entityDefinition: LoadEntity.Instance.Definition,
        container: entityContainer,
        leaseContainer: leaseContainer,
        partitionKeyPolicy: EntityPartitionKeyPolicy.FromField(nameof(LoadState.TenantId)),
        options: new CosmosObservationOutboxRepositoryOptions
        {
            InstanceName = "dispatch-worker"
        }));

var predicate = new EntityPredicate(new And<FieldPredicate>(
[
    new FieldPredicate(FieldPath.FromField(nameof(LoadState.Status)), new ExactValuePredicate("open")),
    new FieldPredicate(FieldPath.FromField(nameof(LoadState.CustomerId)), new ExactValuePredicate("customer-42"))
]));

CosmosSqlQuery sql = new CosmosSqlQueryCompiler().Compile(predicate);
QueryDefinition cosmosQuery = sql.ToQueryDefinition();
```

## Related Packages

- `Cohesive.Storage` for repository abstractions.
- `Cohesive.Relations` for query and aggregation plans.
- `Cohesive.AI` for vector storage contracts.
