# ARM GET contract fixtures

These are hand-authored, minimal synthetic fixtures, not recorded Azure responses and not generated SDK artifacts. They pin the response fields consumed by the collector. Names and identities are synthetic; `{{RESOURCE_ID}}` is replaced with an explicit test binding. No third-party example payload was copied. The fixture authorship is part of this repository and uses its Apache-2.0 license; no separate third-party payload license is required.

Authoritative contracts checked on 2026-09-19:

| Fixture | Pinned API and relevant response shape |
|---|---|
| `site.json` | [Web Apps GET, 2025-03-01](https://learn.microsoft.com/en-us/rest/api/appservice/web-apps/get?view=rest-appservice-2025-03-01): root ARM id/type, properties.state. Hostname is deliberately ignored. |
| `cosmos.json` | [Database Accounts GET, 2025-10-15](https://learn.microsoft.com/en-us/rest/api/cosmos-db-resource-provider/database-accounts/get?view=rest-cosmos-db-resource-provider-2025-10-15): root ARM id/type, properties.provisioningState. |
| `database.json` | [SQL Database GET, 2025-10-15](https://learn.microsoft.com/en-us/rest/api/cosmos-db-resource-provider/sql-resources/get-sql-database?view=rest-cosmos-db-resource-provider-2025-10-15): root ARM id/type and properties.resource; nested database id is not the ARM identity. No provisioning token is invented. |
| `scheduler.json` | [Schedulers GET, 2025-11-01](https://learn.microsoft.com/en-us/rest/api/durabletask/schedulers/get?view=rest-durabletask-2025-11-01): root ARM id/type and properties.provisioningState. |
| `taskhub.json` | [Task Hubs GET, 2025-11-01](https://learn.microsoft.com/en-us/rest/api/durabletask/task-hubs/get?view=rest-durabletask-2025-11-01): root ARM id/type and properties.provisioningState. |

All fixtures prove parsing, routing and management evidence only. They do not establish application health, data-plane access, worker admission, live permissions or provider service availability. Unknown state values are omitted rather than copied into retained artifacts. Tests separately cover failure bodies, wrong identities, duplicate fields, oversized bodies and cancellation.
