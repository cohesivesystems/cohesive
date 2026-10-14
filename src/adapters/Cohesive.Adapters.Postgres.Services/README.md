# PostgreSQL service preparation

Optional integration of `Cohesive.Api.Execution` service selections with PostgreSQL runtime readers and
`Cohesive.Infra` evidence. The base PostgreSQL adapter does not depend on service hosting.

Declare relational queries once using `ServiceDependencies.Query`, attached through `ServiceBuilder.Use`.
`bindings.PrepareService(service, implementationId)` derives provider-neutral snapshot requirements and
native PostgreSQL evidence from those selections. `persistence.Query(service, query)` checks the exact
semantic inputs and prepares a reader using the selected row and byte bounds. Both paths run at setup,
without database IO; schema installation, credentials, privileges and runtime readiness remain separate.

Only `SingleSnapshot` selections are supported by this native recipe. `IndependentSources` requires explicit
composed preparation and fails here with a coded service diagnostic. No cross-query snapshot is claimed.
The service selections remain host attachments to canonical query IR, not a second query definition.

Tests in `PostgresServiceSelectionTests` cover exact selection, bounds, immutable registration, unselected
queries and consistency rejection. The Aspire-first integration tests exercise the resulting readers and
process bindings against real PostgreSQL, including receipt replay and cross-partition isolation.
