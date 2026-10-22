# Bounded decoded-text reuse in token readers

October 18, 2026; macOS 27.0.1 arm64, SDK 10.0.402/runtime 10.0.13, Release.
This increment follows the [boundary conversion work](observation-values-stage2.md). It addresses
that report's remaining per-field/per-string allocation rather than introducing another value model.

## Design and ownership

Plain JSON, shape-typed hydration and lossless tagged values now reuse repeated short decoded
strings through the existing `ObservationJsonPropertyNames` authority. A stack-owned token scope
avoids cache-owner allocation for small input, creates the owner after 64 eligible tokens, and
shares it across the entire recursive read. Tagged kind names and string-encoded scalar payloads
participate alongside field names and ordinary strings.

The existing direct-mapped table has 256 positions. Tokens must be contiguous and no longer than
256 raw UTF-8 bytes; oversized or segmented tokens use the original decoder. After 32 initial
misses with no reuse, the table is cleared and abandoned for the remainder of that traversal.
Once reuse is observed, collisions may cause more decoding but cannot grow the table. Its hash is
process-randomized, so exact allocation can vary with bucket collisions across fresh processes.

Entries hold independently owned decoded strings, never caller UTF-8 memory. All pooled references
clear on success and failure, including deferred tagged payload decoding. Results can share
immutable text within a read; separate reads have separate ownership. Containers remain freshly
constructed and admission stays fresh. No global content cache, external dependency, new public
API, wire representation or fingerprint version is introduced.

A token hit requires the parser's decoded ordinal comparison, not merely a matching hash. Tests
force a hash collision between invalid UTF-8 and valid replacement-character text and preserve the
parser's rejection behavior. Escaped names retain ordinal duplicate semantics. Different raw escape
spellings need not share a physical string. The element and token readers retain different lookup
mechanics because only the element interpretation owns a complete live source document.

The private stack-owned reader groups existing recursive operations with their traversal state;
the shared token scope owns the lazy lease used by both ordinary and tagged decoding. These are
physical lifetime boundaries, not additional semantic authorities. Public parsing entry points and
internal typed hydration retain their existing contracts.

## Measurement

Control is the final boundary increment, including its invalid-diagnostic exception fix. The same
benchmark binary and dependency closure run against frozen control/candidate Core assemblies.
BenchmarkDotNet uses in-process emit, three warmups, five measured iterations and a 250 ms target.
Fixture construction, input bytes, sequence segmentation and setup equivalence checks are excluded.
Runs are sequential without concurrent builds, tests or other measured workloads.

Allocation includes required owned containers and decoded text; it is not retained-heap size.
The deterministic warm allocation regression bounds 4,096 repeated strings to their 32-byte value
slots plus 8 KiB, independent of per-item string allocation. Cold and concurrent pool misses may
allocate the fixed scratch table. Unique and oversized strings still allocate their required text.

An initial matched run exposed bypass overhead on flat and unique inputs. Its results are exploratory;
the final candidate keeps the small/abandoned-cache branches inline and moves cache preparation out
of that path. The final tables and evidence identify the tuned binary. This code-generation adjustment
does not change eligibility, ownership, limits or semantic decisions. It does not eliminate all
bypass overhead. This increment prioritizes reduced collection allocation and bounded-large-input
throughput while explicitly accepting the measured CPU costs on smaller and unproductive inputs.

## Matched results

Means below are in microseconds; allocation includes required owned output.

### Plain JSON parsing

| Workload | Control μs | Candidate μs | Control B/op | Candidate B/op |
| --- | ---: | ---: | ---: | ---: |
| flat | 0.567 | 0.652 | 1,680 | 1,680 |
| nested | 1.629 | 1.759 | 4,720 | 4,720 |
| collection | 26.864 | 28.368 | 60,570 | 34,987 |
| large | 1,786.392 | 1,368.254 | 1,948,191 | 1,027,377 |

### Lossless tagged payloads

| Workload | Control μs | Candidate μs | Control B/op | Candidate B/op |
| --- | ---: | ---: | ---: | ---: |
| flat | 0.924 | 0.912 | 1,496 | 1,496 |
| nested | 2.660 | 2.640 | 4,185 | 4,185 |
| collection | 62.316 | 62.030 | 88,972 | 39,450 |
| large | 3,133.939 | 2,332.699 | 2,844,930 | 1,162,349 |

### Text distributions and buffer boundaries

| Workload | Control μs | Candidate μs | Control B/op | Candidate B/op |
| --- | ---: | ---: | ---: | ---: |
| unique | 29.367 | 31.116 | 110,621 | 110,657 |
| escaped | 71.650 | 78.440 | 118,820 | 69,039 |
| large-text | 70.166 | 70.387 | 1,061,403 | 1,061,403 |
| segmented | 129.537 | 130.304 | 188,461 | 188,461 |

### Complete checkpoint recovery with fresh admission

| Workload | Control μs | Candidate μs | Control B/op | Candidate B/op |
| --- | ---: | ---: | ---: | ---: |
| flat | 56.654 | 54.665 | 37,535 | 37,541 |
| nested | 100.807 | 98.576 | 47,306 | 47,305 |
| collection | 1,106.114 | 1,110.117 | 221,374 | 157,139 |
| large | 26,412.939 | 25,069.923 | 8,412,614 | 7,342,361 |

The 4,096-record plain parser allocates **47.3% less** and its mean is **23.4% lower**. The complete
parse/map/write operation moves from 1.920 ms and 1,955,344 B to 1.465 ms and 1,027,263 B.
On this repeated-record fixture, direct parsing now approaches the element route's allocation
without constructing a temporary document. The element route remains useful for other distributions.

Large tagged decoding allocates **59.1% less** with a **25.6% lower mean**. Full large recovery
allocates **12.7% less**, a smaller gain because strict wire checking, envelopes and admission remain.
Its mean is 5.1% lower in this run; this is not a universal recovery-latency claim. The checkpoint
collection distributions have substantial variance; consult their full reports rather than treating
a small difference between means as established.

**The tradeoff is real:** flat direct parsing rises from 567 ns to 652 ns (about 15%), nested parsing
is 8.0% slower, 128-record parsing is 5.6% slower, and unique-text parsing is 6.0% slower.
Escaped-text parsing allocates 41.9% less but is 9.5% slower. Those results are retained rather than
hidden behind the larger-input win. Unique input adds only the bounded owner allocation after the
unproductive table is returned. Long and segmented tokens gain no allocation reduction.

The element route's 8 KiB repeated-string case allocates about 25 KiB versus the token reader's
1.06 MB. Its live document supports raw-key reuse without retaining independent token buffers;
this token policy deliberately bypasses oversized strings. There is no claim that direct parsing
universally replaces the element interpretation.

Full distributions, GC data and unrounded results:
[control pipeline](evidence/observation-values-string-reuse/control-pipeline.md),
[candidate pipeline](evidence/observation-values-string-reuse/candidate-pipeline.md),
[control text](evidence/observation-values-string-reuse/control-text.md),
[candidate text](evidence/observation-values-string-reuse/candidate-text.md),
[control tagged](evidence/observation-values-string-reuse/control-tagged.md),
[candidate tagged](evidence/observation-values-string-reuse/candidate-tagged.md),
[control recovery](evidence/observation-values-string-reuse/control-checkpoint.md), and
[candidate recovery](evidence/observation-values-string-reuse/candidate-checkpoint.md).
Full JSON and CSV exports accompany them. Only Core differs in the final measured runtime closure;
exploratory runs with rebuilt dependency assemblies are excluded from these final tables.

## Verification

The full suites passed **5,006 Core tests (54 skipped)** and **1,141 Relations tests** before the
final inlining adjustment. **192 affected decoder, allocation, profile, canonical, normalization and
checkpoint tests passed afterward.** The tested final Core binary matches the measured candidate.
Coverage includes every kind, generated trees, decoded
duplicate names, escaped Unicode, invalid UTF-8, invalid surrogate escapes, forced cache collisions,
single-byte segmentation, large/unique strings, cache failure cleanup, input-buffer reuse,
cross-read independence and typed hydration followed by fresh semantic validation.

Binary/source hashes, complete benchmark distributions and resource counters accompany the report.
Timing results are local bounded-workload evidence, not network, storage or production guarantees.

## Consumer qualification and limits

The existing EDI and authoring harnesses run three alternating fresh-process pairs with only Core
substituted. Their older Storage ABI is retained on both sides; the rebuilt whole-checkpoint
benchmark above covers current Storage. No sibling-repository changes or compatibility shim are
introduced. All complete EDI output hashes and both graph fingerprints remain identical.

EDI medians of per-process warm batch medians (seven batches of four after twenty warmups):

| Stops | Control B/op | Candidate B/op | Control ms/op | Candidate ms/op |
| --- | ---: | ---: | ---: | ---: |
| 2 | 2,449,786 | 2,449,954 | 4.576 | 4.386 |
| 16 | 2,879,128 | 2,878,826 | 5.493 | 5.466 |
| 64 | 4,349,620 | 4,349,620 | 10.492 | 10.605 |

EDI allocation is effectively unchanged. These parser-specific gains do not establish a general
native EDI mapping speedup. The original complete repository-transition authoring workload also
retains separate cold and repeated invocations with a fresh fixture:

| Case | Control cold B | Candidate cold B | Control repeated B | Candidate repeated B | Control repeated ms | Candidate repeated ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 004010 | 245,292,528 | 245,275,976 | 78,600,992 | 78,247,816 | 708.258 | 754.151 |
| 005030 | 247,383,928 | 246,955,296 | 83,079,040 | 83,106,576 | 989.620 | 981.368 |

The 004010 repeated case is 6.5% slower in this local sample despite slightly lower allocation;
005030 allocation is essentially unchanged. There is no universal authoring-speed claim or causal
CPU profile inferred from these counters. The earlier authoring allocation signal remains an
unattributed qualification concern rather than being declared resolved.

[EDI counters](evidence/observation-values-string-reuse/edi-counters.csv),
[raw retained-heap samples](evidence/observation-values-string-reuse/edi-retention.json),
[authoring counters](evidence/observation-values-string-reuse/protocol-counters.csv), and
[graph fingerprints](evidence/observation-values-string-reuse/protocol-fingerprints.txt) retain the
underlying evidence. Retention samples include cache/GC variation and are not an exact heap census.
Complete checkpoint wire sizes and hashes are unchanged for flat, nested, 128-record and
4,096-record fixtures: 3,538 / 4,754 / 38,140 / 1,117,436 UTF-8 bytes. Both closures' full wire
hashes are retained in the evidence directory.
