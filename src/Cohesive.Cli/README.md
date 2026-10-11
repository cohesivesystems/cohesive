# Cohesive.Cli

`Cohesive.Cli` provides reusable typed command composition for Cohesive tools without requiring the broader
application-host, storage, transition, process, or relation packages.

## Typed commands

```csharp
using Cohesive.Cli;

var app = new CliApplication(description: "Training jobs");

app.Command<TrainCommand>("train", "Start a training run")
    .OnExecute(context =>
    {
        var command = context.Configuration;
        context.Io.WriteLine($"Training {command.Model} on {command.Dataset}");
        return 0;
    });

return await app.RunAsync(args);
```

Command-line values, environment variables, and registered configuration providers merge through
`Cohesive.Configuration` before binding to the command configuration. The same command tree owns generated help,
validation, middleware, output routing, cancellation, dynamic handler binding, and invocation diagnostics.

## Explicit declarations and root commands

Small tools can declare options directly without a reflected configuration class:

```csharp
using Cohesive.Cli;

var app = new CliApplication(
    description: "View historical bars from the Parquet data catalog");

app.RootCommand(new Dictionary<string, CliOption>
    {
        ["instrument"] = CliOption.For(
            defaultValue: "AMD.XNAS",
            environmentVariable: "IB_INSTRUMENT",
            description: "Instrument identifier to query"),
        ["limit"] = CliOption.For(defaultValue: 20, description: "Number of bars to display")
    })
    .OnExecute(context =>
    {
        var instrument = context.Configuration.Get<string>("instrument");
        var limit = context.Configuration.Get<int>("limit");
        // Query the catalog.
        return 0;
    });

return await app.RunAsync(args);
```

Invoke this program with `--instrument AMD.XNAS --limit 20`, or without arguments to use defaults.
`--help` displays generated help without executing the handler. Typed configurations also support
root execution through `app.RootCommand<ViewBarsOptions>()`.

Use `app.Command("bars", options: declarations)` or `command.SubCommand("bars", options: declarations)`
for named commands. Typed and explicit commands can coexist in the same tree. A declaration key is an
unprefixed, flat CLI name (`limit` produces `--limit`). `command.Argument("instrument")` projects a
parameter into a positional argument. `command.Parameters` exposes the effective descriptors for either
surface. Explicit declarations support scalar conversion through `Cohesive.Configuration` and string arrays;
other collection types are currently rejected. Set `Required`, `ShortName`, and `AllowedValues` using
record initializers or `with` expressions. `CliOption.For<T>()` declares an option without a default.

Both surfaces use the same command construction, conversion, validation, middleware, I/O, cancellation,
and diagnostics. Explicit commands reuse `CliCommandBuilder<CliValues>`; property-selector mapping APIs
are unavailable on that surface and fail explicitly. Typed validators can consume `CliValues` and use
`Get<T>` to read declarations. Option dictionaries and mutable defaults are snapshotted at registration
and authoring respectively. `Get<T>` requires the exact declared type, throws for unknown names or type
mismatches, and returns the type's default for an absent optional value. String array reads are defensive copies.

Explicit defaults apply only when a parameter is absent from all higher-priority sources. Precedence is:
CLI values, explicit environment mappings, automatic environment configuration, command configuration,
application configuration, then declaration defaults. Environment mappings are read per invocation;
`WithoutEnvironmentVariables()` disables both automatic environment binding and explicit mappings.
A supplied collection replaces its declaration default as a whole. Existing configuration-provider merging
rules still apply between supplied sources. Empty or whitespace scalar values follow the existing
configuration parser's absent-value semantics and do not restore a declaration default.

Register at most one root command. With a root command, `RunAsync([])` executes its handler and
`ShouldHandle` returns true for all arguments, including empty input. Without one, empty input still displays
root help and `ShouldHandle` selects registered command names. Root options are local to root execution;
child commands bind their own declarations.

Reflection-based authoring and explicit declarations produce the same `ConfigurationParameterDescriptor`
model. `ConfigurationParameterParser.ParseValues` validates and converts direct descriptors without creating
synthetic CLR types; typed object binding consumes that same conversion mechanism. These are the current
runtime declaration APIs, not a versioned portable command IR or serialization format.

## Configuration provenance and declarative constraints

Constraints are data declarations evaluated against effective bound values from every source,
including defaults. Typed selectors capture parameter identity, so later configuration-key or CLI-name
mapping changes remain attached to the same property:

```csharp
app.Command<ViewBarsOptions>("bars")
    .RequirePositive(options => options.Limit)
    .RequireRange(options => options.Limit, maximum: 1000)
    .WithConfigurationExplanation()
    .OnExecute(context =>
    {
        // context.ConfigurationProvenance is an immutable, redaction-safe snapshot.
        context.WriteConfigurationExplanation();
        return 0;
    });
```

Explicit commands declare the same rules using configuration keys (not aliases):

```csharp
app.RootCommand(new Dictionary<string, CliOption>
    {
        ["limit"] = CliOption.For(defaultValue: 20),
        ["upload"] = CliOption.For(defaultValue: false),
        ["offline"] = CliOption.For(defaultValue: false),
        ["token"] = CliOption.For<string>() with { Sensitive = true }
    })
    .Constrain(CliConstraint.Positive("limit"))
    .Constrain(CliConstraint.Range("limit", maximum: 1000))
    .Constrain(CliConstraint.AtMostOne("upload", "offline"))
    .Constrain(CliConstraint.Requires("upload", "token"))
    .WithConfigurationExplanation()
    .OnExecute(context => 0);
```

`CliConstraint.ExactlyOne(...)` requires one selected value. Selection means nonempty text or collection,
true for a boolean, and non-null for other scalars; numeric zero is selected. Numeric positive and range
rules support 8–64-bit integral types and decimal, including nullable forms. Range endpoints are inclusive.
Absent optional numeric values are skipped; use `Required` when presence is mandatory. Floating-point
numeric rules are currently rejected rather than silently rounded into decimal bounds. Unknown parameter
references and unsupported rule/type combinations fail when registering constraints and are rechecked
when building the command. Existing typed `RequireExactlyOne` and standard-input exclusivity helpers
now produce the same declarative rules (`AtMostOneStandardInput` is also available directly). `command.Constraints` exposes resolved immutable declarations;
help displays their requirements using effective CLI names. Custom delegate validators remain available.

`context.ConfigurationProvenance` reports the effective display value and winning sources for each
parameter. It distinguishes command-line inputs, automatic and explicit environment binding, application
and command providers, explicit declaration defaults, CLR defaults, and absent optional explicit values.
Collections report each consumed child key's source, preserving existing provider merging behavior;
comma-separated scalar collection inputs have one origin. Values reflect bound semantics, not raw input
spelling. Provider identities use their CLR type and registration index, avoiding provider `ToString()`
implementations that may disclose credentials. Automatic environment origins identify the binding key
and configured prefix; explicitly mapped origins identify the actual declared variable name. CLR defaults
identify initialization or the type default; arbitrary constructor/property-initializer internals are not
traced. Ignored whitespace inputs report the effective fallback rather than claiming to supply a value.
Provenance is captured before middleware runs and is retained when host integrations derive a new context.
Manually constructed contexts have an empty provenance snapshot.

`WithConfigurationExplanation()` opts a command into `--explain-configuration`. The switch binds and
converts inputs, prints effective values and origins, then exits without running middleware, declarative
constraints, custom validators, or handlers. This permits inspecting configuration that fails a cross-option
constraint; missing required inputs and conversion failures still prevent explanation. The switch name is
reserved only when enabled. Programmatic explanation is always available through the context.

Mark sensitive typed properties with `[ConfigurationParameter(Sensitive = true)]` or
`command.Map(options => options.Token).AsSensitive()`. Explicit declarations use `Sensitive = true`.
The shared descriptor owns this policy. Explanations and built-in allowed-value diagnostics use
`ConfigurationParameterParser.RedactedValue`; allowed values are omitted from help for sensitive
parameters. Sensitive boolean values are consumed as text and converted by the shared binder so rejected
values cannot be echoed by System.CommandLine's boolean-token heuristic. Application handlers, custom
validators, and user-supplied descriptions own their own disclosure policy; raw configuration and typed
values remain available for normal execution.

These APIs provide invocation provenance and declarative runtime validation; a versioned portable command
IR and structured diagnostic codes remain separate work.

## Standard streams and cancellation

`CommandIo` is the single invocation-scoped authority for raw input and output streams, error output, UTF-8 text
adaptation, and JSON serialization policy. `CliApplication` defaults to `CommandIo.Console()` and places the same
instance on every `CliCommandContext`. Tests and embedded tools can use `CommandIo.Null(...)`, overriding only the
channels they need to feed or capture:

```csharp
var io = CommandIo.Null(
    standardInput: input,
    standardOutput: output,
    standardError: error);
var app = new CliApplication(description: "Artifact tool", io);

app.Command<ImportCommand>("import")
    .OnExecute(async context =>
    {
        var command = context.Configuration;
        var manifest = await context.Io.ReadUtf8TextAsync(command.InputPath, context.CancellationToken);
        await context.Io.WriteOutputAsync(
            command.OutputPath,
            (output, cancellationToken) => ExportAsync(manifest, output, cancellationToken),
            context.CancellationToken);
        return 0;
    });
```

`RunAsync` is the standard console entry point: empty arguments execute a registered root handler or display root help, and `Console.CancelKeyPress` is
attached only while the application is running. `InvokeAsync` remains the embedding and test entry point and does not
attach a process signal handler.

`CommandIo.ReadInputAsync` and `ReadUtf8TextAsync` select standard input when the path is `-` and otherwise scope a
file input stream. `WriteOutputAsync` similarly selects standard output or a file; file destinations are replaced
atomically after a successful write, while standard output necessarily streams directly. This consolidates path
routing without pretending the input and output ownership or failure contracts are identical.

## Validation

Typed validator method groups are inferred without a cast:

```csharp
command.Validate(ValidateImport);

static IReadOnlyList<string> ValidateImport(ImportCommand command) =>
    command.BatchSize > 0 ? [] : ["Batch size must be positive."];
```

Common cross-parameter constraints can derive their displayed option names from the command's effective parameter
metadata, including expression-based name overrides:

```csharp
app.Command<ManifestCommand>("manifest")
    .RequireExactlyOne(command => command.World, command => command.RelationshipWorld);

app.Command<VerifyCommand>("verify")
    .AllowStandardInputForAtMostOne(command => command.Manifest, command => command.JsonLines);
```

The standard-stream marker is owned by `CommandIo.StandardStreamPath`; applications do not need another
literal `"-"` constant.

Prefer declaring invocation dependencies as typed handler parameters. Command contexts also expose optional
`IServiceProvider` lookup for custom context implementations: `GetService` returns `null` when no runtime integration
has attached a provider, while `GetRequiredService` fails explicitly. Provider attachment is reserved for the CLI
runtime and trusted integrations rather than the public context constructors.

Use `Cohesive.Cli.Testing.CliApplicationTestHarness` to invoke a command tree with captured output channels. Add
`Cohesive.Host` only when a command needs host lifecycle and dependency-injection scope integration through
`Cohesive.Host.Cli.UseHostContext`.

## Explicit invocation policy

Boolean options accept a bare switch (`--json`) as true or an explicit value (`--json false`).
An omitted switch does not override a configured/default value, including a nullable boolean.
Allowed-value and required constraints still apply during configuration binding.

Applications requiring explicit inputs can call `WithoutEnvironmentVariables()` to disable automatic
environment binding. Registered configuration providers remain active. Calling
`WithEnvironmentVariablePrefix(...)` subsequently re-enables environment binding; the last call wins.
