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
