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

## Training cancellation recovery

`AzureMLModelTrainer` implements `ICancellableModelTrainer` through Azure ML's command-job cancellation operation.
It first observes the exact provider job:

- `Completed`, `Failed`, or `Cancelled` returns `AlreadyTerminal` without issuing cancellation;
- `CancelRequested` returns `Accepted` without redundant dispatch;
- authoritative absence returns `NotFound`; and
- any other observed state is eligible for Azure ML cancellation.

A successful Azure cancellation request returns `Accepted`, which remains non-terminal evidence. If dispatch fails,
the adapter observes the job again before classifying the attempt. A terminal race or `CancelRequested` state wins
over the transport failure. Absence after a cancellation 404 is `NotFound`; a still-observable job plus the same 404
is `Unresolved` because the provider evidence conflicts. Throttling, timeout, conflict, transport, and server failures
remain retryable `Unresolved` evidence, while deterministic authorization or validation refusal is `Rejected`.

Azure ML's `CancelRequested` status maps to `TrainingJobStatus.CancellationRequested`, never ordinary `Running`.
The calling workflow must continue status observation until the provider reports terminal state. Cancelling the
method's `CancellationToken` stops only the local wait and does not prove that the Azure ML job stopped.

## Related Packages

- `Cohesive.AI` for training, dataset, and model registry contracts.
- `Cohesive.Adapters.AzureStorage` for Azure Blob-backed training artifacts and dataset streams.
