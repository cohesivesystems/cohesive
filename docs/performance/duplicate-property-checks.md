# Allocation-free warm duplicate-property checks

The shared strict JSON boundary previously allocated a HashSet for every object and decoded
every property name twice while traversing portable documents. In Ari's training closure,
those checks alone accounted for about 63 MB of cumulative allocations before type compaction.
Correctness requires ordinal equality of decoded names, the first depth-first duplicate location,
and correctly escaped JSON Pointer segments; integrity/admission checks cannot skip imported data.

The scanner now writes the existing JsonElement into leased pooled UTF-8 storage and compares
UTF-8 names directly. An open-addressed per-object index uses stack storage for small objects
and pooled arrays for wide objects. Hashes select candidates only: ValueTextEquals proves decoded
name equality, so escaped aliases and hash collisions cannot bypass rejection. Large escaped names
use pooled scratch bytes. Diagnostic strings are decoded/formatted only when a duplicate is found.
The scanner preserves source order and reports nested duplicates before later parent duplicates.

Existing CanonicalJsonWriter remains the authority for canonical ordering and numeric semantics.
The duplicate scanner performs no normalization, fingerprinting or semantic validation. It avoids
adding a competing JSON model or weakening strict import guarantees. The existing pooled byte
writer is reused; at most eight idle scanner leases retain writer metadata. Return resets writer
state, releases and clears payload/name buffers, and removes all document references. Concurrent
scans exclusively own their scratch state; failures return every rented buffer in finally.

The successful warm path allocates zero managed bytes in a regression covering repeated scans of
32 objects with 128 fields each, nested arrays and Unicode property names. Initial pool population,
concurrent pool misses and error diagnostics can allocate; this is not a cold zero-allocation claim.
The JsonElement entry point still copies UTF-8 into pooled storage, trading bounded scratch work
for removal of per-property strings and sets. Pool retention belongs to the bounded codec cache
and standard shared ArrayPool, not a cache of documents or validation results.

Ari isolated admission qualification, Release/.NET 10.0.201 on macOS:

| Boundary | Compact types only | Compact types + scanner |
| --- | ---: | ---: |
| First admission, cumulative allocated bytes | 331,131,760 | 294,080,464 |
| Warm repeated admission, cumulative allocated bytes | 12,166,352 | 11,848,696 |

The combined sample measured 615 ms first admission and 22 ms warm admission. Catalog authoring
before admission is excluded. These are local cumulative allocation samples, not retained heap,
production latency or CI suite duration claims. The prepared-projection PR is not part of this baseline.
Regression coverage retains ordinal escaped-name equality, first failure paths, exact canonical wire
checks, concurrent isolation, invalid imports and buffer cleanup on errors.
