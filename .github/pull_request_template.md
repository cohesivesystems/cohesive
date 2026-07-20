## Summary

- What changed:

## Validation

- [ ] `dotnet test Cohesive.sln -c Release --no-restore`
- [ ] `dotnet run --project src/Cohesive.Relations.Benchmarks/Cohesive.Relations.Benchmarks.csproj -c Release --no-build -- --job Dry --filter "*Relation*"`
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
