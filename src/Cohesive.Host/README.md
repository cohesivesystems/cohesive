# Cohesive.Host

Host-level helpers for runtime configuration, hosted CLI execution, and transition/process integration.

## Install

```bash
dotnet add package Cohesive.Host
```

## Use When

- You want to attach a `Cohesive.Cli` command to generic-host lifecycle and dependency-injection scopes.
- You need shared host abstractions for running Cohesive transitions or processes in an application host.
- You want command arguments, environment variables, and configuration providers to flow through a single typed path.

## Example

```csharp
using Cohesive.Cli;
using Cohesive.Host.Cli;

var app = new CliApplication(description: "Training jobs");

app.Command<TrainCommand>("train", "Start a training run")
    .OnExecute(context =>
    {
        var command = context.Configuration;
        Console.WriteLine($"Training {command.Model} on {command.Dataset}");
        return 0;
    });

return await app.InvokeAsync(args);
```

## Related Packages

- `Cohesive.Cli` for provider-neutral typed command composition and invocation.
- `Cohesive.Configuration` for profile and projection support.
- `Cohesive.Api.Execution` for prepared service processes and their scoped failure subscriptions.
- `Cohesive.Processes` and `Cohesive.Transitions` for semantic runtime models.

## Private process failure logging

For a prepared `HostedServiceProcess`, explicitly opt into protected operator logging before building the host:

```csharp
using Cohesive.Host.Services;

builder.Services.AddCohesiveTransitionFailureLogging(process);
```

The native host owns the subscription: it starts on host startup and is released on stop or container
disposal, even when `StopAsync` was never called. An unstarted host never subscribes. Repeated registration
of the same process creates one subscription per host; sharing a process across hosts does not share
subscription ownership. Route mapping remains independent of diagnostics.

The category is `TransitionFailureLoggingExtensions.LoggerCategory`. Source-generated logging emits
at Debug and skips message argument construction when disabled. These messages contain private native identities, concurrency tokens and
provider detail: use protected sinks. Portable process failures remain sanitized. Recoverable observer
failures increment the tag-free `cohesive.execution.diagnostic.subscriber.failures` counter; listening
only to that counter does not enable execution telemetry on other paths.

`StopAsync` releases subscriptions. Starting the logging service again creates one fresh subscription per
process; repeated starts while active do not duplicate delivery. This service lifecycle does not promise
that every native host or its other hosted services support restarting. Generic-host tests cover startup,
stop/start, disposal without stop, duplicate registration, disabled logging and independent hosts.

## Offline application composition

`ModelActivator.Create<T>()` discovers concrete constructor dependencies and prepares one offline graph.
For `ApplicationModel` roots, public `required ... { get; init; }` properties also declare dependencies,
including inherited required properties. Other models retain constructor injection. The optional callback
accepts ordinary `IServiceCollection` singleton registrations for interfaces/factories.

Dependencies complete before the root is constructed. The activator then assigns its required init
properties before authoring completion or publication. Constructors must not read init properties; use
constructors for models that actively author declarations from dependencies. No partially populated root
is exposed to resolve a cycle. Property metadata is retained only within the composition builder, and
reflection-based assignment runs once during activation, never per invocation. No source generator,
explicit Prepare call or runtime service container is required.

```csharp
public sealed class OrdersApplication : ApplicationModel
{
    public required OrdersDomain Domain { get; init; }
    public required OrdersQueries Queries { get; init; }
}
```

The direct builder still requires explicit registrations for all dependencies. Automatically constructed
roots resolve properties through the same singleton registry as constructor parameters. Explicit factory
or instance registrations retain responsibility for initialization: their values are not overwritten, but
null required components are rejected before model completion. Required fields, mutable setters,
non-public accessors and value-type component properties are unsupported.

The underlying `ApplicationModelBuilder` also supports direct explicit registration. `Build()` returns
`PreparedApplicationModel`, with shared instances and a validated canonical execution catalog. The optional
`ApplicationModel` root exposes this through `Composition` after activation. DI handles construction and
singleton sharing; `IAuthoringModel` completion and semantic validation remain a shared lifecycle concern.
Missing interfaces, cycles, duplicate revisions, unsupported lifetimes and disposable resources fail at setup.
Factories perform no IO and must not retain the temporary provider. Constructor selection is intentionally
strict for discovery: one public constructor, or an explicit factory. Models and prepared values are scoped
to one activation; failure caching belongs to the direct builder/authoring instance.

## Finite host-owned process execution

`AddCohesiveHostProcessWorker(prepared, maximumExecutions)` attaches prepared native process bindings to the
.NET host. It does not start during construction. Admission and checkpoints use the existing in-memory
execution adapter and process runtime; the worker supplies bounded scheduling and host cancellation only.
It declares Host lifetime, never Durable. Request disconnects do not cancel admitted work; host exit loses
receipt/result history. Capacity includes completed instances, with no eviction or automatic retry. For the
HTTP composition, `BindHosted` prepares and registers this provider at ServiceApiBindingsBuilder.Build, leaving
AppHost semantic inspection offline. See [the protocol](../../docs/design/accepted-process-api.md).
