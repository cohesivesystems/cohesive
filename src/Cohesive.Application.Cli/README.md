# Cohesive.Application.Cli

Optional `Cohesive.Cli` commands for admitted application reports. Application, workers
and transport adapters do not acquire a CLI dependency from offline model activation.

```csharp
using Cohesive.Application.Cli;
var cli = new CliApplication("orders").AddApplicationExplanation((args, ct) => CreateReport(args));
```

`explain -- [native arguments]` activates only for execution, never help. The factory
must remain offline. Serialization finishes before output begins; failures use CLI error
handling. A pre-cancelled `InvokeAsync` returns exit code 1 without invoking the factory.
Native argument validation remains the factory's responsibility.

Migration: replace `--explain` with `explain`; reference this package and import
`Cohesive.Application.Cli`. There is no forwarding facade for the old Host command.
