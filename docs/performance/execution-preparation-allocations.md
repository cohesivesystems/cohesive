# Execution preparation: final implementation and qualification

The CLR mapper, TypeRef serializer registry and canonical JSON writer remain the semantic authorities.
This change reduces repeated preparation and temporary representations; instance-validation plan caching
is a separate next investigation. No packages are published or Ari pins upgraded here.

## Ownership and failure behavior

Reflection and inferred default root contracts use one weak-key preparation helper. A per-key slot
publishes only successful preparation, with lock-free warm reads. Failed factories leave that same slot
retryable rather than caching exceptions or removing entries that a concurrent caller may have prepared.
Stable negative metadata outcomes remain valid cached results. Property/type inspection and portable
JSON-kind discovery also use weak ownership; a collectible dynamic CLR type test covers the entire
mapper path. Holding a key or returned reflection metadata can still retain its CLR type.

Default roots without explicit mappings or occurrence metadata share immutable projections. Completed
cycle-free structural children are reused within traversal; recursive projections retain ancestor-specific
diagnostics. Framework TypeRef graphs are audited for deep immutable public state. Caller-provided
contracts are not promoted into default-root caching.

Compact types deduplicate using exact canonical UTF-8 equality; hashes only choose dictionary buckets.
Duplicate candidates create neither owned text keys nor canonical leaf JSON. Unique parents retain
serializer payloads and converter-recorded reference tokens. Replay checks token bounds and parses
replaced digits back to the provisional index before writing. Serialization restores capture/depth stacks
in a finally block, removes failed in-progress identity entries, and preserves retryability.

## One canonical traversal

JsonNode and JsonElement use one generic structural walk for object sorting, duplicate detection,
array ordering, set validation and diagnostic paths. Small value adapters expose storage access without
boxing or full-tree conversion. Numeric JsonElement handling is shared; authored typed node scalars
retain their established serializer/ObservationValue contract. The immutable owned-byte convenience
API is internal to semantic blocks and qualification, while Relations uses the streaming path for hashes.
Execution normalization, reference numbering and execution/relation fingerprints retain strict canonical
semantics. Temporary pooled buffers never escape the operation.

## Measurements and tests

A warmed declaration with 64 equivalent 128-field parents originally allocated 3,931,064 bytes during
document creation. Canonical-byte lookup reduced it to about 2.00 MB, with two retained type entries and
one shared decoded parent type. A 2,200,000-byte regression budget fails against the preceding
string-key implementation. This compares the byte-key change, not every commit against main.

A fresh final Ari source-assembly run measured 111,334,720 bytes for root catalog authoring,
76,783,224 for first deployment-catalog access, and 685,544 for warm access. Root authoring excludes
lazy document projection; first access includes it. These are cumulative allocated bytes, not retained
heap, released-package measurements or end-to-end latency claims. Do not add nested scopes.
All 164 canonical document bodies remain byte-for-byte identical after the review fixes.

Final representative BenchmarkDotNet reports (macOS Arm64, SDK 10.0.201/runtime 10.0.5) cover:

- [CLR mapping](execution-preparation-final/clr-mapping.md): warm root reuse and fresh explicit-mapper
  traversals for flat, nested, collection and large shapes.
- [Compact document creation](execution-preparation-final/compact-types.md): five workloads including
  duplicate-heavy declarations, with declaration construction excluded and serializer metadata warmed.
- [Canonical JSON](execution-preparation-final/canonical-json.md): flat, repeated, escaped and unique
  property layouts. Allocation guards protect reuse, including the no-full-tree-copy path.

CSV files alongside each report retain machine-readable measurements. Short timing jobs do not
establish application latency improvements. Intermediate reports were removed rather than maintained
as competing descriptions of current code. Core/Relations/Ari tests cover canonical numeric annotations,
UTF-8 escaping, ordering, digit-width changes, cycles, corrupted replay tokens, codec ownership,
cache failure/concurrency, collectible metadata, and imported integrity/unused-entry validation.

No skips or timeout increases were added. Ari qualification overlays are restored byte-for-byte.

Final qualification: 4,369 Core tests pass (33 existing skips), 1,106 Relations tests pass,
and 855 Ari engine tests pass (18 existing skips). The focused compatibility suite passes 80
tests; additional tests prove corruption rejection, traversal-stack cleanup, retry/concurrent
success publication, and collectible CLR metadata ownership.
