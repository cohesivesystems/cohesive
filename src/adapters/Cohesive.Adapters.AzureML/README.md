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

## Training submission recovery

`AzureMLModelTrainer` realizes `TrainingJobSubmission` with a deterministic Azure ML job name derived from the
complete logical submission identity. The job retains the original logical identity and the versioned exact-request
fingerprint in `cohesive.submissionId` and `cohesive.requestFingerprint` properties.

Submitting the same identity and request returns the existing job. Reusing the identity with different request
content, or colliding with a provider job that lacks Cohesive submission evidence, fails with
`TrainingJobSubmissionConflictException`; the adapter never treats that job as accepted for the caller.

`ReconcileSubmissionAsync` performs a direct lookup by deterministic job name:

- a matching job returns `Accepted`;
- an authoritative not-found result returns `ConfirmedAbsent`;
- an Azure request failure returns `Unresolved` with portable error and retryability evidence; and
- mismatched identity or fingerprint evidence throws `TrainingJobSubmissionConflictException`.

This is stable-identity idempotency, not a claim of physical exactly-once delivery. The calling durable workflow
retains attempt, timeout, cancellation, and retry authority and must reuse the exact same `TrainingJobSubmission`
through reconciliation and any admitted retry.

## Related Packages

- `Cohesive.AI` for training, dataset, and model registry contracts.
- `Cohesive.Adapters.AzureStorage` for Azure Blob-backed training artifacts and dataset streams.
