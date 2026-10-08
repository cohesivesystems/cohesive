# Nested CLR contract reuse

Default root reuse still rebuilt repeated structural children on each first traversal. For example,
a large envelope with eight branches and four identical leaf types per branch inferred 32 separate
leaf graphs before compact encoding could discover their equality. The mapper now returns the
same completed child contract within that traversal.

## Semantics and ownership

This extends the existing Cohesive CLR mapper rather than adding an Ari abstraction or an
independent schema comparer. A traversal owns a dictionary keyed by structural CLR type, using
that mapper's fixed explicit mappings. Structural fields derive nullability from property declarations;
outer occurrence nullability is applied by the containing field. Collections, key/value pairs and
single-value wrappers remain on their existing occurrence-sensitive paths.

Only completed structural projections that encountered no recursive edge are memoized. A
monotonic encounter counter excludes every enclosing projection containing a recursive diagnostic.
Such projections must be rebuilt for each ancestor path. A previously completed cycle-free projection
cannot reference a current structural ancestor through an inferred edge: doing so would expose a
cycle during its original traversal and prevent publication. Memoized children may contain opaque
unsupported contracts whose diagnostics are context-independent.

No new process-wide cache is added. The dictionary dies with traversal; default root reuse from the
preceding change can retain the resulting immutable graph. Explicit consumer contracts retain their
existing mapper ownership and are not promoted into default shared roots.

## Measurements

Local macOS Arm64, SDK 10.0.201/runtime 10.0.5. Paired Ari source-assembly runs replace only
Core; all 164 canonical document bodies remain byte-for-byte identical. Cumulative allocated bytes:

| Boundary | Before | After |
|---|---:|---:|
| Root catalog authoring | 122,203,544 | 115,077,240 |
| First deployment catalog access, including deferred projection | 78,153,384 | 76,568,200 |
| Second deployment catalog access | 700,792 | 700,792 |

Authoring falls by 7,126,304 bytes (5.8%). These are not retained heap or end-to-end latency claims.
Root authoring excludes lazy projection; first deployment access includes it. Do not sum nested scopes.

Representative fresh traversals use a nonempty unused decimal override to bypass default root
reuse, with reflection metadata warmed. BenchmarkDotNet reports are adjacent. Flat allocation grows
from 1,001 bytes to about 1.28 KB due to the traversal dictionary. Nested falls from 3,983 bytes to
2.25 KB, collection from 8,569 bytes to 3.25 KB, and large from 30,674 bytes to 4.03 KB. The small
flat overhead is accepted for the measured aggregate reduction; timing is not an application latency
claim. Warm root-cache behavior is unchanged.

Tests verify reference sharing within one traversal, isolation across fresh traversals, explicit overrides,
nullable occurrences and recursive siblings with different ancestor paths. A 5,000-byte large-traversal
allocation budget protects the mechanism; the preceding assembly fails the reference-sharing regression.

Validation: 4,303 Core tests pass with 33 existing skips; the final mapper suite passes
21 tests, including the additional recursive-sibling regression. Relations passes 1,082 tests;
Ari engine passes 855 with 18 existing skips. Ari package assemblies were restored byte-for-byte
after qualification. The fix is local and unpublished.
