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
Public `required ... { get; init; }` properties also declare dependencies on every model declaration,
including inherited required properties. Constructor and property injection may coexist on the same model.
The optional callback
accepts ordinary `IServiceCollection` singleton registrations for interfaces/factories.

Both sets of dependencies complete before a model is constructed. The activator passes constructor
dependencies, assigns required init properties, then completes authoring before exposing the model to consumers.
Use constructor parameters when authoring in the constructor; use property dependencies during
`IAuthoringModel.Complete()` or definition preparation triggered by completion.
Reflection cannot prevent arbitrary constructor code from reading properties early: constructor failures
report this initialization boundary and preserve the original exception. Constructors must not read init properties; use
constructors for models that actively author declarations from dependencies. No partially populated model
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
models resolve properties through the same singleton registry as constructor parameters. Explicit factory
or instance registrations retain responsibility for initialization: their values are not overwritten, but
null required components are rejected before model completion. Required fields, mutable setters,
non-public accessors and value-type component properties are unsupported. Init modifiers are recognized
by full metadata name, including library-local IsExternalInit polyfills.

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

### Explicit environment policies

Mark environment-selected dependencies with `[ExplicitModelRegistration]` to prevent model
auto-discovery from silently constructing defaults. Register an instance, factory or type in
the activation services before `ModelActivator.Create`. Ordinary model dependencies still
support constructor and required-init discovery. The marker validates registration, not the
contents of a policy; its owner validates those values.

## Offline application explanation

`ApplicationExplanation.Create(application, infrastructure, deployment, environmentName, deploymentSource)`
admits the application and returns an immutable `ApplicationExplanationDocument`. It rejects incomplete
or foreign infrastructure associations and storage admission failures before producing a report.
The document retains original canonical entities, portable endpoint implementation links, query and
execution documents, and the validated deployment artifact. Existing documents retain their own
fingerprint authority. `SchemaVersion` versions only the report structure; `Scope` is explicitly
`OfflineAdmission`, never live readiness.

History is collected from contributing native bindings implementing `IStorageHistoryBinding`.
The PostgreSQL binding projects the authority and reconstitution of its declared history attachment,
with its existing database identity. No clients, credentials, connections or native preparation are
required. A composition without a history attachment contributes no history entry.

`ApplicationExplainCommand.TryWrite(writer, () => report)` evaluates its typed factory only when
`--explain` is supplied, serializes the complete report before writing, and leaves other host arguments
untouched. `report.ToJson()` uses the existing portable document serializers directly; it does not
serialize and parse embedded documents into mutable JSON trees. The output is an inspection report,
not a supported application import format or an independent executable system IR.
