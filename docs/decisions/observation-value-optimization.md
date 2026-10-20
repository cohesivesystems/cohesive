---
kind: decision
status: accepted
authority: cohesive.observation-value.optimization
owners: [cohesive-core, cohesive-relations]
applies_to: [cohesive]
last_verified: 2026-10-18
supersedes: []
---

# Observation value optimization boundaries

## Decision

Optimize construction, interpretation and boundary conversion against the canonical value model.
Backwards compatibility is not a constraint, but a physical optimization must not accidentally change
semantic behavior. Intentional semantic changes need an explicit decision and updated conformance fixtures.
Builders, CLR projections, layouts, serializers and execution plans interpret that model; none is a
second semantic authority. Reuse existing projection, ownership and preparation mechanisms.

## Rules for the initial optimization stages

- Numeric equality uses the existing Int64/Decimal/Double canonical numeric domain. A finite double
  participates in decimal equality when its invariant round-trip text converts to Decimal and back
  to the same double. This is not arbitrary epsilon equality or the exact rational value of IEEE bits.
  Equal numeric values have equal ordinary hash codes across carrier kinds. Decimal scale and signed
  zero do not affect equality. Explicit constructors may retain different kinds.
- Temporal kinds remain distinct from strings. DateTimeOffset equality currently preserves canonical
  offset-bearing text, while ordering compares instants. This distinction is intentional for this
  optimization stage; instant-only equality is a separate semantic decision, not a storage change.
- Undefined and Null differ. Missing object membership differs from a stored Undefined member.
  Field assignment interprets Undefined as removal; it is not a way to store an Undefined member.
  Shape-bound ordinal storage interprets Undefined slots as absence according to its existing contract.
- Object member order is not value identity; array order and multiplicity are. Physical ordinal order
  must not change equality, canonical object ordering or declared fingerprint profiles.
- Immutable composition retains unchanged subtree storage. Mutable caller inputs are snapshotted;
  transferred storage must have one owner and must never be mutated or returned to a pool afterward.
- Ordinary JSON is explicitly lossy: it can collapse Undefined/Null, scalar kinds and binary/text
  distinctions. A contract does not always resolve nested kinds: unknown, opaque and schemaless
  regions exist. Retain the lossless tagged codec until its replacement covers those regions.
- Nonfinite doubles can exist in memory but have no portable canonical JSON encoding. Existing
  fingerprint operations retain their declared format/version and rejection policy.
- PortableValue states (Missing, Absent, Null, Unknown, Failed, Concrete) belong to its envelope.
  They cannot be erased by changing the payload representation.
- Boundary admission and contextual checks remain fresh. Immutable interpretation reuse is scoped
  to exact owner and interpretation dependencies, never a global tenant-content cache.
- Expression evaluation order, short-circuiting, empty aggregate behavior and first reported failure
  are observable. Selectors currently finish before numeric reduction starts. Streaming a selector
  directly into an aggregate is safe only when that ordering remains equivalent or is changed explicitly.

`ObservationValueSemanticContractTests` exercises representation-independent rules and the explicitly
lossy JSON boundary. Existing numeric, portable-value, ordinal-layout and expression suites remain the
detailed authorities. Tests intentionally do not pin physical object implementations or byte sizes here.

## Future identity format

A new lossless binary canonical format is deferred. Its specification must distinguish value content
identity from contract-qualified document identity and physical artifact identity. Physical layouts
must not influence content identity. Schema-dependent encodings must identify an exact resolvable
contract revision. The choice of flat-byte hashing versus Merkle hashing is explicit and versioned;
one is not silently substituted for the other. Fingerprints do not substitute for structural equality
where collision resistance alone does not meet the operation's contract.

## Measurement and sequence

Start with reproducible flat, nested, collection-heavy and bounded-large baselines; separate fresh
construction, cold preparation and warm execution. Account for required result storage separately
from temporary working memory, and do not describe allocated bytes as retained heap.

Then remove pre-scans and demonstrated round trips, preserving these rules. Use differential
semantic checks and deterministic allocation/bounded-memory guards for resource claims; timing
belongs to BenchmarkDotNet reports. Recursive layouts, generated views and alternative storage
follow only after measuring the remaining costs. No new external package is required by this stage.

## Boundary interpretations

The lossless tagged reader consumes tokens directly when kind precedes payload, as emitted by the
canonical writer. Payload-before-kind input remains valid through a local deferred document; this
is an operating distinction, not a second codec. Both paths use the same payload decoder. Contract
metadata and diagnostics continue through their registered serializer contracts. Duplicate and
unknown members are rejected. For inputs with multiple independent errors, codec error precedence
is not guaranteed; expression evaluation's first-failure rule above remains unchanged.

Prepared response readers belong to endpoint registration and retain one frozen serializer profile
and binary policy. They reuse the existing projection compiler, produce independent mutable graphs,
and may share immutable strings. Customized metadata, converters, case variants and unsupported
contracts use the owning serializer. Preparation never invokes a user converter's read operation.
An allocation-free whole-value gate preserves depth and JSON-encodability constraints even in
ignored members or nested fallback regions. It stops at the configured reading depth or the default
64-container fast-path boundary; deeper valid profiles fall back to the serializer. This deliberately
pays one structural traversal to preserve whole-input constraints.
Malformed UTF-16 must follow the same normalization as the JSON boundary, including in older fixed
profile readers; a shared allocation-free validator guards direct string reuse.

Compact checkpoint text uses the existing canonical writer with pooled UTF-8 staging and a final
owned string. It still materializes the serializer document. This removes an extra owned full-size
byte array without introducing another canonical format, bypassing recovery admission, or changing
the text-based store contract. A fully streaming store boundary remains deferred.
Internal checkpoint operations reuse frozen framework serializer profiles, including their metadata;
`CreateOptions` continues to return independent mutable profiles for callers. No checkpoint content
is stored in a global cache, and recovery still runs exact-plan compatibility validation every time.

## Traversal-local decoded text

Plain, typed and lossless tagged token readers share the existing traversal-owned string-reuse
mechanism. Token scopes begin on the stack and allocate a cache owner only after 64 eligible
tokens. Only contiguous property names and string tokens of at most 256 raw UTF-8 bytes are
eligible. Long or segmented tokens retain the ordinary parser path. Root ordinal field matching
continues to avoid materializing field names altogether.

The existing table has 256 direct-mapped positions and abandons a traversal with 32 initial misses
and no observed reuse. A hit requires the token decoder's ordinal decoded-text comparison; a hash
collision alone is never identity or evidence of valid text. Cache hits preserve malformed UTF-8
and escape rejection, including an explicit forced-collision regression. Different escape spellings
need not share storage. The existing process-randomized bucket hash affects physical hit rates,
never semantic identity or canonical output.

Token entries retain owned decoded strings rather than parser memory. The lease clears all
references on success or failure, including deferred tagged payload decoding. Each root read owns
its scope; there is no cross-read text cache. Small inputs avoid cache-owner allocation and warm
repeated input allocates primarily required result storage. Cold/concurrent pool misses may allocate
bounded scratch. Unique strings and oversized strings still allocate their required owned text.
This deliberately prioritizes repeated short text without an unbounded tenant-data interner.

## Fixed tagged metadata

The lossless decoder compares `$kind` tokens directly with their fixed UTF-8 wire spellings.
These names describe the format rather than retained user text and do not enter the traversal
string cache. `Utf8JsonReader.ValueTextEquals` preserves ordinal, case-sensitive decoded matching
for contiguous, segmented and escaped tokens. Unknown names are decoded only to report failure.
The existing enum and converter remain the semantic and wire authorities; payload parsing,
member-order independence, ownership, validation and canonical writing retain their contracts.
