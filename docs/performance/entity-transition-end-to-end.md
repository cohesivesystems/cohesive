# Entity transition end to end

## What is measured

One warm entity API transition, from request bytes to response bytes, through the real ASP.NET binding
(`EntityApiOperationBinding.Transition`): body deserialization, repository read, reference decision,
candidate preparation, commit and response writing. The fixture is a 13-field `Shipment` entity with
string, decimal, Int64, Boolean and timestamp fields. The `Assign` transition has one admission guard and three
field updates. Requests alternate between two carriers, so every invocation is an Applied, committed transition.

Two stores are measured:

- `InMemory`: the in-memory outbox repository. Isolates framework work from storage encoding and I/O.
- `Sqlite`: a file-backed, WAL-mode `SqliteEntityRepository`. Stored rows cross real byte boundaries; elapsed
  time is dominated by the durable commit.

Two response projections are measured. `ClrResource` reads observation fields into a CLR record and
serializes it, as conventional application code does. `Observation` writes the committed observation value
directly.

The stage rows call the same public operations the binding composes, so allocation can be attributed.
They are not a second pipeline, and their sum is not the endpoint total: binding overhead, the HTTP body
reader and response writing are only in the endpoint row. `stage:commit-unconditional` omits the
concurrency token so it can repeat without a read.

Run:

```sh
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --entity-transition-allocations 64
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*EntityTransitionEndToEndBenchmarks*'
```

The allocation harness reports the median of seven batches after 50 warm operations, using precise
process-wide allocation counters, because endpoint continuations may resume on another thread. Elapsed
microseconds are indicative only; use the BenchmarkDotNet class for timing.

## Results

Measured October 22, 2026, macOS 27.0.1 / Apple M5 Max arm64, .NET SDK 10.0.402, Release. The baseline is
local `main` at `3054381f`, including the nested-layout ingestion merge, with only the benchmark files added.
The candidate is this change on the same base. Values are bytes per operation.

| Store | Workload | Baseline B/op | Candidate B/op | Change |
| --- | --- | ---: | ---: | ---: |
| InMemory | endpoint (CLR response) | 25,040 | 20,840 | −16.8% |
| InMemory | endpoint (observation response) | 25,008 | 20,808 | −16.8% |
| InMemory | stage: decide (validate loaded + interpret) | 14,600 | 12,136 | −16.9% |
| InMemory | stage: decide / interpret | 13,456 | 10,992 | −18.3% |
| InMemory | stage: apply (patch + candidate state) | 5,528 | 4,152 | −24.9% |
| Sqlite | endpoint (CLR response) | 52,080 | 47,960 | −7.9% |
| Sqlite | endpoint (observation response) | 50,314 | 46,584 | −7.4% |
| Sqlite | stage: read | 10,160 | 9,922 | −2.3% |
| Sqlite | stage: commit (unconditional) | 16,829 | 16,825 | 0% |

Unchanged rows: in-memory read 176 B, in-memory commit 264 B, CLR response projection 520 B, observation
response 408 B. In-memory endpoint time went from about 44 µs to 37–39 µs per transition. SQLite endpoint time is
about 0.9–1.2 ms in both versions, dominated by the WAL commit and noisy at this sample size.

## Changes measured

1. **Batched patch projection** (`TransitionStateProjector.Apply`). Each patch used to call `WithField`, which
   rebuilt an `ImmutableSortedDictionary` along its path. Patches are now checked in order against pending
   assignments, and the candidate is built once with `WithFields`. If a later read targets an ancestor of a
   pending assignment, pending assignments are folded into the candidate first. Differential tests compare
   overlapping ancestor/descendant sequences and Null-then-Set chains with the sequential reference.
2. **Direct-field `WithFields`.** When every assignment addresses a direct field, the object is copied once into
   owned storage instead of building the nested assignment tree. Arrays, segments, immutable arrays and lists
   are read without an enumerator. Nested paths keep the existing algorithm. Tests compare five receiver
   storages and five enumerable forms with sequential `WithField`.
3. **One owned candidate dictionary.** `EntityDefinition.BuildEntityState` transfers its freshly normalized
   dictionary to the observation instead of snapshotting it again.
4. **No LINQ on candidate reads.** The reference interpreter's `ReadCandidate` finds the nearest written
   ancestor in one pass. It sorts descendants only when one exists, instead of running two sorted LINQ
   pipelines on every read.
5. **Lazy validator state.** `PortableExecutionValidator` allocates its diagnostics list and recursive-type set
   on first use, and pre-sizes its location cursor. Successful validations return the `Valid` singleton.
6. **Cached declared-JSON check.** `ObservationValue.FromObject` checked for `JsonConverterAttribute` with
   reflection on every projected value, including scalars. That fact is now prepared once per type.
7. **No Cosmos write copy.** `CosmosObservationContainerDocument.Observation` is typed as
   `IReadOnlyDictionary`. Immutable observation storage is serialized directly, instead of being copied into a
   new `Dictionary` on every entity write and outbox payload. The Cosmos emulator was not run; the serialized
   shape is unchanged because both types serialize as JSON objects in the same enumeration order.

The same changes also reduce `ObservationHostedQueryTests.WarmForwardingDoesNotAllocateWithPayloadSize`'s typed
forwarding of 1,024 strings from 189,968 B to 42,256 B. That path previously made a reflection attribute query for
every projected element. Observation forwarding went from 704 B to 640 B. The test's relative margin was lowered
accordingly; its absolute observation-forwarding ceilings are unchanged.

## What remains

The allocation that remains in the in-memory endpoint is mostly reference-interpreter evidence: trace events,
before/after `PortableValue`s and per-expression typed validation. Request and response serialization are
small. These are expected costs of an interpreter that records its evidence. The planned reduction is
compiled guard evaluation over a read-set slot layout, with this interpreter kept as the differential oracle.

Deferred quick wins:

- **Cosmos conditional writes read the document again** to map the entity token to an ETag. Removing that
  round trip requires carrying the provider ETag on the loaded snapshot, or making the ETag the concurrency
  token. That changes token semantics and needs emulator qualification.
- **Layout-preserving `WithFields` on ordinal storage** was not done. `IOrdinalObservationFieldReader`
  implementations promise validated-observation semantics, so a patched slot vector must not claim the
  layout's shape before revalidation. This needs an explicit distinction between unvalidated and admitted
  storage.
- **Validation is repeated** across the loaded snapshot, the interpreter's activation frame and candidate
  construction. Removing the duplicates requires admission evidence on the observation storage rather than
  local skips.
