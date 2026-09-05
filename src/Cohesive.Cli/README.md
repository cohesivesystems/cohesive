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

`RunAsync` is the standard console entry point: empty arguments display root help, and `Console.CancelKeyPress` is
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
