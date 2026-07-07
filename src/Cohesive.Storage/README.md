# Cohesive.Storage

Provider-neutral storage abstractions for entity repositories, observation streams, outbox records, seeding, and process repository adapters.

## Install

```bash
dotnet add package Cohesive.Storage
```

## Use When

- You need repository contracts for Cohesive entities and observations.
- You want storage behavior to attach to semantic entity and relation models without binding application code to a database SDK.
- You need adapters between entity snapshots, observation records, query repositories, and process execution.

## Related Packages

- `Cohesive.Transitions` for entity state and transition models.
- `Cohesive.Relations` for observation query semantics.
- `Cohesive.Adapters.Cosmos` for Cosmos DB-backed storage.

