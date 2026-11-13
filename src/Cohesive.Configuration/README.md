# Cohesive.Configuration

Configuration profiles, configuration projection, dependency selection, and runtime profile helpers for Cohesive applications.

## Install

```bash
dotnet add package Cohesive.Configuration
```

## Use When

- You need layered runtime profiles over `Microsoft.Extensions.Configuration`.
- You want to project typed command or option objects into configuration overrides.
- You need a declarative catalog for selecting infrastructure dependencies by profile, environment, or backend mode.

## Example

```csharp
using Cohesive.Configuration;

var projection = new ConfigurationProjection<TrainCommand, RuntimeSettings>("Training");
projection.Map(x => x.ConnectionString, x => x.Storage.Default.ConnectionString);
projection.Set(false, x => x.EnableDemoDefinitions);

var overrides = projection.Build(new TrainCommand(
    ConnectionString: "UseDevelopmentStorage=true"));
```

## Related Packages

- `Cohesive.Cli` for typed command binding that can flow into configuration projection.
- `Cohesive.Host` for optional generic-host lifecycle and dependency-injection integration.
- `Cohesive.Adapters.AzureAppConfiguration` for Azure App Configuration integration.

## Portable scoped selection

`SelectionLayer<TChoice,TPolicy>` is a data-only authoring source for an atomic choice and mandatory
policies. `Prefer` and `Enforce` snapshot their inputs. `Document` lowers the layer to canonical IR.
`ScopedSelection<TChoice,TPolicy>` resolves these documents using host-authenticated `SelectionGrant`s.

Precedence is Framework < Adapter < Profile < Local. Within a precedence tier, system < tenant < user.
Equal winning tiers diagnose ambiguity even when their values match. Publication authority is exact
issuer plus scope; local precedence additionally requires an override grant. Scope specificity grants
no authority. Policies from every applicable layer apply regardless of which default wins.

The domain interpreter validates a complete choice against the accumulated policies. The callback is
an interpretation dependency and never enters canonical IR. Integration policy interpretation belongs
in Integrations; host configuration providers and executable dependency factories retain their existing
roles. The new mechanism neither changes nor silently wraps those older APIs.

A snapshot retains the exact winner, requesting scope and all applicable contribution references in
canonical identity order. Losing defaults remain available for explanation. `Inspect` reproduces the
snapshot from its retained dependency closure; it verifies integrity, not authenticated authorship.
`CheckDispatch` checks the original choice against current policies without applying newer defaults.
The host must supply complete, fresh policy documents and authenticated grants, and synchronize the
check with each new-effect boundary. No identity-provider adapter or distributed revocation protocol is
implied. Reconciliation of an already-issued effect keeps its original identity and is distinct from new
permission to dispatch.

The first consumer selects provider/connection/batch as an atomic unit. Per-field merge policies,
policy exceptions, and a universal constraint language are intentionally absent. Explicit overrides
cannot waive a mandatory policy. Source maps, exact revisions and structured diagnostics support
inspection and agent-authored changes; source C# builders remain the edit surface.
