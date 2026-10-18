# Cohesive.Adapters.Aspire.Postgres

Optional native association between Aspire PostgreSQL databases and prepared Cohesive query evidence.
It keeps PostgreSQL hosting dependencies out of the general Aspire and Postgres/Infra packages.

With `.Postgres(bindings, endpoints.Plan)` declared on the canonical resource, import this namespace
and call `associations.Resource(store, database)` inside `builder.AttachCohesive(...)`.
The exact prepared evidence is retrieved from the authoring result; query compilation is not repeated.
Missing PostgreSQL preparation fails with a node-specific error. Lifecycle ownership defaults to
`aspire/native`, or can be specified with `authority:`; native resources retain their actual ownership.
Explicitly passing `PostgresPreparedQueryEvidence` remains available for independently prepared compositions.
The extension checks the actual native database name against the prepared logical database before
forwarding to the existing association compiler. Resource labels can differ from database names.
It creates no resource and does not connect, migrate, deploy or start the application.
