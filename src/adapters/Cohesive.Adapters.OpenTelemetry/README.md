# Cohesive.Adapters.OpenTelemetry

`Cohesive.Adapters.OpenTelemetry` interprets Cohesive's existing `ActivitySource` and `Meter` scopes through native
OpenTelemetry builders. Core packages continue to emit standard .NET diagnostics without depending on OpenTelemetry.
This adapter does not define another telemetry model and it does not select exporters, collectors, sampling,
resources, propagation, processors, logging, or hosting.

## Install

```bash
dotnet add package Cohesive.Adapters.OpenTelemetry
```

## Register core instrumentation

Register all core scopes in an existing host pipeline:

```csharp
using Cohesive.Adapters.OpenTelemetry;

services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddCohesiveCoreInstrumentation())
    .WithMetrics(metrics => metrics
        .AddCohesiveCoreInstrumentation());
```

`Core` is explicit in the aggregate name because other adapter and provider scopes remain host selections. Trace and
metric registration project the same internal paired core-scope membership, and conformance tests require the
aggregate to remain behaviorally equivalent to the block helpers composed together. Register a smaller surface when
the host does not use every core block:

```csharp
services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddCohesiveExecutionInstrumentation()
        .AddCohesiveRelationsInstrumentation())
    .WithMetrics(metrics => metrics
        .AddCohesiveExecutionInstrumentation()
        .AddCohesiveRelationsInstrumentation());
```

The extension methods operate on `TracerProviderBuilder` and `MeterProviderBuilder`; the same methods work with
`Sdk.CreateTracerProviderBuilder()` and `Sdk.CreateMeterProviderBuilder()` outside dependency injection.

## Scope authority and parentage

The instrumentation owner remains the source of truth for every name, activity, instrument, unit, status, and tag.
This package copies no scope names. Its internal collection-membership list pairs the public activity-source and meter
constants owned by each block so trace and metric registration cannot drift independently.

| Owner | Activity source | Meter | Responsibility |
| --- | --- | --- | --- |
| `Cohesive.Execution` | `Cohesive.Execution` | `Cohesive.Execution` | Logical execution, Storage execution projections, checkpoints, Control, and materialization state |
| `Cohesive.Relations` | `Cohesive.Relations` | `Cohesive.Relations` | Canonical relation compilation, realization, acquisition, interpretation, and mapping |
| `Cohesive.Processes.Distribution` | `Cohesive.Processes.Distribution` | `Cohesive.Processes.Distribution` | Distributed work admission, claims, leases, worker turns, and settlement |

Cohesive activities start beneath `Activity.Current`. In an ASP.NET host, the framework request activity is therefore
the parent of logical Cohesive work. Provider SDK or HTTP activities started while a Cohesive activity is current are
its children. Do not add a second wrapper activity around an already-instrumented provider call merely to measure the
same interval.

## Other adapter and provider scopes

The core-scope aggregate intentionally does not reference other adapter assemblies. A host registers only the adapters
it selected, using the constants owned by those packages:

```csharp
services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddCohesiveCoreInstrumentation()
        .AddSource(CosmosRelationQueryTelemetry.InstrumentationName)
        .AddSource(CosmosClientFactory.OperationActivitySourceName))
    .WithMetrics(metrics => metrics
        .AddCohesiveCoreInstrumentation()
        .AddMeter(CosmosRelationQueryTelemetry.InstrumentationName));
```

The adapter deliberately does not offer typed helpers for these external scopes. Doing so here would pull every
supported infrastructure adapter into one package; doing so in each adapter would make OpenTelemetry mandatory for
all of that adapter's consumers. Hosts therefore use the selected package's compile-time constants with native
`AddSource` and `AddMeter` calls.

Current adapter scopes are:

| Owner | Trace source | Meter | Notes |
| --- | --- | --- | --- |
| `Cohesive.Adapters.Cosmos` | `Cohesive.Adapters.Cosmos.Relations` | same | Cohesive relation-adapter work |
| Microsoft.Azure.Cosmos | `Azure.Cosmos.Operation` | not publicly configurable in the current stable SDK | Native database-client operations; query text is off by Cohesive default |
| `Cohesive.Adapters.Postgres` | `Cohesive.Adapters.Postgres.Relations` | same | Cohesive relation-adapter work; register provider instrumentation separately |
| `Cohesive.Adapters.Elastic` | `Cohesive.Adapters.Elastic.Relations` | same | Cohesive relation-adapter work |
| `Cohesive.Adapters.Elastic` | `Cohesive.Adapters.Elastic.Materialization` | same | Target lifecycle, bulk operations, sizes, and outcomes |

## Attribute and metric safety

- Metric dimensions must remain closed, bounded, and low-cardinality. Use only dimensions documented by the owning
  telemetry contract.
- Exact request, process, attempt, generation, document, tenant, resource, payload, query, evidence, and fingerprint
  identities are trace-only unless the owner explicitly documents a bounded metric interpretation.
- Query text, payload content, credentials, authorization material, raw exception messages, and connection strings are
  prohibited by default. Enable a provider's sensitive-data option only through that provider's native configuration
  and only with an explicit data-handling decision.
- Static service, deployment, environment, region, and version attributes belong on the OpenTelemetry `Resource`, not
  on every activity or metric point.
- Missing telemetry is not success evidence. Sampling, export failure, process loss, and inactive listeners remain
  distinct from a measured successful operation.

`Cohesive.ServiceLevels` can later bind stable semantic operation identities to objectives and indicators. This package
only makes runtime evidence collectible; it does not define objectives or evaluate compliance.
