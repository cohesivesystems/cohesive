# Cohesive.Adapters.Postgres.Infra

Optional integration between PostgreSQL query preparation and `Cohesive.Infra`. The base PostgreSQL
adapter remains usable without Infra. No resources, connections or migrations are created here.

```csharp
var required = PostgresInfrastructure.QuerySnapshot(query.CompilationRequest);
var artifact = bindings.Prepare(query.CompilationRequest);
var implementation = PostgresInfrastructure.QueryImplementation(new("orders/postgres"), artifact);
```

The requirement fingerprints the complete semantic plan reference: definition, shape snapshots,
relationship catalog, demand, inputs and compiler profile. It does not depend on physical table names.
Evidence is derived from a validated, fully placed, unpaged PostgreSQL query-row artifact. It attributes
the exact artifact and binding fingerprints; changing a table changes evidence without changing the
semantic requirement. Evidence for one plan cannot satisfy another plan's requirement.

`QueryImplementation` accepts multiple distinct queries on one database. Each gets an independent
single-statement snapshot guarantee. It rejects empty/duplicate input, mixed databases, source-scoped
bindings, paging and non-row branches. Composed or multi-read queries need different evidence; this
factory must not certify them from one native prefix.

The canonical query is the semantic authority; PostgreSQL mappings and native compilation determine
whether it can be realized. `PostgresPersistenceBindings` provides offline preparation shared with
runtime registration. There are no app-authored capability assertions or runtime data-source stand-ins.

This is preparation evidence, not deployment or runtime qualification. Schema installation, permissions,
provider availability, conditional entity writes, receipt atomicity and distributed snapshots are not
claimed. Native query failure remains possible if the live database violates the prepared configuration.

The library tests cover exact-plan isolation, evidence sensitivity to mappings, offline/runtime artifact
equality, mapping snapshot ownership and failures before IO. The Aspire example exercises actual native
project metadata and the same mappings in its worker; PostgreSQL integration tests verify runtime use.
