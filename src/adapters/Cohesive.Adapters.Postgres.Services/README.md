# PostgreSQL service preparation

Optional integration of `Cohesive.Api.Execution` service selections with PostgreSQL runtime readers and
`Cohesive.Infra` evidence. The base PostgreSQL adapter does not depend on service hosting.

Declare relational queries through API implementation Bind calls (or explicit `ServiceDependencies.Query` for separately authored services).
`bindings.PrepareService(endpoints.Plan, implementationId)` validates endpoint coverage and derives
provider-neutral snapshot requirements and native PostgreSQL evidence directly from the portable
implementation plan. No application-level query forwarding catalog is needed. Separately declared
services and explicit dependency selections remain supported by the same preparation path. `persistence.Query(service, query)` checks the exact
semantic inputs and prepares a reader using the selected row and byte bounds. Both paths run at setup,
without database IO; schema installation, credentials, privileges and runtime readiness remain separate.

Only `SingleSnapshot` selections are supported by this native recipe. `IndependentSources` requires explicit
composed preparation and fails here with a coded service diagnostic. No cross-query snapshot is claimed.
The service selections remain host attachments to canonical query IR, not a second query definition.

Tests in `PostgresServiceSelectionTests` cover exact selection, bounds, immutable registration, unselected
queries and consistency rejection. The Aspire-first integration tests exercise the resulting readers and
process bindings against real PostgreSQL, including receipt replay and cross-partition isolation.

`persistence.ForServices(validatedReceipts)` attaches the same native mappings to `IServicePersistence`.
Process observation contracts select a unique registered entity mapping; no entity/repository list is repeated
at each service binding. Repositories are shared within this resolver's host lifetime. Native query preparation
still receives explicit complete-result bounds. The resolver owns no data sources and performs no schema IO.


`ForServices` shares repository instances by observation contract and query readers by exact query instance
and row/byte bounds within one resolver. Preparation is synchronized and successful readers are retained
for that host scope; failed preparation is not cached. Inputs and results remain invocation-scoped.
