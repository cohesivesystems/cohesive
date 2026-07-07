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

## Related Packages

- `Cohesive.Adapters.AzureML` for Azure Machine Learning training integration.
- `Cohesive.Adapters.AzureStorage` for training artifacts and dataset output streams backed by Azure Blob Storage.
- `Cohesive.Adapters.ONNX` and `Cohesive.Adapters.MicrosoftML` for concrete inference/tokenization integrations.
