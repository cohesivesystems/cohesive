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

## Resource Health availability

`availability.json` is an original, hand-authored synthetic fixture under the repository license, not a copied Azure response. Its replacement marker identifies an exact reviewed resource; the returned ID identifies that resource's `availabilityStatuses/current` extension. The source timestamp is intentionally earlier than capture time. The extra summary is a synthetic redaction sentinel.

- [Resource Health GET contract, API 2025-05-01](https://learn.microsoft.com/en-us/rest/api/resourcehealth/availability-statuses/get-by-resource?view=rest-resourcehealth-2025-05-01): extension identity/type, four availability tokens and `reportedTime` as the last health-check time.
- [Resource Health supported resource types](https://learn.microsoft.com/en-us/azure/service-health/resource-health-checks-resource-types): consulted 2026-09-19 for App Service sites and Cosmos accounts. Neither scope is application admission. SQL database children, Durable Task schedulers/task hubs and Blob containers are deliberately unsupported in this adapter slice; no parent-health substitution occurs.

Tests vary state, source time, identity, malformed fields and duplicates. They cover healthy, unhealthy, degraded, unknown, stale/future, unavailable collection and unsupported coverage without claiming live qualification.

On 2026-09-20, the [Task Hubs GET 2025-11-01 contract](https://learn.microsoft.com/en-us/rest/api/durabletask/task-hubs/get?view=rest-durabletask-2025-11-01) was rechecked: its example and resource description include root `type`. Original synthetic tests also omit this field while retaining a matching ID and success state. These responses must produce `missingResourceType` and never admit success; invalid IDs take precedence, and present invalid types remain identity mismatches. No live response or deployment identity is included.
