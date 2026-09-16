---
kind: decision
status: implemented
authority: cohesive.opentelemetry.registration
owners: [cohesive-core]
applies_to: [cohesive-adapters-opentelemetry, cohesive-execution, cohesive-relations, cohesive-processes-distribution]
last_verified: 2026-09-15
supersedes: []
---

# Register Cohesive telemetry through native OpenTelemetry builders

## Context

Cohesive blocks already emit traces and metrics with `System.Diagnostics.ActivitySource` and
`System.Diagnostics.Metrics.Meter`. Their owning packages define the scope names, operations, instruments, units,
status, and attributes. Applications nevertheless had to discover and register each scope manually before an
OpenTelemetry provider could collect it. This made incomplete registration easy and obscured the relationship
between ASP.NET request telemetry, Cohesive logical work, and provider-client operations.

The integration must preserve the existing instrumentation owners as semantic authorities. It must also remain
optional: a consumer of core domain packages should not acquire an OpenTelemetry SDK, exporter, host integration, or
storage adapter merely because the package can emit native diagnostics.

## Decision

Provide a small optional `Cohesive.Adapters.OpenTelemetry` package with extension methods on the native
`TracerProviderBuilder` and `MeterProviderBuilder` types. It registers the source and meter constants owned by the
core execution, Relations, and Process-distribution blocks. The package is an adapter because it interprets native
.NET diagnostic emitters through an external collection API; the emitting core packages remain provider-neutral.

The package provides both an aggregate `AddCohesiveCoreInstrumentation` helper and block-specific helpers. `Core` is
explicit because the aggregate does not imply coverage of adapter or provider scopes. Trace and metric registration
project one internal paired core-scope membership list whose values reference the instrumentation owners' public
constants; no source or meter name is copied. Conformance tests require the aggregate to remain behaviorally
equivalent to the block helpers composed together.

The package depends only on the lightweight OpenTelemetry provider-builder API and the core packages whose constants
it consumes. It does not select or wrap exporters, collectors, sampling, resources, propagation, processors, logging,
or hosting.

Adapter and provider scopes remain explicit host selections. Hosts register those scopes from constants exposed by
the selected adapter packages and configure provider-native instrumentation independently. Consequently, selecting
core Cohesive collection does not pull Cosmos, PostgreSQL, Elasticsearch, or another adapter into the dependency
graph. Central typed helpers for those scopes are intentionally omitted: they would either pull every adapter into
this package or make OpenTelemetry a dependency of each adapter for all consumers.

Activity parentage remains native. Cohesive activities start beneath `Activity.Current`, so ASP.NET request
activities are their parents and provider-client operations started during logical work are their children. The
registration package does not create wrapper activities or modify propagation.

## Alternatives considered

### Put OpenTelemetry registration in every instrumentation owner

Rejected because it would add an OpenTelemetry dependency to every core package and conflate emitting native
diagnostics with selecting a collection pipeline.

### Maintain a copied catalog of scope names

Rejected because copied string constants would become a parallel authority and could drift from the emitters. The
registration helpers consume the owners' public constants directly.

### Register all adapters from one aggregate package

Rejected because an application would acquire unused infrastructure dependencies merely for registration
convenience. Adapter selection is a host responsibility and its scope constants remain adapter-owned.

### Abstract OpenTelemetry or System.Diagnostics

Rejected because the native APIs already provide the required emission and collection contracts. A Cohesive facade
would add translation without a distinct semantic guarantee.

### Define service objectives in the registration package

Rejected because telemetry collection and service-level policy have different authority and lifecycle.
`Cohesive.ServiceLevels` may later bind stable semantic operation identities to objectives and indicators without
changing the collection contract.

## Consequences

- Hosts can collect every core Cohesive scope with one native builder call or select only the blocks they use.
- Existing instrumentation packages remain the authorities for emitted names and semantics.
- Trace and metric aggregate membership is paired in one place and tested against explicit block composition.
- Export, sampling, resources, sensitive-data policy, and Application Insights configuration remain host-owned.
- Other adapter scopes require explicit native registration from their package constants, preventing hidden
  infrastructure dependencies while retaining compile-time names.
- Package-consumer compatibility is constrained by the OpenTelemetry provider-builder API version. Applications with
  older OpenTelemetry dependency sets must validate package resolution when adopting this integration.
