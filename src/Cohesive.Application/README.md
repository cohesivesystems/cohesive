# Cohesive.Application

Offline application model composition, activation, validation and explanation. This package owns the
prepared semantic/realization graph; [Cohesive.Host](../Cohesive.Host/README.md) owns runtime lifecycle.
Microsoft.Extensions.DependencyInjection is the standard construction mechanism. No host, worker,
native client or schema is started implicitly.

```csharp
using Cohesive.Application;

var model = ModelActivator.Create<OrdersApplication>();
```

## Migration from Cohesive.Host

Reference `Cohesive.Application` and replace `using Cohesive.Host.Composition` with
`using Cohesive.Application`. No forwarding facade is retained. Keep Host when using its runtime
workers, scoped CLI host integration or failure logging. The custom `ApplicationExplainCommand`
was removed: reference the optional `Cohesive.Application.Cli` package and register `AddApplicationExplanation`
(`using Cohesive.Application.Cli`) on `CliApplication`, and replace `--explain`
with `explain` (native arguments follow `--`). Application has no dependency on Cli or System.CommandLine. The optional integration depends on both.

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

`cli.AddApplicationExplanation((hostArguments, cancellationToken) => report)` registers a named
`explain` command through `Cohesive.Cli`. Use `explain --help` for generated help and
`explain -- --EntityState:Authority=Events` to pass native configuration after the standard delimiter.
The command constructs the model only during execution, honors cancellation, and serializes the
complete report before writing through `CommandIo`. Help never activates a model. Native argument values are passed through, not validated by the explain command;
the native configuration builder owns their interpretation. The delimiter prevents native flags such as
`--help` from being consumed by the explain command.
`cli.RunOrFallbackAsync(args, runHost)` passes normal startup arguments unchanged to the native host.
Use `WithoutEnvironmentVariables()` on a dedicated AppHost CLI so native configuration is resolved
only by the native builder, rather than the CLI's optional environment parameter binding. `report.ToJson()` uses the existing portable document serializers directly; it does not
serialize and parse embedded documents into mutable JSON trees. The output is an inspection report,
not a supported application import format or an independent executable system IR.
