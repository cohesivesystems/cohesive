## Summary

- What changed:

## Design and Optimization Judgment

- Semantic authority and invariants affected:
- Material tradeoffs or intentionally deferred concerns:
- Performance evidence, when performance influenced the design:

## Critical Files for Review

<!-- Select the smallest useful set, normally 3-7 files, and explain what deserves attention. -->

- `path/to/file`: Contract, invariant, algorithm, wire format, or architectural decision to review.

## Validation

- [ ] `dotnet test Cohesive.sln -c Release --no-restore`
- [ ] Relation benchmarks run locally or through the manual `relation-benchmarks` workflow when relevant
- [ ] `corepack pnpm frontend:test`
- [ ] `corepack pnpm frontend:build`
- [ ] `corepack pnpm pack:local`
- [ ] Manual verification completed when needed

## Package Impact

- [ ] Public API surface changed intentionally
- [ ] Package version/release notes updated if needed
- [ ] Breaking changes called out

## Notes

- Risks:
- Rollback / mitigation:
