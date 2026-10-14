# Cohesive.Adapters.Aspire.Postgres

Optional native association between Aspire PostgreSQL databases and prepared Cohesive query evidence.
It keeps PostgreSQL hosting dependencies out of the general Aspire and Postgres/Infra packages.

Import this namespace and pass `PostgresPreparedQueryEvidence` to `associations.Resource(...)`.
The extension checks the actual native database name against the prepared logical database before
forwarding to the existing association compiler. Resource labels can differ from database names.
It creates no resource and does not connect, migrate, deploy or start the application.
