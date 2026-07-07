# Cohesive.Host

Host-level helpers for command-line applications, runtime configuration, and transition/process integration.

## Install

```bash
dotnet add package Cohesive.Host
```

## Use When

- You want to build typed CLI command trees with Cohesive configuration binding.
- You need shared host abstractions for running Cohesive transitions or processes in an application host.
- You want command arguments, environment variables, and configuration providers to flow through a single typed path.

## Example

```csharp
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

- `Cohesive.Configuration` for profile and projection support.
- `Cohesive.Processes` and `Cohesive.Transitions` for semantic runtime models.
