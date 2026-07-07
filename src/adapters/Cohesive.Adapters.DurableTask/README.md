# Cohesive.Adapters.DurableTask

Azure Durable Task integration for running Cohesive process definitions as durable orchestrations.

## Install

```bash
dotnet add package Cohesive.Adapters.DurableTask
```

## Use When

- You want a durable execution host for `Cohesive.Processes`.
- You need Azure Storage-backed Durable Task workers and process execution repositories.
- You want process orchestration infrastructure to remain behind Cohesive process runtime contracts.

## Example

```csharp
using Cohesive.Adapters.AzureStorage;
using Cohesive.Adapters.DurableTask;
using Cohesive.Adapters.DurableTask.AzureStorage;

services.AddAzureStorageDurableTaskEngine("orders", durable => durable
    .WithDefinitions(new DurableTaskProcessDefinitionRegistry()
        .Register(new FulfillOrderProcess().Define().Definition))
    .ConfigureAzureStorage((sp, settings) =>
        settings.ConfigureDurableTaskAzureStorage(sp, new DurableTaskAzureStorageSettings
        {
            TaskHubName = "orders",
            AzureStorageName = AzureBlobStorageOptions.DefaultName
        })));
```

## Related Packages

- `Cohesive.Processes` for process definitions and runtime contracts.
- `Cohesive.Adapters.AzureStorage` for Azure Storage configuration support.
