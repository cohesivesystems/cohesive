# OpenTelemetry semantic observations

The optional `Cohesive.Adapters.OpenTelemetry.Assurance` package connects explicitly mapped semantic
observations to the bounded `TraceMonitor`. Core Assurance remains independent of OpenTelemetry.
This is the first capture integration for #558, stacked on the reference monitor in #566.

A native SDK integration test exports payment and shipment activities, ingests them into a monitor,
and restores its checkpoint between deliveries. The correlated payment obligation becomes satisfied;
the open production scope remains inconclusive. A separately tested committed reservation with
`Reserved = 12` violates the requirement `Reserved <= 10` and retains that violation after restore.

## Producer and capture host

```csharp
// Optional source; ordinary Cohesive instrumentation does not enable observation payloads.
var provider = Sdk.CreateTracerProviderBuilder()
    .AddCohesiveAssuranceInstrumentation()
    // Add the host's processor/exporter here.
    .Build();

// mappedRows contains exact semantic identities, inputs, order, and commit outcomes.
// Its one mapping alternative has no completeness frontiers.
using (var activity = AssuranceActivityAdapter.Source.StartActivity("reservation"))
    activity?.AddEvent(AssuranceActivityAdapter.CreateEvent(mappedRows));

// In the capture consumer; capturedActivity comes from the native SDK pipeline.
var next = AssuranceActivityAdapter.Apply(monitor, capturedActivity, cumulativeReference);
var checkpoint = next.Serialize();
// Persist checkpoint AND source delivery position atomically before acknowledgment.
var resumed = TraceMonitor.Restore(checkpoint);
var evaluation = resumed.Evaluation; // Existing ClaimEvidence.FromTrace accepts this result.
```

These are integration fragments: the application supplies the mapping and the host supplies delivery,
storage and acknowledgment. The adapter neither invents domain observations from arbitrary spans nor
implements a durable worker. SDK sampling and exporter delivery remain host-controlled.

## Contract and evidence boundary

The event name is `cohesive.assurance.observations`; its string attribute
`cohesive.assurance.observations.json` carries a versioned canonical observation document. The core
`TraceObservationBatch.Serialize/Deserialize` pair owns this format and portable value encoding.
Deserialization produces untrusted inputs, never verified evidence. Existing evaluation artifacts
keep their existing nested batch format.

Ingestion checks the complete observation contract, target, epoch and mapping-evidence reference
against the pinned monitor. Exactly one completely enumerated mapping is accepted. Complete mapping
enumeration says how the supplied records are interpreted; it does **not** assert complete capture.
All semantic events in one activity are applied atomically. Invalid later events cannot partially
advance the monitor. Duplicate occurrence handling, semantic ordering and predicate evaluation
remain owned by the monitor and batch interpreter.

Span timestamps, arrival order, status, trace IDs and sampling flags do not establish semantic
serialization order, successful commits or absence of missing observations. Unknown commit outcomes
remain unknown even on an OK span. Producer-supplied semantic order must already satisfy the monitor's
ordered atomic-scope contract; this adapter does not reorder interleaved deliveries.

This transport profile rejects **all completeness certificates**, at both encoding and ingestion.
Sampling, exporter failure or no arrivals can therefore never close a scope. Absence-based claims
need a separately qualified capture protocol with explicit completeness evidence; hosts may submit
that evidence through the monitor's native update boundary. Passing observed obligations is still
sampled execution evidence, not proof of universal correctness or backend isolation.

## Bounds, recovery and remaining work

The default payload limit is 1,048,576 UTF-16 characters, configurable per call. Ingestion checks the
aggregate payload across all semantic events before decoding each event; observation counts are also
bounded by the monitor profile. Encoding checks length after serialization, so the bound is not a
preallocation guarantee. Collector/backend attribute limits may be smaller and must be qualified by
the host; truncated documents are rejected. Retained monitor history remains bounded without eviction.

Malformed envelopes and binding mismatches use stable `TraceMonitorUpdateException` diagnostic codes;
capacity failures use the existing budget code. Cancellation or rejection leaves the immutable prior
checkpoint unchanged. Hosts must not acknowledge rejected deliveries.

The host owns cumulative alternative references and must retain raw captures and transport correlation
(trace/span IDs, delivery positions and producer envelope references) alongside checkpoints if needed.
The monitor retains semantic occurrences, not the original transport envelopes. Provenance therefore
requires both artifacts when tracing a result back to its original delivery.

Tests exercise the native SDK and an in-process exporter, including sampling and export loss. Remote
OTLP decoding, collector/backend qualification, durable checkpoint/position storage, completeness
protocols, correction propagation, richer operation/transaction histories, and executable Simulation
reconstruction remain work in #558. An imported counterexample is not a reproduced execution until
controllable inputs are replayed and the candidate emits new observations.
