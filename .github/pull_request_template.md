## Summary

- What changed:

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
