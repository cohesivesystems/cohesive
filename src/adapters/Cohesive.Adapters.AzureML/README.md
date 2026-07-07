# Cohesive.Adapters.AzureML

Azure Machine Learning adapters for model training and dataset registry workflows.

## Install

```bash
dotnet add package Cohesive.Adapters.AzureML
```

## Use When

- You want to run Cohesive training requests against Azure Machine Learning.
- You need dataset and model registry integration backed by Azure ML workspace resources.
- You want model training to remain behind Cohesive.AI provider-neutral contracts.

## Example

```csharp
using Cohesive.Adapters.AzureML;

var azureMl = new AzureMLOptions
{
    SubscriptionId = configuration["AzureML:SubscriptionId"],
    ResourceGroupName = configuration["AzureML:ResourceGroupName"],
    WorkspaceName = configuration["AzureML:WorkspaceName"],
    RegistryName = configuration["AzureML:RegistryName"]
};

var trainer = azureMl.GetModelTrainer();
var registry = azureMl.GetRegistry();
```

## Related Packages

- `Cohesive.AI` for training, dataset, and model registry contracts.
- `Cohesive.Adapters.AzureStorage` for Azure Blob-backed training artifacts and dataset streams.
