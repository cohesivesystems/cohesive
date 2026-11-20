# Execution document preparation

Opt-in BenchmarkDotNet qualification of temporary serialization ownership during document creation.
Fixtures cover flat, nested/collection-heavy and bounded-large definitions; setup and input construction
are outside the measured operation. Results include the newly retained canonical document. This harness
is outside the unit suite and has no timing threshold.

```sh
dotnet run -c Release --project eng/runtime-performance/execution-documents -- --filter '*' --artifacts /tmp/execution-document-results
```

Compare the same harness at the recorded parent and candidate revisions; see the owning performance
note in `docs/performance/execution-canonicalization.md` for provenance and limits.
