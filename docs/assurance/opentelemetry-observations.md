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
var capture = AssuranceActivityAdapter.Apply(monitor, capturedActivity, cumulativeReference);
var checkpoint = capture.Monitor.Serialize();
var producerReferences = capture.EnvelopeReferences;
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
`TraceObservationBatch.Serialize/Deserialize` pair owns this standalone input envelope and portable value encoding.
Deserialization produces untrusted inputs, never verified evidence. Existing evaluation artifacts
keep their existing nested batch payload under their own versioned envelope. Use the batch APIs
when transporting unevaluated inputs; use `TraceEvaluation` or `TraceMonitor` serialization APIs
when retaining evaluated results/checkpoints. Both reuse the same batch shape and `AssuranceJson`
policy; there is no second portable-value schema. `TraceObservationContract.SameContent` owns
exact ordered contract comparison, independently of JSON formatting. Nested ordered arrays use one
internal sequence-comparison helper, including default-versus-empty behavior. Content comparison is
explicit; generated record equality is not advertised as deep content equality. Dictionary comparison
for semantic observations remains unordered and therefore has a distinct contract.

`TraceMonitor.AdvanceCaptured(batches, cumulativeReference)` owns the shared capture boundary for
OTel, future OTLP decoders and durable transports. It validates the capture profile, checks the complete
observation contract, target, epoch and mapping-evidence reference against the pinned monitor, and
bounds total supplied observations and envelope count using **independent counters** with the same
`MaxObservations` ceiling. This reference profile intentionally reuses that configured ceiling to bound
producer-reference receipts without adding a separate tuning option. Empty envelopes consume only
per-call envelope capacity, never retained row capacity. Envelope capacity resets on every capture;
retained rows remain subject to the monitor's existing history bound. A regression test accepts empty
envelopes alongside rows and continues until the retained row limit.
`TraceObservationBatch.ValidateCapture` is the same profile check used by producers.
The adapter only decodes activity events and enforces transport source/payload bounds. Exactly one completely enumerated mapping is accepted. Complete mapping
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

All observation rejections use the sealed `TraceObservationException`, carrying a `Diagnostic` from
core `TraceEvaluationCodes`. There are no boundary-specific exception subtypes. `TelemetrySource`
identifies a wrong activity source; `TelemetryBinding` identifies a mismatched semantic contract,
target, epoch or mapping evidence. Both are permanent for unchanged input and configuration and
follow the quarantine policy below. The public diagnostic constructor lets adapters report their
boundary failures without pretending a monitor executed. Cancellation or rejection leaves the immutable prior
checkpoint unchanged. Disposition belongs to the capture host:

- Envelope, binding and `TelemetryPayloadLimit` rejections are permanent for unchanged input and
  configuration. Durably quarantine the complete activity, delivery position and diagnostic, then
  acknowledge that transport delivery. Record a capture gap; never advance the monitor or claim the
  rejected observations were accepted. If quarantine persistence fails, retry persistence before acknowledgment.
- Monitor budget exhaustion requires backpressure and an explicit capacity/profile decision; replaying
  unchanged input against the same full monitor cannot help. Preserve the delivery until that decision,
  or durably quarantine it with a capture gap. Never silently evict accepted history.
- Cancellation and transient host storage/transport failures may be retried from the last durable position.
  Semantic order, duplicate-content and frontier violations require diagnosis/correction, not blind retries.

`CreateEvent` bounds each event, while `Apply` bounds the aggregate activity. Hosts should use one
semantic event per activity where practical. Multiple individually valid events can exceed the total;
this is a permanent payload-limit rejection under the current configuration, covered by a regression test.
A host may explicitly revise the bound and replay the quarantined activity, retaining the original rejection.

The host owns cumulative alternative references. `Apply` returns the core `TraceCapture`, containing
both the accepted monitor and each producer envelope reference in event order, including empty envelopes
and duplicates. Persist these references with the source delivery position and checkpoint. They are
producer assertions, not content-digest verification; the adapter checks presence and retains exact values.
Empty captures (including activities with no semantic events) still honor `cumulativeReference` through
the native monitor update. A new reference creates a checkpoint with identical observations and the
new alternative reference; replay with the current reference returns the existing monitor. The exact
checkpoint artifact identity remains `capture.Monitor.Reference()`, distinct from the alternative reference.
Persist the returned monitor, not an assumed checkpoint synthesized from the call's arguments.

The host still retains raw captures and trace/span correlation for auditing conflicting producer references.
The monitor retains semantic occurrences, not original transport envelopes or an unbounded provenance ledger.

Tests exercise the native SDK and an in-process exporter, including sampling and export loss. Remote
OTLP decoding, collector/backend qualification, durable checkpoint/position storage, completeness
protocols, correction propagation, richer operation/transaction histories, and executable Simulation
reconstruction remain work in #558. An imported counterexample is not a reproduced execution until
controllable inputs are replayed and the candidate emits new observations.
