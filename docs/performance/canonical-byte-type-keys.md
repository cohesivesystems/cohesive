# Canonical byte keys for compact type interning

Type interning previously decoded every candidate's canonical UTF-8 into a UTF-16 string before
checking whether an equal entry already existed. A declaration containing 64 equivalent 128-field
parents created 64 large text keys, while retaining only one parent in the compact type table.

## Authority and ownership

Cohesive's existing execution-definition canonical writer remains the semantic authority. Its shared
`WriteCanonicalDefinition` entry point performs the same validation, normalized numeric writing and
property ordering as owned document normalization. The codec introduces no alternative type model,
field traversal or canonical format. The CLR serializer still emits each candidate's original payload;
parent reference replay and strict external document validation are unchanged.

A document-local dictionary uses owned canonical byte arrays as keys. .NET alternate span lookup
compares borrowed pooled bytes without copying them on duplicate lookup. HashCode hashes bytes
only to select dictionary buckets; exact SequenceEqual establishes identity, including annotations.
Only insertion copies the key. No pooled buffer escapes its lifetime, and keys die with the type pool.

Unique leaves parse their already-written canonical bytes into an owned JsonElement. Duplicate leaves
need no owned JSON document. Unique parents retain their original serializer payload for reference
renumbering and do not create provisional ordering text. Final leaf/parent ordering continues to use
StringComparer.Ordinal on canonical text, so UTF-8 lexical ordering never replaces the wire contract.
There is no new shared cache, serializer lease or writer-lifetime mechanism.

## Qualification

Local macOS Arm64, .NET SDK 10.0.201/runtime 10.0.5. Paired source-assembly runs replace only
Core in an identical Ari workload. Values are cumulative allocated bytes, not retained heap or
end-to-end latency claims. Root authoring excludes lazy document projection; first deployment
catalog access includes it. Do not sum nested scopes.

| Boundary | Before bytes | After bytes |
|---|---:|---:|
| Root catalog authoring | 115,032,272 | 111,651,184 |
| First deployment catalog access including projection | 76,571,112 | 76,032,744 |
| Second deployment catalog access | 701,192 | 685,544 |

Authoring saves 3,381,088 bytes (2.9%). All 164 canonical document bodies remain byte-for-byte
identical. Warmed document preparation benchmarks exclude declaration construction and distinguish
serializer metadata population from per-document work. Adjacent BenchmarkDotNet reports cover
flat, nested, collection, large and duplicate-heavy inputs. Allocation falls across all five shapes:
flat 35.32 to 33.46 KB, nested 63.46 to 61.40 KB, collection 217.84 to 203.13 KB,
large 885.87 to 818.84 KB and duplicate-heavy 3,839.89 to 1,958.69 KB. Short timings do not
establish application latency gains.

The deterministic 64-duplicate-parent test measures 2,004,024 bytes and enforces a 2,200,000-byte
budget; the preceding implementation exceeds it. Existing tests protect canonical numeric annotation
equivalence, distinct annotation identity, UTF-8 escaping, ordering, reference digit-width changes,
concurrent codec ownership, unsupported tags, invalid indices, cycles and external integrity checks.

Validation: 4,305 Core tests pass with 33 existing skips; Relations passes 1,082; Ari engine
passes 855 with 18 existing skips. The prior assembly allocates 3,931,064 bytes and fails the
new duplicate-key allocation guard. Ari package assemblies were restored byte-for-byte after
qualification. This change remains local and unpublished.
