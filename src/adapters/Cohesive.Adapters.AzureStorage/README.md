# Cohesive.Adapters.AzureStorage

Azure Blob Storage adapters for Cohesive AI training artifacts, dataset output streams, and blob target resolution.

## Install

```bash
dotnet add package Cohesive.Adapters.AzureStorage
```

## Use When

- You need to package source code or datasets into Azure Blob Storage for training workflows.
- You want named blob client resolution and target URI handling behind Cohesive abstractions.
- You are integrating Cohesive.AI training artifacts with Azure-based infrastructure.

## Example

```csharp
using Cohesive.Adapters.AzureStorage;

services.RegisterAzureBlobStorageClients(
[
    KeyValuePair.Create("Default", new AzureBlobStorageOptions
    {
        AccountName = "cohesivedevstorage"
    })
]);

services.RegisterAzureBlobContainers(
[
    KeyValuePair.Create("training", new AzureBlobContainerOptions
    {
        AzureBlobStorageName = "Default",
        ContainerName = "training-artifacts"
    })
]);

services.RegisterAzureBlobStorageOutputStreamProvider(
    name: "training-datasets",
    containerProfileName: "training");
```

## Related Packages

- `Cohesive.AI` for training artifact and dataset contracts.
- `Cohesive.Adapters.AzureML` for Azure ML training execution.
