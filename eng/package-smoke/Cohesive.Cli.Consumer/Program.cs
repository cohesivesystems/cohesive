using System.Text;
using Cohesive.Cli;
using Cohesive.Configuration;

EchoCommand? captured = null;
var streamsMatched = false;
await using MemoryStream standardInput = new(Encoding.UTF8.GetBytes("fixture"));
await using MemoryStream standardOutput = new();
using StringWriter standardError = new();
var app = new CliApplication(
        description: "Package smoke",
        standardInput: standardInput,
        standardOutput: standardOutput,
        standardError: standardError)
    .UseConsoleCancellation();
app.Command<EchoCommand>("echo", "Echo one value")
    .RequireExactlyOne(command => command.Value, command => command.AlternateValue)
    .Validate(ValidateEcho)
    .OnExecute((CliCommandContext<EchoCommand> context) =>
    {
        captured = context.Configuration;
        streamsMatched = ReferenceEquals(standardInput, context.StandardInput)
            && ReferenceEquals(standardOutput, context.StandardOutput);
        using var reader = CliStandardStreams.OpenUtf8Reader(context.StandardInput);
        context.Output.WriteLine($"{context.Configuration.Value}:{reader.ReadToEnd()}");
        return 0;
    });

var exitCode = await app.InvokeAsync(["echo", "--value", "ready"]);
return exitCode == 0
       && Encoding.UTF8.GetString(standardOutput.ToArray()).Trim() == "ready:fixture"
       && standardError.ToString().Length == 0
       && captured?.Value == "ready"
       && streamsMatched
    ? 0
    : 1;

static IReadOnlyList<string> ValidateEcho(EchoCommand command) =>
    command.Value.Length <= 20
        ? []
        : ["Echo value is too long."];

sealed class EchoCommand
{
    [ConfigurationParameter("value", CliKey = "value")]
    public string Value { get; init; } = string.Empty;

    [ConfigurationParameter("alternate-value", CliKey = "alternate-value")]
    public string AlternateValue { get; init; } = string.Empty;
}
