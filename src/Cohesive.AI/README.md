# Cohesive.AI

AI-oriented semantic contracts for inference, training, vector storage, text processing, tokenization, ontology modeling, and model registries.

## Install

```bash
dotnet add package Cohesive.AI
```

## Use When

- You need provider-neutral contracts for embeddings, pair scoring, graph scoring, feature-vector scoring, or model training.
- You want to model semantic concepts, ontologies, closure rules, and concept grounding.
- You need reusable text/token utilities or vector store abstractions without taking a specific cloud or model runtime dependency.

## Example

```csharp
using Cohesive.AI.Semantics;

var ontology = new OntologyBuilder()
    .AddConcept(new("party.role", "Party Role"))
    .AddConcept(new("party.ship-to", "Ship To"))
    .AddParent(childConceptId: "party.ship-to", parentConceptId: "party.role")
    .AddScopedMeaning(scope: "edi.n101", symbol: "ST", conceptId: "party.ship-to")
    .Build();

var closure = OntologyClosure.Create(ontology);
var isPartyRole = closure.IsSubConceptOf("party.ship-to", "party.role");
```

### Reconciliable training submissions

Training submission is identified independently of any physical attempt. Bind the workflow's stable logical
identity to the exact request once, then use the same submission for dispatch and ambiguity recovery:

```csharp
using Cohesive.AI.Training;

var submission = new TrainingJobSubmission(
    submissionId: "tenant/acme/training-run/42/submission",
    request: trainingRequest);

var job = await trainer.SubmitAsync(submission, cancellationToken);
var reconciliation = await trainer.ReconcileSubmissionAsync(submission, cancellationToken);
```

`TrainingJobSubmission` snapshots dataset bindings and derives a versioned request fingerprint. Dataset bindings
are canonicalized by their ordinal names because list order is not provider meaning; duplicate names are rejected.
Every other request value, including provider configuration text, participates exactly. An adapter must return the
same provider job for a repeated identity and fingerprint, and must throw `TrainingJobSubmissionConflictException`
when the identity is already bound to different or missing request evidence.

Reconciliation returns one closed result: `Accepted`, `ConfirmedAbsent`, or `Unresolved`. A workflow may safely
retry only according to its durable recovery policy and the returned evidence; physical attempt identity must not
replace the stable logical submission identity.

### Reconciliable training cancellation

Training cancellation is a provider capability separate from cancelling the caller's wait. A workflow binds its
stable cancellation-operation identity to the accepted provider job and invokes the stronger trainer capability:

```csharp
var cancellation = new TrainingJobCancellation(
    cancellationId: "tenant/acme/training-run/42/cancel/1",
    jobId: job.JobId);

if (trainer is not ICancellableModelTrainer cancellableTrainer)
    throw new InvalidOperationException("The selected trainer cannot cancel accepted provider jobs.");

var cancellationResult = await cancellableTrainer.CancelAsync(cancellation, cancellationToken);
```

`Accepted` means only that the provider accepted cancellation; it is not proof that the job is terminal. Continue
observing the job until it reports `Cancelled`, `Completed`, or `Failed`. `AlreadyTerminal` retains that exact
provider-owned state so a cancellation race cannot overwrite success or failure. `NotFound`, `Rejected`, and
`Unresolved` remain distinct recovery evidence. A provider's intermediate cancellation state is represented as
`TrainingJobStatus.CancellationRequested`, not collapsed into `Running`.

An implementation must treat the same cancellation identity and job identity as one logical operation. If it
observes that cancellation identity already bound to another job, it throws
`TrainingJobCancellationConflictException`. Cancelling the method's `CancellationToken` only stops the caller's
wait; it never asserts that the provider job stopped.

## Related Packages

- `Cohesive.Adapters.AzureML` for Azure Machine Learning training integration.
- `Cohesive.Adapters.AzureStorage` for training artifacts and dataset output streams backed by Azure Blob Storage.
- `Cohesive.Adapters.ONNX` and `Cohesive.Adapters.MicrosoftML` for concrete inference/tokenization integrations.
