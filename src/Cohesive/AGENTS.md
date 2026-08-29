# Cohesive Core Agent Notes

This directory owns foundational semantic values and their common runtime operations. Follow the repository-level
instructions and the normative [Code Quality and Optimization Model](../../docs/quality/code-quality.md). The local
priority is to preserve semantics while treating per-value and per-field operations as presumptively hot.

## Foundational runtime path protocol

Construction, successful validation, field lookup, equality, hashing, canonicalization, fingerprinting, and
materialization of core values require representative performance evidence when added or materially changed.

- Identify invocation multiplicity, ownership, lifetime, retained-result allocation, and temporary allocation before
  choosing the implementation.
- Benchmark representative flat, nested, collection-heavy, and bounded-large inputs as applicable. Separate cold
  compilation and cache population from warm execution.
- Add deterministic allocation or bounded-memory regression tests for claimed hot-path guarantees. Keep timing
  evidence in BenchmarkDotNet reports unless a controlled CI environment provides a reliable threshold.
- Do not route the successful common path through JSON text, reflection, diagnostic strings, LINQ materialization,
  general-purpose object graphs, or repeated metadata discovery without evidence that the cost is acceptable.
- Construct detailed paths and diagnostics lazily after failure is known. Optimize success and failure paths as
  distinct workloads.

## Parallel canonical implementations

Canonical bytes and fingerprints are durable contracts. If different physical writers are retained for materially
different operating requirements, such as maximum throughput and payload-bounded streaming, they remain
interpretations of one canonical format.

- Centralize format tokens, ordering rules, scalar mappings, and normalization decisions at the owning layer.
- Require differential tests with exact byte equality across every `ObservationValueKind`, boundary values, nested
  structures, escaping, binary policy, and deterministic generated combinations.
- Make fixture coverage fail when a new closed-set value kind is added without a case.
- Document the operating distinction that justifies each implementation and retain benchmark evidence for it.
- Do not introduce a generic dispatch abstraction merely to remove similar syntax when it adds overhead or obscures
  the closed semantic model; share the stable semantic mechanism and verify any necessary physical duplication.
