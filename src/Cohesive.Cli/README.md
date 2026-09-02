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
        Console.WriteLine($"Training {command.Model} on {command.Dataset}");
        return 0;
    });

return await app.InvokeAsync(args);
```

Command-line values, environment variables, and registered configuration providers merge through
`Cohesive.Configuration` before binding to the command configuration. The same command tree owns generated help,
validation, middleware, output routing, cancellation, dynamic handler binding, and invocation diagnostics.

Prefer declaring invocation dependencies as typed handler parameters. Command contexts also expose optional
`IServiceProvider` lookup for custom context implementations: `GetService` returns `null` when no runtime integration
has attached a provider, while `GetRequiredService` fails explicitly. Provider attachment is reserved for the CLI
runtime and trusted integrations rather than the public context constructors.

Use `Cohesive.Cli.Testing.CliApplicationTestHarness` to invoke a command tree with captured output channels. Add
`Cohesive.Host` only when a command needs host lifecycle and dependency-injection scope integration through
`Cohesive.Host.Cli.UseHostContext`.
