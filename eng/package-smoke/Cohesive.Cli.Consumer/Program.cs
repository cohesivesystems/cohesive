using Cohesive.Cli;
using Cohesive.Cli.Testing;
using Cohesive.Configuration;

EchoCommand? captured = null;
var app = new CliApplication("Package smoke");
app.Command<EchoCommand>("echo", "Echo one value")
    .OnExecute((CliCommandContext<EchoCommand> context) =>
    {
        captured = context.Configuration;
        context.Output.WriteLine(context.Configuration.Value);
        return 0;
    });

var invocation = await CliApplicationTestHarness.InvokeAsync(app, ["echo", "--value", "ready"]);
return invocation.ExitCode == 0
       && invocation.StandardOutput.Trim() == "ready"
       && invocation.ErrorOutput.Length == 0
       && captured?.Value == "ready"
    ? 0
    : 1;

sealed class EchoCommand
{
    [ConfigurationParameter("value", CliKey = "value", Required = true)]
    public string Value { get; init; } = string.Empty;
}
