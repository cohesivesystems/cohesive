# Execution preparation: final implementation and qualification

The CLR mapper, TypeRef serializer registry and canonical JSON writer retain their existing contracts.
ShapeGraph and TypeRef declarations remain the semantic authorities for instance validation.
This change reduces repeated preparation and temporary representations. Nested instance validation
now reuses field-name preparation for detailed failures and avoids repeated case-insensitive scans;
graph-bound named and child links are also prepared lazily. Full validator code generation remains
a separate investigation. No packages are published or Ari pins upgraded here.

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

Final qualification: 4,382 Core tests pass (33 existing skips), 1,106 Relations tests pass,
and 855 Ari engine tests pass (18 existing skips). The focused compatibility suite passes 80
tests. The final instance-validation changes include 28 focused checks. Additional tests prove corruption rejection, traversal-stack cleanup, retry/concurrent
success publication, and collectible CLR metadata ownership.


## Nested instance validation

`ObservationValidator` retains its declared type dispatch, exact graph resolution, required/nullability/
cardinality rules, depth limits and lazy diagnostic paths. Inline objects and named structural objects
now reuse one local case-insensitive property index after an exact lookup misses. Exact matches still
win, and fallback retains the first source-enumeration match. Values and indexes are discarded after
that object check. Unknown-property diagnosis lazily prepares a case-insensitive name set in a weak-key,
success-only slot for the exact type object; it does not cache validation outcomes or graph bindings.
Union discriminator allowances remain invocation-specific. The existing structural declaration lookup
uses ordinal identity, so it cannot express this case-insensitive check without changing its contract.
This extends existing Cohesive validation and preparation mechanisms without an Ari abstraction or
another semantic type model.

Concrete qualified example: 128 fields declared `field0` through `field127`, with corresponding instance
properties `FIELD0` through `FIELD127`. Previously each declaration lookup scanned the immutable instance
map, twice, with boxed enumerators. One temporary index replaces those repeated scans. On the local
macOS Arm64 machine, warm validation changes from 278.6 μs / 45,057 B to 8.02 μs / 7,121 B.
These are synthetic instance workloads, not Ari endpoint or catalog measurements.

The [before](execution-preparation-final/nested-validation-before.md) and
[after](execution-preparation-final/nested-validation-after.md) reports and adjacent CSVs cover flat,
eight-level nested, 32-object collection, and 128-field inputs with exact, mismatched-case and unknown
properties. Type and instance construction occur outside measurement; setup warms validation 64 times.
BenchmarkDotNet uses the same fixture assembly for both variants, baseline Core from `97e7d1df`,
4096 invocations, three warmup iterations and eight measured iterations, in-process Short job:

```sh
dotnet build src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj -c Release
dotnet src/Cohesive.Relations.Benchmarks/bin/Release/net10.0/Cohesive.Relations.Benchmarks.dll --filter '*NestedValidationBenchmarks*' --inProcess --job Short --warmupCount 3 --iterationCount 8 --invocationCount 4096 --unrollFactor 1
```

For the baseline, copy that built output to an isolated directory and replace only `Cohesive.dll`
with the Release assembly built at the baseline revision. Run the same command against the copied DLL.
Exact-name means are 0.31 μs flat, 0.42 μs nested, 9.72 μs collection and 2.96 μs large after this change.
These small costs vary with JIT and machine state; avoid a universal speedup claim.
The occasional 1 B result is benchmark harness amortization; a direct counter regression proves zero
warm temporary allocation for exact object validation. Wide case-fallback allocation is bounded at
10 KB per check. A fresh 128-field unknown-property check is bounded at 20 KB including cache population,
and its second check against another instance at 1 KB. These cold budgets exclude declaration/value
construction and global runtime initialization. Detailed diagnostics, exact-name precedence after
fallback preparation, duplicate spellings, instance isolation and same-ID/different-graph named types
have regression coverage.


## Prepared graph-bound validation links

`ObservationValidationPlan` binds only array/object/named/enum nodes reachable from the requested root.
It holds the original named definitions and child links, not a copied type system or validation result.
Scalar leaves keep direct checks. For one exact immutable `ShapeGraph`, weak TypeRef keys share completed
child nodes across roots; graph ownership itself is weak. Missing named links are stable metadata for
that graph object, while messages still use each occurrence's source and diagnostic cursor. Two graphs
with identical IDs remain distinct cache scopes. Graphless checks bypass this preparation.

A per-graph gate prepares new closures using an iterative queue, reusing previously published children.
All new cyclic links are complete before publication; no per-node lock is held while acquiring another.
Warm lookup bypasses the preparation gate. This specialized closure publication differs from the
single-key `WeakPreparationCache`; substituting independently locked factories would risk recursive
initialization and deadlocks. Failed construction retains no incomplete state. Cold first reads of
different roots in the same graph serialize; unrelated graphs prepare independently. Roots outside
the graph's declarations can expire even while that graph remains alive. Tests prove these lifetimes,
concurrent first use, shared children, recursion/depth limits and graph-specific missing-link diagnostics.

The [named baseline](execution-preparation-final/named-validation-before.md) and
[final report](execution-preparation-final/named-validation-after.md), with adjacent CSVs, use the same
fixture assembly and published Core baseline `97e7d1df`. The command above can select
`*NamedValidationBenchmarks*` instead. Types and values are constructed outside timing, then warmed:
16 named-enum fields (flat), eight named structural parent levels (nested), 32 repeated objects
(collection), or 128 named-enum fields (large). Warm local means change from 281 to 255 ns flat,
520 to 416 ns nested, 9.01 to 8.01 μs collection, and 2.69 to 2.40 μs large. These are approximately
9–20% improvements against the published baseline, combining field lookup and prepared links. They
are not Ari endpoint or catalog latency measurements.

The dedicated cold counter constructs a fresh 128-field named graph and value outside measurement,
initializes runtime dispatch with unrelated graph/type keys, then measures first validation: 31,752 B
locally. Its next 1,000 exact-name validations allocate 0 B. The regression budgets cold preparation
at 64 KB and requires zero warm allocation. This trades first-use preparation and retained metadata
for repeated lookup savings; it does not reduce already-zero warm allocation. Runtime values,
nullability/presence/cardinality decisions, union allowances, depth checks and diagnostics stay fresh.


## Prepared string literal indexes

Inline string enums, named enums with string underlying types, and string-discriminator unions
check the first eight declarations directly. Larger declarations prepare an ordinal membership set
or first-case dispatch dictionary only on a later match or miss. Named enum labels and literal aliases
remain equivalent accepted spellings. Union insertion preserves the first declared matching case.
Nonstring primitive literals retain the existing representation-sensitive scan: integer `10` does
not match discriminator text `010`. Graph-bound indexes live on their plan node; standalone checks
use one weak declaration table.
A node owns metadata bound to its exact declaration. Accessor constraints bind the owner, index
result type and static factory. One shared getter owns a private index reference, checks exact owner
identity and publishes successfully under the metadata lock. Each current owner needs one index kind,
so there is no slot-selection API or per-kind field list. Successful publication also records accessor
identity, checked before casts on every read; a different accessor receives InvalidOperationException,
including when both return HashSet<string>. A zero-state typed token binds generic arguments once
and supplies short getter calls without boxing. Its nested helper is the sole callable entry point;
the raw generic getter is private. Each current owner type has one accessor. If a second accessor is
introduced for the same owner, the first successful preparation wins and the mismatch is detected at
use, not at compilation; that extension requires an explicit ownership decision. Tests have non-preparing read-only inspection.

There is no owner-to-factory switch; reading indexless metadata is safe. Slots publish successful
preparation only and retry failures. Named reference nodes resolving the same declaration share that container
within the graph; a graph-owned dictionary is bounded by its own declarations and retains no reference
root nodes. Object/inline-enum containers are lazy. Occurrence values are never cached. Node TypeRef identity and positional child
access have Release checks; metadata access verifies exact owner identity before the accessor
publishes its index privately; field-name accessors constrain owner types at compile time. The three
literal paths use one generic hybrid lookup policy, specialized by small static accessors.

The [baseline](execution-preparation-final/literal-validation-before.md) and
[final](execution-preparation-final/literal-validation-after.md) reports and adjacent CSVs use
`LiteralValidationBenchmarks`, 128-entry declarations, baseline Core `01dc0d7b`, and the same fixture
assembly. Types and values are constructed outside measurement and warmed 64 times. Run:

```sh
DOTNET_TieredCompilation=0 dotnet src/Cohesive.Relations.Benchmarks/bin/Release/net10.0/Cohesive.Relations.Benchmarks.dll --filter '*LiteralValidationBenchmarks*' --inProcess --job Short --warmupCount 3 --iterationCount 8 --invocationCount 4096 --unrollFactor 1
```

Both variants disable tiered compilation to avoid tier-promotion noise in these short in-process
runs; these timings are not comparable to earlier tiered reports or Ari endpoint timings.
Late inline enum matches change from 265 to 33 ns, named enum literals from 1,543 to 55 ns,
and union cases from 1,027 to 68 ns. Invalid values change from 535 to 121 ns, 3,057 to 122 ns,
and 2,135 to 196 ns respectively. First named enum matches improve from 30 to 24 ns; first union
matches remain approximately unchanged. First inline enum checks add about 0.9 ns. Unchanged object
diagnostic fixtures vary by roughly 0–4%, illustrating measurement noise rather than an unrelated speedup.

Diagnostic allocation sizes are unchanged apart from harness rounding: about 210 B for inline enum
misses, 169 B for named misses and 314 B for union misses. A direct counter proves zero temporary
allocation for 1,000 warm inline/union checks;
the occasional 1 B benchmark result is harness amortization. Fresh late/missing lookups allocate
2,840 B for inline enum preparation, 4,112 B for union preparation and 5,976 B for named enum
preparation locally. These counters exclude declaration/value construction, global runtime setup,
and graph-link preparation. Early inline/union matches allocate zero and do not prepare indexes.
Consolidating preparation adds 3,088 B to the 128-field named graph cold boundary (now 31,752 B),
including the original TypeRef and metadata slot on each node, plus a concurrent graph-owned
declaration registry that permits metadata-only readers without preparing validation closures. Graph-bound and standalone warm
literal checks have zero-allocation regression coverage. The per-graph closure gate remains the
intentional publication boundary; this change does not parallelize first-use roots within one graph.
Tests cover ordinal membership, aliases, duplicate union literals, temporal string values, numeric
representation sensitivity, distinct declaration identities and cold/warm allocation bounds.

## Lazy union decoding and exact index reuse

Plain typed decoding checks the first eight union cases without fetching validation plans or shared
metadata. Only a later match or miss requests the graph's declaration metadata. That concurrent
registry is bounded by the graph's own named declarations; metadata construction does not walk
children or compile a validation closure. Named validation plans bind the same metadata container.
A cold decoding regression proves that both early and late reads leave the union plan unprepared;
the late read populates its graph-owned dispatch dictionary, and subsequent validation must reuse
that exact dictionary instance. This replaces the indirect allocation-budget reuse assertion.
Release mismatch tests protect metadata ownership and positional child identity. Nonstring enum
members with no literal remain skipped, including the empty-bytes regression.

The [reader baseline](execution-preparation-final/union-reader-before.md) and
[final report](execution-preparation-final/union-reader-after.md), with adjacent CSVs, measure
`UnionReaderBenchmarks`: arrays of 1, 32 or 128 union objects, 128 discriminator cases, first versus
last-case selection, and a typed integer payload. Graph, declarations and JSON bytes are constructed
outside measurement; 64 reads warm each fixture. Timing includes parsing and owned output allocation,
but excludes semantic validation. The same fixture assembly runs against baseline Core `1c3dd3a5`
with only the existing typed reader method's visibility changed from private to internal, permitting
the benchmark seam; its implementation is unchanged. The final version keeps that internal seam.
Run the literal command above with `*UnionReaderBenchmarks*` instead, disabling tiered compilation
for both variants. No test work overlaps these runs.

Warm early-case arrays change from 19.63 to 19.16 μs at 32 objects and 79.10 to 77.30 μs at 128 objects.
Late-case arrays change from 20.90 to 20.46 μs and 82.06 to 82.14 μs respectively. The single-object
means are approximately unchanged, as are owned output allocations (roughly 15 KB at 32 objects and
60 KB at 128; 1–2 B differences are harness amortization). These small warm changes do not establish
a general speedup. The deterministic improvement is removing unused closure/index preparation and
reusing the exact graph-owned index. Cold named-graph preparation costs 984 B more than the previous
revision locally, while 1,000 warm validations still allocate zero bytes.
