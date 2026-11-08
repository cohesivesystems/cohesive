# Tender acceptance distillation sketch

These internal source files are compiled directly by `Cohesive.Tests`; they introduce no package or public
framework API. They author candidate machines using `Machine.Define`, plus a bounded effect-order extraction
and an in-memory failure-injection interpretation. They do not reference or execute the archived application.

Read the [extraction dossier](../../../docs/research/tender-acceptance/README.md) and
[coordination model](../../../docs/research/tender-acceptance/coordination.md) before interpreting the model.
In particular, positive receipt validation and reconciliation are proposed improvements, not recovered guarantees.

```sh
dotnet test src/Cohesive.Tests --filter FullyQualifiedName~TenderAcceptanceCharacterizationTests
```

The fluent source is authoritative for the proposal. Tests verify deterministic canonical Machine documents,
fingerprints and serialization. The source-derived trace fixtures pin only the documented clean acceptance
slice; live differential testing against the original application is still required for migration.

Domain clarification D01 assigns customer-ordered stops to the load and actual execution history to trips.
The proposed machines do not require an original trip. The observed trace still includes the legacy trip
notification so that characterization does not silently rewrite the behavior being extracted.
