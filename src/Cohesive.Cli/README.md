# Cohesive.Cli

`Cohesive.Cli` provides reusable typed command composition for Cohesive tools without requiring the broader
application-host, storage, transition, process, or relation packages.

## Typed commands

```csharp
using Cohesive.Cli;

var app = new CliApplication(description: "Training jobs")
    .UseConsoleCancellation();

app.Command<TrainCommand>("train", "Start a training run")
    .OnExecute(context =>
    {
        var command = context.Configuration;
        context.Output.WriteLine($"Training {command.Model} on {command.Dataset}");
        return 0;
    });

return await app.InvokeAsync(args);
```

Command-line values, environment variables, and registered configuration providers merge through
`Cohesive.Configuration` before binding to the command configuration. The same command tree owns generated help,
validation, middleware, output routing, cancellation, dynamic handler binding, and invocation diagnostics.

## Standard streams and cancellation

`CliApplication` defaults to the process console and places its raw input and output streams on every
`CliCommandContext`. Tests and embedded tools can supply caller-owned streams once when creating the application:

```csharp
var app = new CliApplication(
    description: "Artifact tool",
    standardInput: input,
    standardOutput: output,
    standardError: error);

app.Command<ImportCommand>("import")
    .OnExecute(async context =>
    {
        using var reader = CliStandardStreams.OpenUtf8Reader(context.StandardInput);
        await ImportAsync(reader, context.StandardOutput, context.CancellationToken);
        return 0;
    });
```

`UseConsoleCancellation()` attaches `Console.CancelKeyPress` only while an invocation is active, prevents immediate
process termination, and cancels the token exposed by the command context. Explicit invocation cancellation remains
linked to the same token.

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

The standard-stream marker is owned by `CliStandardStreams.StandardStreamPath`; applications do not need another
literal `"-"` constant.

Prefer declaring invocation dependencies as typed handler parameters. Command contexts also expose optional
`IServiceProvider` lookup for custom context implementations: `GetService` returns `null` when no runtime integration
has attached a provider, while `GetRequiredService` fails explicitly. Provider attachment is reserved for the CLI
runtime and trusted integrations rather than the public context constructors.

Use `Cohesive.Cli.Testing.CliApplicationTestHarness` to invoke a command tree with captured output channels. Add
`Cohesive.Host` only when a command needs host lifecycle and dependency-injection scope integration through
`Cohesive.Host.Cli.UseHostContext`.
